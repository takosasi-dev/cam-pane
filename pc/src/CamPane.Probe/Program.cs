// CamPane.Probe の入口。Phase 0 の実測用で、引数で 3 つの確認(decoders / decode / show)を選ぶ。
// 出力は仕様書 §8.2 と §8.3 に貼る。製品の本体(CamPane.App)からは参照しない。
namespace CamPane.Probe;

internal static class Program
{
    // show が WPF のウィンドウを出すので、入口のスレッドを STA にする。
    [STAThread]
    private static int Main(string[] args)
    {
        return args.FirstOrDefault() switch
        {
            "decoders" => DecodersProbe.Run(),
            "decode" => DecodeProbe.Run(args[1..]),
            "show" => ShowProbe.Run(args[1..]),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("""
            CamPane.Probe  (管理者でない PowerShell から動かす)
              decoders          Media Foundation の H.264 デコーダーの一覧(名前、ハードウェアか、同期か非同期か)を出す
              decode <ファイル>  生の H.264 のファイルを復号し、NAL の数・フレームの数・絵の大きさ・形式・時間・使ったデコーダーを出す
              show <ファイル>    同じファイルを復号し、ウィンドウ「CamPane Probe」に 30 fps 相当で繰り返し映す(10 分で自動で閉じる)

            ファイルは、生の H.264(Annex B。開始コード 00 00 01 / 00 00 00 01 で区切った NAL の並び)。200 MB まで。
            復号には同期の MFT だけを使う(ハードウェアの非同期の MFT は一覧に出すだけ)。絵はどこにも保存しない。
            """);
        return 2;
    }
}
