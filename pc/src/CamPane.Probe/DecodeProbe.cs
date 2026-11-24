// 生の H.264(Annex B)のファイルを復号し、NAL の数・フレームの数・絵の大きさ・形式・時間・使ったデコーダーを出す(仕様書 §8.2)。
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
        var watch = Stopwatch.StartNew();
        using var decoder = new H264Decoder(data, nals, verbose: true);
        var frames = 0;
        var stopped = "入力を全部入れて、残りも出し切った";
        while (decoder.TryReadFrame())
        {
            frames++;
            if (frames == 1)
            {
                Console.WriteLine($"最初の 1 枚まで: {watch.ElapsedMilliseconds} ms(デコーダーの用意を含む)");
            }

            if (frames >= MaxFrames || watch.Elapsed > Limit)
            {
                stopped = $"上限({MaxFrames} 枚、または {Limit.TotalSeconds:0} 秒)で打ち切った";
                break;
            }
        }

        watch.Stop();
        var seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.001);
        return string.Join(Environment.NewLine,
            $"decode: {stopped}",
            $"  読んだ NAL: {nals.Count} 個(デコーダーに入れた {decoder.Fed} 個)",
            $"  復号できたフレーム: {frames} 枚",
            $"  絵の大きさ: {decoder.Layout}",
            "  出力の形式: NV12",
            $"  かかった時間: {watch.ElapsedMilliseconds} ms({frames / seconds:0.0} 枚/秒。デコーダーの用意と、配列への写しを含む)",
            $"  デコーダー: {decoder.Name}(同期の MFT として呼んだ。D3D のマネージャーは渡していない)");
    }
}
