// ProbeFormat の単体テスト。Annex B の区切り(3 バイトと 4 バイトの開始コード)、SPS の読み取り、
// プロファイルとレベルの名前(SDK の定数と突き合わせる)、ファイル名、16 進の表示を確かめる。
package io.github.takosasidev.campane.probe

import android.media.MediaCodecInfo.CodecProfileLevel
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.util.Date
import java.util.TimeZone

class ProbeFormatTest {

    private fun bytes(vararg v: Int) = ByteArray(v.size) { v[it].toByte() }

    // SPS(Main, 3.1) と PPS を 4 バイトの開始コードで、IDR を 3 バイト、IDR でない絵を 4 バイトで並べた物。
    private val stream = bytes(
        0, 0, 0, 1, 0x67, 77, 0x40, 31, 0xAA,
        0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80,
        0, 0, 1, 0x65, 0x88, 0x84,
        0, 0, 0, 1, 0x41, 0x9A, 0x00,
    )

    @Test
    fun countsNalTypesWithBothStartCodes() {
        val (types, leading) = ProbeFormat.nalTypes(stream)
        assertEquals(listOf(7, 8, 5, 1), types)
        assertEquals(0, leading)
        assertEquals(NalCounts(total = 4, sps = 1, pps = 1, idr = 1, nonIdr = 1, other = 0, leading = 0), ProbeFormat.countNals(stream))
    }

    @Test
    fun trailingZeroInPayloadIsNotSwallowedByThreeByteCode() {
        // 絵の終わりが 00 で、次が 3 バイトの開始コードのとき、その 00 は次の開始コードの側に数える(PC 側と同じ)。
        val (types, _) = ProbeFormat.nalTypes(bytes(0, 0, 1, 0x65, 0x10, 0, 0, 0, 1, 0x41, 0x20))
        assertEquals(listOf(5, 1), types)
    }

    @Test
    fun reportsLeadingBytesAndEmptyNal() {
        val (types, leading) = ProbeFormat.nalTypes(bytes(0x12, 0x34, 0, 0, 0, 1, 0, 0, 1, 0x06, 0x05))
        assertEquals(2, leading)
        assertEquals(listOf(-1, 6), types)
        val counts = ProbeFormat.countNals(bytes(0x12, 0x34, 0, 0, 0, 1, 0, 0, 1, 0x06, 0x05))
        assertEquals(2, counts.other)
    }

    @Test
    fun noStartCodeMeansNoNal() {
        val (types, leading) = ProbeFormat.nalTypes(bytes(1, 2, 3, 4))
        assertEquals(emptyList<Int>(), types)
        assertEquals(4, leading)
    }

    @Test
    fun readsProfileFromSps() {
        assertEquals("profile_idc=77(Main) level_idc=31(3.1)", ProbeFormat.spsProfile(stream))
        assertEquals(
            "profile_idc=66(Constrained Baseline) level_idc=30(3.0)",
            ProbeFormat.spsProfile(bytes(0, 0, 1, 0x67, 66, 0xC0, 30, 0)),
        )
        assertNull(ProbeFormat.spsProfile(bytes(0, 0, 1, 0x65, 1, 2, 3, 4)))
    }

    @Test
    fun namesMatchSdkConstants() {
        assertEquals("Baseline", ProbeFormat.profileName(CodecProfileLevel.AVCProfileBaseline))
        assertEquals("Main", ProbeFormat.profileName(CodecProfileLevel.AVCProfileMain))
        assertEquals("High", ProbeFormat.profileName(CodecProfileLevel.AVCProfileHigh))
        assertEquals("ConstrainedBaseline", ProbeFormat.profileName(CodecProfileLevel.AVCProfileConstrainedBaseline))
        assertEquals("ConstrainedHigh", ProbeFormat.profileName(CodecProfileLevel.AVCProfileConstrainedHigh))
        assertEquals("不明(0x1000000)", ProbeFormat.profileName(0x1000000))
        assertEquals("1b", ProbeFormat.levelName(CodecProfileLevel.AVCLevel1b))
        assertEquals("3.1", ProbeFormat.levelName(CodecProfileLevel.AVCLevel31))
        assertEquals("4.1", ProbeFormat.levelName(CodecProfileLevel.AVCLevel41))
        assertEquals("5.2", ProbeFormat.levelName(CodecProfileLevel.AVCLevel52))
        assertEquals("6.2", ProbeFormat.levelName(CodecProfileLevel.AVCLevel62))
    }

    @Test
    fun buildsFileName() {
        // 2026-11-24 13:05:09 UTC
        val date = Date(1795525509000L)
        assertEquals("campane_probe_20261124_130509.h264", ProbeFormat.fileName(date, TimeZone.getTimeZone("UTC")))
    }

    @Test
    fun formatsHex() {
        assertEquals("00 00 00 01 67", ProbeFormat.hex(stream, 5))
        assertEquals("ab", ProbeFormat.hex(bytes(0xAB), 32))
    }

    @Test
    fun choosesPathFromAction() {
        assertEquals(EncodePath.GL, ProbeFormat.encodePathOf("encode"))
        assertEquals(EncodePath.DIRECT, ProbeFormat.encodePathOf("encode-direct"))
        assertNull(ProbeFormat.encodePathOf("ENCODE"))
        assertNull(ProbeFormat.encodePathOf(null))
    }
}
