// Android に依らない計算: 経路の選び方、Annex B を NAL に区切って種類を数える、SPS のプロファイルを読む、定数を名前にする、ファイル名を作る。
// android.* を import しない(JVM の単体テストで確かめるため)。
// 区切りの決まりは PC の CamPane.Probe の AnnexB.Split と同じにしている。
package io.github.takosasidev.campane.probe

import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/** NAL の種類ごとの数。PC の Probe の「NAL: ...」の行と見比べられるように同じ分け方にする。 */
data class NalCounts(
    val total: Int,
    val sps: Int,
    val pps: Int,
    val idr: Int,
    val nonIdr: Int,
    val other: Int,
    /** 最初の開始コードより前にあったバイト数(0 であるべき)。 */
    val leading: Int,
)

/** 5 秒の圧縮で、カメラの絵をエンコーダーへ渡す経路。 */
enum class EncodePath(val action: String, val label: String) {
    GL("encode", "GL 経由(カメラ → SurfaceTexture → EGL → エンコーダーの入力の Surface)"),
    DIRECT("encode-direct", "直接(カメラ → エンコーダーの入力の Surface)"),
}

object ProbeFormat {

    /** adb の --es action の値から経路を選ぶ。知らない値と無いときは null。 */
    fun encodePathOf(action: String?): EncodePath? = EncodePath.values().firstOrNull { it.action == action }

    /** 開始コード(00 00 01 と 00 00 00 01)で区切り、各 NAL の nal_unit_type と、最初の開始コードの前のバイト数を返す。中身が空の NAL は -1。 */
    fun nalTypes(data: ByteArray): Pair<List<Int>, Int> {
        val types = ArrayList<Int>()
        var leading = data.size
        var payload = -1 // 今の NAL の中身の先頭(まだ無ければ -1)
        var i = 0
        while (i + 2 < data.size) {
            if (!startsCode(data, i)) {
                i++
                continue
            }
            // 4 バイトの開始コードの先頭の 00 は、次の NAL の側(開始コード)に数える。
            val codeStart = if (i > maxOf(payload, 0) && data[i - 1].toInt() == 0) i - 1 else i
            if (payload >= 0) types.add(typeOf(data, payload, codeStart)) else leading = codeStart
            payload = i + 3
            i = payload
        }
        if (payload >= 0) types.add(typeOf(data, payload, data.size))
        return types to leading
    }

    private fun typeOf(data: ByteArray, payload: Int, end: Int): Int =
        if (payload < end) data[payload].toInt() and 0x1F else -1

    private fun startsCode(data: ByteArray, at: Int): Boolean =
        at + 2 < data.size && data[at].toInt() == 0 && data[at + 1].toInt() == 0 && data[at + 2].toInt() == 1

    fun countNals(data: ByteArray): NalCounts {
        val (types, leading) = nalTypes(data)
        return NalCounts(
            total = types.size,
            sps = types.count { it == 7 },
            pps = types.count { it == 8 },
            idr = types.count { it == 5 },
            nonIdr = types.count { it == 1 },
            other = types.count { it !in setOf(1, 5, 7, 8) },
            leading = leading,
        )
    }

    /** 最初の SPS から profile_idc と level_idc を読む。例: "profile_idc=77(Main) level_idc=31(3.1)"。SPS が無ければ null。 */
    fun spsProfile(data: ByteArray): String? {
        var i = 0
        while (i + 5 < data.size) {
            if (startsCode(data, i) && (data[i + 3].toInt() and 0x1F) == 7) {
                val profileIdc = data[i + 4].toInt() and 0xFF
                val constraint1 = (data[i + 5].toInt() and 0x40) != 0
                val levelIdc = if (i + 6 < data.size) data[i + 6].toInt() and 0xFF else -1
                val name = when (profileIdc) {
                    66 -> if (constraint1) "Constrained Baseline" else "Baseline"
                    77 -> "Main"
                    88 -> "Extended"
                    100 -> "High"
                    else -> "不明"
                }
                return "profile_idc=$profileIdc($name) level_idc=$levelIdc(${levelIdc / 10}.${levelIdc % 10})"
            }
            i++
        }
        return null
    }

    // 値は MediaCodecInfo.CodecProfileLevel の AVCProfile* と同じ(テストで突き合わせる)。
    private val profiles = linkedMapOf(
        0x01 to "Baseline", 0x02 to "Main", 0x04 to "Extended", 0x08 to "High",
        0x10 to "High10", 0x20 to "High422", 0x40 to "High444",
        0x10000 to "ConstrainedBaseline", 0x80000 to "ConstrainedHigh",
    )

    // 値は MediaCodecInfo.CodecProfileLevel の AVCLevel* と同じ。
    private val levels = linkedMapOf(
        0x01 to "1", 0x02 to "1b", 0x04 to "1.1", 0x08 to "1.2", 0x10 to "1.3",
        0x20 to "2", 0x40 to "2.1", 0x80 to "2.2",
        0x100 to "3", 0x200 to "3.1", 0x400 to "3.2",
        0x800 to "4", 0x1000 to "4.1", 0x2000 to "4.2",
        0x4000 to "5", 0x8000 to "5.1", 0x10000 to "5.2",
        0x20000 to "6", 0x40000 to "6.1", 0x80000 to "6.2",
    )

    fun profileName(value: Int): String = profiles[value] ?: "不明(0x${Integer.toHexString(value)})"

    fun levelName(value: Int): String = levels[value] ?: "不明(0x${Integer.toHexString(value)})"

    fun fileName(date: Date, zone: TimeZone = TimeZone.getDefault()): String {
        val format = SimpleDateFormat("yyyyMMdd_HHmmss", Locale.US)
        format.timeZone = zone
        return "campane_probe_${format.format(date)}.h264"
    }

    /** 先頭 count バイトを 2 桁の 16 進で空白区切りにする。 */
    fun hex(data: ByteArray, count: Int): String =
        data.take(count).joinToString(" ") { String.format(Locale.US, "%02x", it.toInt() and 0xFF) }
}
