// 端末を調べて 1 行 1 項目で出す: Android の版と機種、Camera2 のカメラの一覧、video/avc のエンコーダーの一覧。
// カメラを開かないので、カメラの権限は要らない。1 台・1 個ずつ try で包み、落ちても次へ進む。
// API 24 で使える呼び出しだけを使う(定数は API の版に依らないよう数字で比べる)。
package io.github.takosasidev.campane.probe

import android.content.Context
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.os.Build
import android.util.Size

class Inspector(private val context: Context, private val out: (String) -> Unit) {

    fun run() {
        out("=== 端末 ===")
        out("Android: ${Build.VERSION.RELEASE}")
        out("API: ${Build.VERSION.SDK_INT}")
        out("機種: ${Build.MANUFACTURER} ${Build.MODEL}(${Build.DEVICE})")
        cameras()
        encoders()
    }

    private fun cameras() {
        out("=== カメラ ===")
        val manager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
        val ids = try {
            manager.cameraIdList
        } catch (e: Exception) {
            out("カメラの一覧を取れなかった: $e")
            return
        }
        out("カメラの数: ${ids.size}")
        for (id in ids) {
            try {
                camera(id, manager.getCameraCharacteristics(id))
            } catch (e: Exception) {
                out("カメラ $id: 読めなかった: $e")
            }
        }
    }

    private fun camera(id: String, c: CameraCharacteristics) {
        out("カメラ $id")
        out("  向き: ${facingName(c.get(CameraCharacteristics.LENS_FACING))}")
        out("  ライト: ${if (c.get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true) "あり" else "なし"}")
        out("  ハードウェアのレベル: ${hardwareLevelName(c.get(CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL))}")
        out("  センサーの向き: ${c.get(CameraCharacteristics.SENSOR_ORIENTATION)}°")
        val map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
        out("  大きさ(SurfaceTexture): ${sizes(map?.getOutputSizes(SurfaceTexture::class.java))}")
        out("  大きさ(MediaCodec): ${sizes(map?.getOutputSizes(MediaCodec::class.java))}")
        val ranges = c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)
        out("  AE のフレームレートの範囲: ${ranges?.joinToString(", ") { "[${it.lower}, ${it.upper}]" } ?: "なし"}")
    }

    private fun sizes(list: Array<Size>?): String =
        list?.joinToString(", ") { "${it.width}x${it.height}" } ?: "なし"

    private fun encoders() {
        out("=== H.264 のエンコーダー ===")
        val infos = try {
            MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { info ->
                info.isEncoder && info.supportedTypes.any { it.equals(MIME, ignoreCase = true) }
            }
        } catch (e: Exception) {
            out("エンコーダーの一覧を取れなかった: $e")
            return
        }
        out("エンコーダーの数: ${infos.size}")
        for (info in infos) {
            try {
                encoder(info)
            } catch (e: Exception) {
                out("エンコーダー ${info.name}: 読めなかった: $e")
            }
        }
    }

    private fun encoder(info: MediaCodecInfo) {
        out("エンコーダー: ${info.name}")
        val caps = info.getCapabilitiesForType(MIME)
        out("  プロファイルとレベル: ${caps.profileLevels.joinToString(", ") { "${ProbeFormat.profileName(it.profile)} / ${ProbeFormat.levelName(it.level)}" }}")
        val surface = caps.colorFormats.contains(MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
        out("  COLOR_FormatSurface: ${if (surface) "あり" else "なし"}")
        val video = caps.videoCapabilities
        for ((w, h) in listOf(1920 to 1080, 1280 to 720, 640 to 480)) {
            for (fps in listOf(30, 15)) {
                out("  ${w}x$h ${fps}fps: ${if (video.areSizeAndRateSupported(w, h, fps.toDouble())) "可" else "不可"}")
            }
        }
        out("  ビットレートの範囲: ${video.bitrateRange.lower} 〜 ${video.bitrateRange.upper} bps")
    }

    companion object {
        const val MIME = "video/avc"

        fun facingName(value: Int?): String = when (value) {
            0 -> "前面"
            1 -> "背面"
            2 -> "外部"
            else -> "不明($value)"
        }

        fun hardwareLevelName(value: Int?): String = when (value) {
            0 -> "LIMITED"
            1 -> "FULL"
            2 -> "LEGACY"
            3 -> "LEVEL_3"
            4 -> "EXTERNAL"
            else -> "不明($value)"
        }
    }
}
