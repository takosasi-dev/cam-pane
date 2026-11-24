// 背面カメラ(無ければ最初のカメラ)の映像を、GL 経由か直接でエンコーダーの入力の Surface に流して 5 秒圧縮し、生の H.264(Annex B)で保存する。
// 1280x720・30 fps が駄目なら理由を出して 640x480 で 1 回やり直す。失敗・中断した回のファイルは消す。
// 作業スレッドで run() を呼ぶ。cancel() は UI スレッドから呼んでよい(カメラを閉じ、GL の片付けを頼むだけ。待たない)。
package io.github.takosasidev.campane.probe

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureFailure
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.TotalCaptureResult
import android.graphics.SurfaceTexture
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecInfo.CodecProfileLevel
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Range
import android.view.Surface
import java.io.File
import java.io.FileOutputStream
import java.util.Date
import java.util.Locale
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

class EncodeProbe(private val context: Context, private val path: EncodePath, private val out: (String) -> Unit) {

    private class ProbeError(message: String) : Exception(message)
    private class Cancelled : Exception()

    /** 出力を数えた結果。 */
    private class Stats {
        var frames = 0
        var keyFrames = 0
        var configs = 0
        var firstIsKey: Boolean? = null
        var ptsBackwards = 0
        var noStartCode = 0
        var firstPts = -1L
        var lastPts = -1L
        var eos = false
    }

    /** 撮影の結果の数(カメラのスレッドで数え、作業スレッドで読む)。 */
    private class CaptureStats {
        @Volatile var started = -1L
        val failed = AtomicInteger()
        val completed = AtomicInteger()
        val firstCompleted = AtomicLong(-1)

        fun callback() = object : CameraCaptureSession.CaptureCallback() {
            override fun onCaptureCompleted(session: CameraCaptureSession, request: CaptureRequest, result: TotalCaptureResult) {
                if (completed.getAndIncrement() == 0) firstCompleted.set(SystemClock.elapsedRealtime() - started)
            }

            override fun onCaptureFailed(session: CameraCaptureSession, request: CaptureRequest, failure: CaptureFailure) {
                failed.incrementAndGet()
            }
        }
    }

    @Volatile private var cancelled = false
    @Volatile private var camera: CameraDevice? = null
    @Volatile private var bridge: GlBridge? = null

    /** 画面が裏に回ったときに呼ぶ。カメラを閉じ、GL の片付けを頼む。エンコーダーと GL の待ちは run() の finally で行う。 */
    fun cancel() {
        cancelled = true
        try {
            camera?.close()
        } catch (e: Exception) {
            out("カメラを閉じるときに例外: $e")
        }
        bridge?.release(wait = false)
    }

    fun run() {
        out("=== 5 秒の圧縮 ===")
        out("経路: ${path.label}")
        try {
            val dir = context.getExternalFilesDir(null) ?: throw ProbeError("保存先(getExternalFilesDir)が使えない")
            val manager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
            val id = chooseCamera(manager)
            for ((w, h) in listOf(1280 to 720, 640 to 480)) {
                if (cancelled) throw Cancelled()
                out("--- ${w}x$h・30 fps で試す ---")
                try {
                    attempt(manager, id, w, h, dir)
                    break
                } catch (e: Exception) {
                    if (cancelled || e is Cancelled) throw Cancelled()
                    out("${w}x$h でできなかった: ${reason(e)}")
                }
            }
        } catch (e: Exception) {
            if (cancelled || e is Cancelled) out("中断した(画面が裏に回った)") else out("失敗: ${reason(e)}")
        } finally {
            out("--- 終わり ---")
        }
    }

    private fun reason(e: Exception): String = if (e is ProbeError) e.message ?: "" else e.toString()

    private fun chooseCamera(manager: CameraManager): String {
        val ids = manager.cameraIdList
        if (ids.isEmpty()) throw ProbeError("カメラが 1 台も無い")
        val back = ids.firstOrNull { manager.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING) == 1 }
        val id = back ?: ids[0]
        out("使うカメラ: $id(${if (back != null) "背面" else "背面が無いので最初のカメラ"})")
        return id
    }

    private fun attempt(manager: CameraManager, id: String, w: Int, h: Int, dir: File) {
        val chars = manager.getCameraCharacteristics(id)
        val map = chars.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
            ?: throw ProbeError("カメラに出せる大きさの表が無い")
        val target = if (path == EncodePath.GL) SurfaceTexture::class.java else MediaCodec::class.java
        if (map.getOutputSizes(target)?.any { it.width == w && it.height == h } != true) {
            throw ProbeError("カメラが ${target.simpleName} 向けに ${w}x$h を出せない(getOutputSizes に無い)")
        }
        val fps = chooseFpsRange(chars)
        out("AE のフレームレートの範囲: ${fps?.let { "[${it.lower}, ${it.upper}]" } ?: "30 を含む物が無いので指定しない"}")

        val thread = HandlerThread("CamPaneProbeCamera").apply { start() }
        val handler = Handler(thread.looper)
        val file = File(dir, ProbeFormat.fileName(Date()))
        var codec: MediaCodec? = null
        var surface: Surface? = null
        var session: CameraCaptureSession? = null
        var saved = false
        val capture = CaptureStats()
        var capturePrinted = false
        fun printCapture() {
            if (capturePrinted || capture.started < 0) return
            capturePrinted = true
            out("撮影の失敗(onCaptureFailed)の回数: ${capture.failed.get()}")
            out("撮影の完了(onCaptureCompleted)の回数: ${capture.completed.get()}")
            val first = capture.firstCompleted.get()
            out("最初の onCaptureCompleted まで: ${if (first < 0) "来なかった" else "$first ms"}")
            bridge?.let {
                out("GL で描いた枚数: ${it.drawn.get()}")
                out("GL で描けなかった回数: ${it.failed.get()}${it.firstError?.let { e -> "(最初: $e)" } ?: ""}")
            }
        }
        try {
            val (c, requested) = createEncoder(w, h)
            codec = c
            val input = c.createInputSurface()
            surface = input
            c.start()
            val cameraTarget = if (path == EncodePath.GL) {
                val b = GlBridge(input, w, h)
                bridge = b
                if (cancelled) throw Cancelled()
                b.start()
            } else {
                input
            }
            val cam = openCamera(manager, id, handler)
            val live = createSession(cam, cameraTarget, handler)
            session = live
            val request = cam.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
                addTarget(cameraTarget)
                if (fps != null) set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, fps)
            }.build()

            val stats = Stats()
            val started = SystemClock.elapsedRealtime()
            FileOutputStream(file).use { stream ->
                capture.started = SystemClock.elapsedRealtime()
                live.setRepeatingRequest(request, capture.callback(), handler)
                // 直接の経路は、2 秒で 1 枚も出なければ打ち切る(この端末の HAL が受けない例があった)。
                val noFrameDeadline = if (path == EncodePath.DIRECT) started + 2_000 else null
                drain(c, stream, stats, started + 5_000, noFrameDeadline)
                live.stopRepeating()
                live.close()
                session = null
                cam.close()
                camera = null
                bridge?.let { if (!it.release()) out("注意: GL の片付けが 2 秒で終わらなかった") }
                printCapture()
                c.signalEndOfInputStream()
                drain(c, stream, stats, SystemClock.elapsedRealtime() + 2_000)
            }
            val seconds = (SystemClock.elapsedRealtime() - started) / 1000.0
            if (!stats.eos) out("注意: 出し切り(EOS)が 2 秒で返らなかった")
            if (stats.frames == 0) throw ProbeError("フレームが 1 枚も出なかった")
            report(file, w, h, stats, seconds, c.name, requested)
            saved = true
        } finally {
            camera?.close()
            bridge?.let { if (!it.release()) out("注意: GL の片付けが 2 秒で終わらなかった") }
            printCapture()
            bridge = null
            try {
                session?.close()
            } catch (e: Exception) {
                out("セッションを閉じるときに例外: $e")
            }
            camera?.close()
            camera = null
            try {
                codec?.stop()
            } catch (e: Exception) {
                // 始まる前に落ちたときは stop が IllegalStateException を投げる。release はする。
            }
            codec?.release()
            surface?.release()
            thread.quitSafely()
            if (!saved && file.exists() && file.delete()) out("途中のファイルを消した: ${file.name}")
        }
    }

    /** 30 fps を上限に持つ範囲のうち、下限が一番高い物。古い HAL は 1000 倍の値を返すのでそれも受ける。 */
    private fun chooseFpsRange(chars: CameraCharacteristics): Range<Int>? =
        chars.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)
            ?.filter { it.upper == 30 || it.upper == 30_000 }
            ?.maxByOrNull { it.lower }

    /** Main を頼み、configure で断られたら作り直して Baseline を頼む。返すのは configure 済みの codec と、頼んだ物の文字。 */
    private fun createEncoder(w: Int, h: Int): Pair<MediaCodec, String> {
        val probe = MediaCodec.createEncoderByType(Inspector.MIME)
        val name = probe.name
        val caps = try {
            probe.codecInfo.getCapabilitiesForType(Inspector.MIME)
        } finally {
            probe.release()
        }
        out("使うエンコーダー: $name")
        if (!caps.videoCapabilities.areSizeAndRateSupported(w, h, 30.0)) {
            out("注意: エンコーダーは ${w}x$h・30 fps を受けないと言っている(areSizeAndRateSupported)。このまま試す")
        }
        val want = if (h >= 720) CodecProfileLevel.AVCLevel31 else CodecProfileLevel.AVCLevel3
        for (profile in listOf(CodecProfileLevel.AVCProfileMain, CodecProfileLevel.AVCProfileBaseline)) {
            val max = caps.profileLevels.filter { it.profile == profile }.maxOfOrNull { it.level }
            if (max == null && profile == CodecProfileLevel.AVCProfileMain) {
                out("Main はエンコーダーの一覧に無いので頼まない")
                continue
            }
            val level = if (max == null) want else minOf(want, max)
            val text = "${ProbeFormat.profileName(profile)} / ${ProbeFormat.levelName(level)}"
            val format = MediaFormat.createVideoFormat(Inspector.MIME, w, h).apply {
                setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
                setInteger(MediaFormat.KEY_BIT_RATE, 4_000_000)
                setInteger(MediaFormat.KEY_FRAME_RATE, 30)
                setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
                setInteger(MediaFormat.KEY_PROFILE, profile)
                setInteger(MediaFormat.KEY_LEVEL, level)
                // KEY_MAX_B_FRAMES は API 29 なので文字で入れる(古い端末は知らない鍵を無視する)。
                setInteger("max-bframes", 0)
            }
            val codec = MediaCodec.createByCodecName(name)
            try {
                codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                out("頼んだプロファイル: $text(configure が受けた)")
                return codec to text
            } catch (e: Exception) {
                codec.release()
                out("頼んだプロファイル: $text は configure で断られた: $e")
            }
        }
        throw ProbeError("Main も Baseline も configure できなかった")
    }

    private fun openCamera(manager: CameraManager, id: String, handler: Handler): CameraDevice {
        if (context.checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            throw ProbeError("カメラの権限が無い")
        }
        val latch = CountDownLatch(1)
        var device: CameraDevice? = null
        var error = ""
        val gaveUp = AtomicBoolean(false)
        manager.openCamera(id, object : CameraDevice.StateCallback() {
            override fun onOpened(d: CameraDevice) {
                if (cancelled || gaveUp.get()) {
                    d.close()
                } else {
                    device = d
                    camera = d
                }
                latch.countDown()
            }

            override fun onDisconnected(d: CameraDevice) {
                d.close()
                error = "切断された(ほかのアプリが使っているかもしれない)"
                latch.countDown()
            }

            override fun onError(d: CameraDevice, code: Int) {
                d.close()
                error = "onError $code(${cameraErrorName(code)})"
                latch.countDown()
            }
        }, handler)
        if (!latch.await(5, TimeUnit.SECONDS)) {
            gaveUp.set(true)
            throw ProbeError("カメラが 5 秒で開かなかった")
        }
        if (cancelled) throw Cancelled()
        return device ?: throw ProbeError("カメラを開けなかった: $error")
    }

    private fun createSession(cam: CameraDevice, surface: Surface, handler: Handler): CameraCaptureSession {
        val latch = CountDownLatch(1)
        var session: CameraCaptureSession? = null
        cam.createCaptureSession(listOf(surface), object : CameraCaptureSession.StateCallback() {
            override fun onConfigured(s: CameraCaptureSession) {
                session = s
                latch.countDown()
            }

            override fun onConfigureFailed(s: CameraCaptureSession) {
                latch.countDown()
            }
        }, handler)
        if (!latch.await(5, TimeUnit.SECONDS)) throw ProbeError("カメラのセッションが 5 秒で作れなかった")
        if (cancelled) throw Cancelled()
        return session ?: throw ProbeError("カメラのセッションを作れなかった(onConfigureFailed。エンコーダーの Surface だけでは駄目かもしれない)")
    }

    /** 期限まで(または EOS まで)出力を引き、BufferInfo の範囲だけをそのままファイルに書く。 */
    private fun drain(codec: MediaCodec, stream: FileOutputStream, s: Stats, deadline: Long, noFrameDeadline: Long? = null) {
        val info = MediaCodec.BufferInfo()
        while (SystemClock.elapsedRealtime() < deadline) {
            if (cancelled) throw Cancelled()
            if (noFrameDeadline != null && s.frames == 0 && SystemClock.elapsedRealtime() >= noFrameDeadline) {
                throw ProbeError("直接の経路ではフレームが来ない(2 秒で 0 枚)")
            }
            val index = codec.dequeueOutputBuffer(info, 10_000)
            if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                out("出力の形式: ${codec.outputFormat}")
                continue
            }
            if (index < 0) continue
            val buffer = codec.getOutputBuffer(index)
            if (buffer != null && info.size > 0) {
                val bytes = ByteArray(info.size)
                buffer.position(info.offset)
                buffer.limit(info.offset + info.size)
                buffer.get(bytes)
                stream.write(bytes)
                if (!startsWithCode(bytes)) s.noStartCode++
                if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                    s.configs++
                } else {
                    val key = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                    s.frames++
                    if (key) s.keyFrames++
                    if (s.firstIsKey == null) s.firstIsKey = key
                    if (s.lastPts >= 0 && info.presentationTimeUs < s.lastPts) s.ptsBackwards++
                    if (s.firstPts < 0) s.firstPts = info.presentationTimeUs
                    s.lastPts = info.presentationTimeUs
                }
            }
            codec.releaseOutputBuffer(index, false)
            if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) {
                s.eos = true
                return
            }
        }
    }

    private fun startsWithCode(b: ByteArray): Boolean =
        (b.size >= 3 && b[0].toInt() == 0 && b[1].toInt() == 0 && b[2].toInt() == 1) ||
            (b.size >= 4 && b[0].toInt() == 0 && b[1].toInt() == 0 && b[2].toInt() == 0 && b[3].toInt() == 1)

    private fun report(file: File, w: Int, h: Int, s: Stats, seconds: Double, codecName: String, requested: String) {
        val data = file.readBytes()
        val nals = ProbeFormat.countNals(data)
        val span = (s.lastPts - s.firstPts) / 1_000_000.0
        out("結果: 成功(${w}x$h・30 fps)")
        out("撮った経路: ${path.label}")
        out("出たフレームの数: ${s.frames}")
        out("キーフレームの数: ${s.keyFrames}")
        out("最初のフレームがキーフレームか: ${if (s.firstIsKey == true) "はい" else "いいえ"}")
        out("設定(CODEC_CONFIG)のバッファの数: ${s.configs}")
        out("合計のバイト数: ${data.size}")
        out("実際にかかった秒: ${String.format(Locale.US, "%.2f", seconds)}(撮り始めから出し切りまで)")
        if (s.frames > 1 && span > 0) {
            out("フレームの時刻の幅: ${String.format(Locale.US, "%.2f 秒(平均 %.1f fps)", span, (s.frames - 1) / span)}")
        }
        out("出力の時刻が戻った回数(B フレームの目安。0 であるべき): ${s.ptsBackwards}")
        out("開始コードで始まらないバッファの数(0 であるべき): ${s.noStartCode}")
        out("使ったエンコーダー: $codecName")
        out("頼んだプロファイル: $requested")
        out("SPS のプロファイル: ${ProbeFormat.spsProfile(data) ?: "SPS が見つからない"}")
        out("保存したパス: ${file.absolutePath}")
        out("先頭 32 バイト: ${ProbeFormat.hex(data, 32)}")
        out("NAL の数: ${nals.total}")
        out("NAL SPS(7): ${nals.sps}")
        out("NAL PPS(8): ${nals.pps}")
        out("NAL IDR(5): ${nals.idr}")
        out("NAL 非 IDR(1): ${nals.nonIdr}")
        out("NAL そのほか: ${nals.other}")
        out("最初の開始コードの前のバイト数(0 であるべき): ${nals.leading}")
        val slices = nals.idr + nals.nonIdr
        out("スライスの NAL(1+5)とフレームの数: ${if (slices == s.frames) "同じ($slices)" else "違う(スライス $slices / フレーム ${s.frames}。1 フレームが複数のスライスかもしれない)"}")
    }

    private fun cameraErrorName(code: Int): String = when (code) {
        CameraDevice.StateCallback.ERROR_CAMERA_IN_USE -> "ほかで使用中"
        CameraDevice.StateCallback.ERROR_MAX_CAMERAS_IN_USE -> "開けるカメラの数の上限"
        CameraDevice.StateCallback.ERROR_CAMERA_DISABLED -> "端末の方針で無効"
        CameraDevice.StateCallback.ERROR_CAMERA_DEVICE -> "カメラの故障"
        CameraDevice.StateCallback.ERROR_CAMERA_SERVICE -> "カメラのサービスの故障"
        else -> "不明"
    }
}
