// 生の H.264(Annex B)のファイルを (a) 今のまま (b) 低遅延を頼んで (c) 低遅延で 1 枚分ずつ入れて (d) さらに SPS に並べ替えなしを書き足して、の 4 回復号し、NAL の数・フレームの数・溜め(遅れ)・時間を比べる(仕様書 §8.2)。
// ウィンドウは出さない。復号した絵は数えるだけで、どこにも書かない(INV-4)。
using System.Diagnostics;

namespace CamPane.Probe;

internal static class DecodeProbe
{
    private const int MaxFrames = 100_000;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    internal static int Run(string[] args)
    {
        if (args is not [var path])
        {
            Console.WriteLine("使い方: CamPane.Probe decode <ファイル>");
            return 2;
        }

        Console.WriteLine(Attempt.Read(() => Mf.Session(() => Decode(path))));
        return 0;
    }

    private static string Decode(string path)
    {
        var (data, nals) = AnnexB.Load(path);
        var pictures = Latency.Pictures(data, nals);
        Console.WriteLine($"絵: {pictures.Count} 枚(スライスの first_mb_in_slice が 0 の所で区切った)");

        Console.WriteLine();
        Console.WriteLine("== (a) 今のまま ==");
        var a = DecodeOnce(data, nals, pictures, lowLatency: false);
        Console.WriteLine();
        Console.WriteLine("== (b) 低遅延を頼んだ(CODECAPI_AVLowLatencyMode = MF_LOW_LATENCY) ==");
        var b = DecodeOnce(data, nals, pictures, lowLatency: true);
        Console.WriteLine();

        // (c) は 1 枚の絵の全スライスと、その前の SPS・PPS などを 1 サンプル・1 つの時刻にまとめる。
        var units = Latency.AccessUnits(data, nals);
        Console.WriteLine($"== (c) 低遅延のまま、1 枚の絵を 1 サンプルにまとめて入れた({units.Count} サンプル。下のデコーダーの記録の「NAL」は 1 サンプルを指す) ==");
        var c = DecodeOnce(data, units, OnePerUnit(units), lowLatency: true, unit: "サンプル");
        Console.WriteLine();

        // (d) は (c) に加えて、入れる前に SPS の VUI に bitstream_restriction(max_num_reorder_frames = 0)を書き足す。
        var (fixedData, notes) = Sps.RewriteStream(data, nals);
        var fixedUnits = Latency.AccessUnits(fixedData, AnnexB.Split(fixedData, out _));
        Console.WriteLine($"== (d) (c) に加えて、SPS に並べ替えなしを書き足した({fixedUnits.Count} サンプル) ==");
        foreach (var group in notes.GroupBy(note => note))
        {
            Console.WriteLine($"SPS {group.Count()} 個: {group.Key}");
        }

        var d = DecodeOnce(fixedData, fixedUnits, OnePerUnit(fixedUnits), lowLatency: true, unit: "サンプル");
        Console.WriteLine();
        return string.Join(Environment.NewLine,
            "比べ(遅れ = その絵を入れ終えてから、その絵が出るまでに入れた NAL(c はサンプル)の数と、それを絵の数に直した値。出し切りの前に出た絵だけで数える):",
            $"  (a) 今のまま:         {a.Row}",
            $"  (b) 低遅延:           {b.Row}",
            $"  (c) 低遅延+1 枚ずつ:  {c.Row}",
            $"  (d) (c)+SPS 書き足し: {d.Row}",
            new[] { b.Name, c.Name, d.Name }.All(name => name == a.Name)
                ? $"  デコーダーは (a)〜(d) とも同じ: {a.Name}"
                : $"  デコーダーが違う(比べにならない): (a) {a.Name} / (b) {b.Name} / (c) {c.Name} / (d) {d.Name}",
            "  注: (a)(b) は NAL を 1 つずつ入れるので、次の絵の最初の NAL が来るまで絵の終わりが分からないことがある。絵に直して 0〜1 なら溜めは無い。",
            "  注: 時間は (a) が先に動くぶん、読み込みの初回の手間を負うことがある。比べの主は入れた数。");
    }

    private static (string Row, string Name) DecodeOnce(byte[] data, List<Nal> nals, List<Picture> pictures, bool lowLatency, string unit = "NAL")
    {
        var watch = Stopwatch.StartNew();
        using var decoder = new H264Decoder(data, nals, verbose: true, lowLatency);
        var fedAtOutput = new List<int>(); // 出し切りの前に出た絵の、出た時点で入れ終えていた単位(NAL かサンプル)の数
        var afterDrain = 0;
        var stopped = "入力を全部入れて、残りも出し切った";
        while (decoder.TryReadFrame())
        {
            if (decoder.Drained)
            {
                afterDrain++;
            }
            else
            {
                fedAtOutput.Add(decoder.Fed);
            }

            var count = fedAtOutput.Count + afterDrain;
            if (count == 1)
            {
                Console.WriteLine($"最初の 1 枚まで: {watch.ElapsedMilliseconds} ms(デコーダーの用意を含む)");
            }

            if (count >= MaxFrames || watch.Elapsed > Limit)
            {
                stopped = $"上限({MaxFrames} 枚、または {Limit.TotalSeconds:0} 秒)で打ち切った";
                break;
            }
        }

        watch.Stop();
        var frames = fedAtOutput.Count + afterDrain;
        var seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.001);

        string first;
        if (fedAtOutput.Count > 0)
        {
            var fed = fedAtOutput[0];
            first = $"{unit} {fed} 個(絵 {pictures.Count(p => p.First < fed)} 枚分。最後のスライスまで入れ終えた絵は {pictures.Count(p => p.Last < fed)} 枚)";
        }
        else
        {
            first = frames > 0 ? $"出し切りの後({unit}を全部 {decoder.Fed} 個入れた後)" : "1 枚も出なかった";
        }

        var lags = Latency.Lags(pictures, fedAtOutput);
        string lagText;
        if (lags is null)
        {
            lagText = "数えられない(k 枚目の出力 = k 枚目の絵 の対応が崩れた)";
        }
        else if (lags.Length == 0)
        {
            lagText = "出し切りの前に出た絵が無い";
        }
        else
        {
            var inPictures = Latency.PictureLags(pictures, fedAtOutput)!;
            lagText = $"{Spread(lags)} {unit}、絵に直すと {Spread(inPictures)} 枚({lags.Length} 枚で)";
        }

        Console.WriteLine(string.Join(Environment.NewLine,
            $"decode: {stopped}",
            $"  入れる{unit}: {nals.Count} 個(デコーダーに入れた {decoder.Fed} 個)",
            $"  最初の 1 枚まで: {first}",
            $"  遅れ: {lagText}",
            $"  復号できたフレーム: {frames} 枚(出し切りの前 {fedAtOutput.Count} 枚 / 後 {afterDrain} 枚。ファイルの絵は {pictures.Count} 枚)",
            $"  絵の大きさ: {decoder.Layout}",
            "  出力の形式: NV12",
            $"  かかった時間: {watch.ElapsedMilliseconds} ms({frames / seconds:0.0} 枚/秒。デコーダーの用意と、配列への写しを含む)",
            $"  デコーダー: {decoder.Name}(同期の MFT として呼んだ。D3D のマネージャーは渡していない)"));

        return ($"最初の 1 枚まで {first} / 遅れ {lagText} / 復号 {frames} 枚(出し切りの後 {afterDrain} 枚) / {watch.ElapsedMilliseconds} ms",
            decoder.Name);
    }

    // 1 サンプル = 1 枚の絵として、絵の並びを作る(スライスの無い塊は絵に数えない)。
    private static List<Picture> OnePerUnit(List<Nal> units) =>
        units.Select((unit, i) => (unit, i)).Where(x => x.unit.IsSlice).Select(x => new Picture(x.i, x.i)).ToList();

    private static string Spread(int[] values)
    {
        var sorted = values.Order().ToArray();
        return $"中央値 {sorted[(sorted.Length - 1) / 2]}・最大 {sorted[^1]}";
    }
}
