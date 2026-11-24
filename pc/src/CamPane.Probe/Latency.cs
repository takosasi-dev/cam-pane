// 復号の溜め(遅れ)を数える。OS は呼ばない。絵の境目は、スライスの first_mb_in_slice が 0 の所で見る。
// 遅れ = 「その絵の最後のスライスを入れてから、その絵が出るまでに入れた単位(NAL か 1 枚分のサンプル)の数」と、それを絵の数に直した値。
// B フレームが無い前提で、k 枚目の出力を k 枚目の絵とみなす。1 枚分ずつ入れる回のために、NAL を絵ごとの塊にまとめる関数も持つ。
namespace CamPane.Probe;

/// <summary>1 枚の絵の、最初と最後のスライスの NAL の番号(入れる順)。</summary>
internal readonly record struct Picture(int First, int Last);

internal static class Latency
{
    /// <summary>スライスの NAL を絵ごとにまとめる。</summary>
    internal static List<Picture> Pictures(ReadOnlySpan<byte> data, IReadOnlyList<Nal> nals)
    {
        var pictures = new List<Picture>();
        for (var i = 0; i < nals.Count; i++)
        {
            if (!nals[i].IsSlice)
            {
                continue;
            }

            if (pictures.Count == 0 || StartsPicture(data, nals[i]))
            {
                pictures.Add(new Picture(i, i));
            }
            else
            {
                pictures[^1] = pictures[^1] with { Last = i };
            }
        }

        return pictures;
    }

    // first_mb_in_slice は ue(v)。0 の符号は「1」の 1 ビットだけなので、NAL ヘッダーの次の 1 バイトの最上位ビットが 1 になる。
    // ASO(スライスの順の入れ替え)は Constrained Baseline には無いので、0 が来れば新しい絵。
    internal static bool StartsPicture(ReadOnlySpan<byte> data, Nal nal)
    {
        var bytes = data.Slice(nal.Offset, nal.Length);
        var header = bytes.IndexOf((byte)1) + 1; // 開始コードの 01 の次が NAL ヘッダー
        return header > 0 && header + 1 < bytes.Length && (bytes[header + 1] & 0x80) != 0;
    }

    /// <summary>
    /// 1 枚の絵の全スライスと、その前の SPS・PPS などを、1 つの塊(開始コードつきの続いたバイト列)にまとめる。
    /// 塊の Type は最初のスライスの型(スライスが無い塊は最初の NAL の型)。最後の絵は終わりで閉じる。
    /// </summary>
    internal static List<Nal> AccessUnits(ReadOnlySpan<byte> data, IReadOnlyList<Nal> nals)
    {
        var units = new List<Nal>();
        var first = 0;
        var slice = -1; // 今の塊の最初のスライスの番号
        for (var i = 0; i < nals.Count; i++)
        {
            // 塊にスライスが入った後に、スライスでない NAL か、新しい絵の最初のスライスが来たら、そこで閉じる。
            if (slice >= 0 && (!nals[i].IsSlice || StartsPicture(data, nals[i])))
            {
                units.Add(Join(nals, first, i - 1, slice));
                first = i;
                slice = -1;
            }

            if (slice < 0 && nals[i].IsSlice)
            {
                slice = i;
            }
        }

        if (first < nals.Count)
        {
            units.Add(Join(nals, first, nals.Count - 1, slice));
        }

        return units;
    }

    private static Nal Join(IReadOnlyList<Nal> nals, int first, int last, int slice) =>
        new(nals[first].Offset, nals[last].Offset + nals[last].Length - nals[first].Offset, nals[slice >= 0 ? slice : first].Type);

    /// <summary>
    /// 遅れを絵の数に直す: k 枚目が出るまでに、k 枚目より後の絵を何枚入れ始めていたか。対応が崩れていれば null(Lags と同じ)。
    /// </summary>
    internal static int[]? PictureLags(IReadOnlyList<Picture> pictures, IReadOnlyList<int> fedAtOutput)
    {
        if (Lags(pictures, fedAtOutput) is null)
        {
            return null;
        }

        return fedAtOutput.Select((fed, k) => pictures.Count(p => p.First < fed) - (k + 1)).ToArray();
    }

    /// <summary>
    /// fedAtOutput[k] は、k 枚目が出た時点でデコーダーに入れ終えていた NAL の数。各絵の遅れを返す。
    /// 出た枚数が絵の数を超える、または遅れが負(出た絵の最後のスライスをまだ入れていない)なら、対応が崩れているので null。
    /// </summary>
    internal static int[]? Lags(IReadOnlyList<Picture> pictures, IReadOnlyList<int> fedAtOutput)
    {
        if (fedAtOutput.Count > pictures.Count)
        {
            return null;
        }

        var lags = new int[fedAtOutput.Count];
        for (var k = 0; k < lags.Length; k++)
        {
            lags[k] = fedAtOutput[k] - (pictures[k].Last + 1);
            if (lags[k] < 0)
            {
                return null;
            }
        }

        return lags;
    }
}
