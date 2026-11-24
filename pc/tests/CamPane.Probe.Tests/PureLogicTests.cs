// Probe のうち、OS を呼ばない計算だけを確かめる: Annex B の区切りと、NV12 から BGRA への変換。
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
