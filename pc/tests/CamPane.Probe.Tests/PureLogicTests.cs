// Probe のうち、OS を呼ばない計算だけを確かめる: Annex B の区切り、絵の境目と溜め(遅れ)の数え方、SPS の読み書き、NV12 から BGRA への変換。
// Media Foundation を呼ぶ部分(復号とウィンドウ)は、実機で動かして確かめる物なので、ここには無い。
namespace CamPane.Probe.Tests;

public class PureLogicTests
{
    [Fact]
    public void 開始コードが_3_バイトでも_4_バイトでも区切る()
    {
        byte[] data = [0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3, 0, 0, 1, 0x65, 9];

        var nals = AnnexB.Split(data, out var leading);

        Assert.Equal(0, leading);
        Assert.Equal([new Nal(0, 6, 7), new Nal(6, 6, 8), new Nal(12, 5, 5)], nals);
        Assert.Equal(data.Length, nals.Sum(n => n.Length)); // 1 バイトも落とさない
    }

    [Fact]
    public void 先頭が_4_バイトの開始コードなら_0_から始まる()
    {
        var nals = AnnexB.Split([0, 0, 0, 1, 0x41, 7], out var leading);

        Assert.Equal(0, leading);
        Assert.Equal([new Nal(0, 6, 1)], nals);
        Assert.True(nals[0].IsSlice);
    }

    [Fact]
    public void 開始コードの前のごみは数えて捨てる()
    {
        var nals = AnnexB.Split([9, 9, 0, 0, 1, 0x41], out var leading);

        Assert.Equal(2, leading);
        Assert.Equal([new Nal(2, 4, 1)], nals);
    }

    [Fact]
    public void 開始コードが無ければ_0_個で_中身が空の_NAL_は型が_マイナス_1()
    {
        Assert.Empty(AnnexB.Split([1, 2, 3, 0, 0, 2], out _));
        Assert.Empty(AnnexB.Split([], out _));
        Assert.Equal([new Nal(0, 3, -1)], AnnexB.Split([0, 0, 1], out _));
    }

    [Fact]
    public void 一つの絵の_2_スライスを_first_mb_in_slice_でまとめ_遅れを数える()
    {
        // SPS・PPS の後に、2 スライスの絵が 2 枚。スライスの次の 1 バイトの最上位ビットが 1 なら first_mb_in_slice = 0(新しい絵)。
        byte[] data =
        [
            0, 0, 0, 1, 0x67, 1,
            0, 0, 0, 1, 0x68, 2,
            0, 0, 1, 0x65, 0x88,
            0, 0, 1, 0x65, 0x40,
            0, 0, 1, 0x41, 0x9A,
            0, 0, 1, 0x41, 0x40,
        ];

        var pictures = Latency.Pictures(data, AnnexB.Split(data, out _));

        Assert.Equal([new Picture(2, 3), new Picture(4, 5)], pictures);
        // 1 枚目は NAL を 5 個入れた所(2 枚目の最初のスライスの後)、2 枚目は 6 個入れた所で出た
        Assert.Equal([1, 0], Latency.Lags(pictures, [5, 6])!);
        // 最後のスライスを入れる前に出たことになる対応は、崩れているので数えない
        Assert.Null(Latency.Lags(pictures, [3]));
        Assert.Null(Latency.Lags(pictures, [5, 6, 6]));
        Assert.Equal([1, 0], Latency.PictureLags(pictures, [5, 6])!);

        // 1 枚分ずつ入れる回: SPS・PPS は次の絵の塊に入り、2 スライスは 1 つにまとまる
        Assert.Equal([new Nal(0, 22, 5), new Nal(22, 10, 1)], Latency.AccessUnits(data, AnnexB.Split(data, out _)));
    }

    // SPS のバイトだけ(絵は含まない)。dtab = 端末の撮った映像の先頭、ffmpeg = libx264 で合成の絵から作った物。
    private static readonly byte[] DtabSps = Convert.FromHexString("6742e033ab402802dc80");
    private static readonly byte[] MainSps = Convert.FromHexString("674d401fd9005005bb0110000003001000000303c0f1832480");
    private static readonly byte[] HighSps = Convert.FromHexString("6764000dacb20283f60220000003002000000781e28549");
    private static readonly byte[] High444Sps = Convert.FromHexString("67f4000d919b28283f6022000003000200000300781e28532c");

    [Fact]
    public void VUI_の無い_SPS_に並べ替えなしの_bitstream_restriction_を足し_読み直して合う()
    {
        var before = Sps.Parse(DtabSps);
        Assert.Equal((66, 0, 1, false), (before.ProfileIdc, before.PicOrderCntType, before.MaxNumRefFrames, before.Vui));

        var (nal, _) = Sps.AddReorderLimit(DtabSps);
        var after = Sps.Parse(nal!);

        Assert.Equal(before with { Vui = true, BitstreamRestriction = true, MaxNumReorderFrames = 0, MaxDecFrameBuffering = 1, RestrictionFlagBit = after.RestrictionFlagBit }, after);
    }

    [Theory]
    [InlineData("main", 77, 1, 3, 79, 44, 11, 0)]
    [InlineData("high", 100, 1, 3, 19, 14, 9, 0)]
    [InlineData("high444", 244, 3, 4, 19, 14, 9, 2)]
    public void VUI_のある_SPS_は_bitstream_restriction_だけを直し_ほかは_ビットのまま残す(
        string name, int profile, int chroma, int maxRef, int width, int height, int mvLength, int reorder)
    {
        var sps = name switch { "main" => MainSps, "high" => HighSps, _ => High444Sps };
        var before = Sps.Parse(sps);
        // 期待の値は ffmpeg の -bsf:v trace_headers で読んだ物
        Assert.Equal(
            (profile, chroma, maxRef, width, height, true, true, mvLength, reorder, maxRef),
            (before.ProfileIdc, before.ChromaFormatIdc, before.MaxNumRefFrames, before.WidthInMbsMinus1, before.HeightInMapUnitsMinus1,
             before.Vui, before.BitstreamRestriction, before.Log2MaxMvLengthHorizontal, before.MaxNumReorderFrames, before.MaxDecFrameBuffering));

        var (nal, _) = Sps.AddReorderLimit(sps);
        var after = Sps.Parse(nal!);

        Assert.Equal(before with { MaxNumReorderFrames = 0, MaxDecFrameBuffering = maxRef }, after);
        var whole = before.RestrictionFlagBit / 8;
        Assert.Equal(Sps.Unescape(sps)[..whole], Sps.Unescape(nal!)[..whole]);
    }

    [Fact]
    public void エミュレーション防止の_03_を外して_付け直すと元に戻る()
    {
        byte[] rbsp = [0x67, 0, 0, 0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0, 4, 9];

        var escaped = Sps.Escape(rbsp);

        Assert.Equal([0x67, 0, 0, 3, 0, 0, 3, 0, 1, 0, 0, 3, 2, 0, 0, 3, 3, 0, 0, 4, 9], escaped);
        Assert.Equal(rbsp, Sps.Unescape(escaped));
    }

    [Theory]
    [InlineData(235, 128, 128, 255, 255, 255)] // 白
    [InlineData(16, 128, 128, 0, 0, 0)]        // 黒
    [InlineData(81, 90, 240, 0, 0, 255)]       // 赤(BT.601)。B・G・R の順
    public void NV12_の_1_色を_BGRA_に直す(byte y, byte u, byte v, byte b, byte g, byte r)
    {
        // 2x2 の絵。Y が 4 つ、その後ろに U・V が 1 組
        byte[] nv12 = [y, y, y, y, u, v];
        var bgra = new byte[2 * 2 * 4];

        Nv12.ToBgra(nv12, new FrameLayout(2, 2, 2, 0, 0, 2, 2), bgra);

        Assert.Equal([b, g, r, 255, b, g, r, 255, b, g, r, 255, b, g, r, 255], bgra);
    }

    [Fact]
    public void ストライドの余白と_見せる範囲のずれを踏まえて写す()
    {
        // 符号化の大きさ 4x2、ストライド 6(右に余白 2)。見せる範囲は右半分の 2x2。
        // 左半分は黒、右半分は白。余白には、読んではいけない値(赤になる値)を入れておく。
        byte[] nv12 =
        [
            16, 16, 235, 235, 81, 81,
            16, 16, 235, 235, 81, 81,
            128, 128, 128, 128, 90, 240,
        ];
        var bgra = new byte[2 * 2 * 4];

        Nv12.ToBgra(nv12, new FrameLayout(4, 2, 6, 2, 0, 2, 2), bgra);

        Assert.All(bgra, value => Assert.Equal(255, value));
    }
}
