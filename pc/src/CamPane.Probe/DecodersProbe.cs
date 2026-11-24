// Media Foundation に登録されている H.264 のデコーダーを、同期・非同期・ハードウェアの区分ごとに列挙して出す(仕様書 §8.2)。
// 区分は MFTEnumEx のフラグで分ける。Learn では、どの MFT も 3 つの区分のちょうど 1 つに入り、ハードウェアは常に非同期とある。
using Windows.Win32.Media.MediaFoundation;

namespace CamPane.Probe;

internal static unsafe class DecodersProbe
{
    private const int MaxListed = 64;

    internal static int Run()
    {
        Console.WriteLine(Attempt.Read(() => Mf.Session(List)));
        return 0;
    }

    private static string List()
    {
        (MFT_ENUM_FLAG Flag, string Label)[] kinds =
        [
            (MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, "ソフトウェア・同期(decode と show が使うのはこの 1 番目)"),
            (MFT_ENUM_FLAG.MFT_ENUM_FLAG_ASYNCMFT, "ソフトウェア・非同期"),
            (MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE, "ハードウェア(常に非同期)"),
        ];
        Console.WriteLine("H.264 のデコーダー(MFTEnumEx、MFT_CATEGORY_VIDEO_DECODER、入力 = MFVideoFormat_H264、並びは SORTANDFILTER)");
        foreach (var (flag, label) in kinds)
        {
            // 区分ごとに試す。1 つの区分の列挙が失敗しても、ほかの区分は出す。
            Console.WriteLine(Attempt.Read(() =>
            {
                using var list = Mf.EnumH264Decoders(flag | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER);
                var lines = new List<string> { $"{label}: {list.Count} 個" };
                for (var i = 0; i < Math.Min(list.Count, MaxListed); i++)
                {
                    lines.Add($"  [{i}] {Mf.Label(list[i])}");
                }

                return string.Join(Environment.NewLine, lines);
            }));
        }

        return "decoders: 一覧を出しました";
    }
}
