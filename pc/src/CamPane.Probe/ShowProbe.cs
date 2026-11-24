// 生の H.264 のファイルを復号し、普通の WPF のウィンドウ(Image に WriteableBitmap、Bgr32)に、縦横比を保って繰り返し映す(仕様書 §8.3)。
// 動かす人が、画面共有や録画のソフトでこのウィンドウを選び、黒くならずに映るかを見る。絵はファイルに書かない(INV-4)。
// 復号は別のスレッドで行い、画面のスレッドを止めない。10 分たつか、ウィンドウを閉じると終わる。
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Win32;

namespace CamPane.Probe;

internal static class ShowProbe
{
    private const int AssumedFps = 30; // ファイルに時刻が無いので、仮に 30 fps で送る
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(5);

    internal static int Run(string[] args)
    {
        if (args is not [var path])
        {
            Console.WriteLine("使い方: CamPane.Probe show <ファイル>");
            return 2;
        }

        Console.WriteLine(Attempt.Read(() => Show(path)));
        return 0;
    }

    private static string Show(string path)
    {
        var (data, nals) = AnnexB.Load(path);
        Mf.Check(PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL), "MFStartup");

        var image = new Image { Stretch = Stretch.Uniform };
        var window = new Window { Title = "CamPane Probe", Width = 960, Height = 540, Background = Brushes.Black, Content = image };
        using var closing = new CancellationTokenSource();
        var result = "復号のスレッドが結果を返さなかった";
        var thread = new Thread(() => result = Attempt.Read(() => Play(data, nals, window, image, closing.Token)))
        {
            IsBackground = true,
            Name = "CamPane.Probe decode",
        };
        thread.SetApartmentState(ApartmentState.MTA);

        Console.WriteLine($"描き方: WPF の Image に WriteableBitmap(Bgr32)を WritePixels で書く。{AssumedFps} fps と仮に置いて繰り返す。{Limit.TotalMinutes:0} 分で自動で閉じる");
        thread.Start();
        new Application().Run(window);
        closing.Cancel();

        if (!thread.Join(UiTimeout))
        {
            // 復号の呼び出しが戻らない。使用中の MFT を残したまま MFShutdown は呼ばず、プロセスの終了に任せる。
            return $"失敗: ウィンドウを閉じた後、復号のスレッドが {UiTimeout.TotalSeconds:0} 秒で止まらなかった";
        }

        var hr = PInvoke.MFShutdown();
        return hr.Failed ? $"{result}{Environment.NewLine}MFShutdown が失敗: {Mf.Describe(hr)}" : result;
    }

    // 復号のスレッドの本体。画面に触る所だけ Dispatcher.Invoke で画面のスレッドへ渡す(取り消しと時間切れつき)。
    private static string Play(byte[] data, List<Nal> nals, Window window, Image image, CancellationToken closing)
    {
        var total = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / AssumedFps);
        long shown = 0;
        var passes = 0;
        var layout = default(FrameLayout);
        var bgra = Array.Empty<byte>();
        WriteableBitmap? bitmap = null; // 画面のスレッドだけが触る

        var summarized = false;

        string Summary(string why)
        {
            summarized = true;
            return $"show: {why}{Environment.NewLine}  出せたフレーム: {shown} 枚({passes} 周目、{total.Elapsed.TotalSeconds:0.0} 秒)  絵の大きさ: {layout}";
        }

        try
        {
            while (true)
            {
                passes++;
                var inPass = 0;
                var passWatch = Stopwatch.StartNew();
                using var decoder = new H264Decoder(data, nals, verbose: passes == 1);
                while (true)
                {
                    var frameWatch = Stopwatch.StartNew();
                    if (!decoder.TryReadFrame())
                    {
                        break;
                    }

                    if (closing.IsCancellationRequested)
                    {
                        return Summary("ウィンドウが閉じられた");
                    }

                    if (total.Elapsed > Limit)
                    {
                        return Summary($"上限の {Limit.TotalMinutes:0} 分に達したので閉じた");
                    }

                    var frame = layout = decoder.Layout;
                    var needed = frame.VisibleWidth * frame.VisibleHeight * 4;
                    if (bgra.Length != needed)
                    {
                        bgra = new byte[needed];
                    }

                    Nv12.ToBgra(decoder.Nv12, frame, bgra);
                    var pixels = bgra;
                    window.Dispatcher.Invoke(
                        () =>
                        {
                            if (bitmap is null || bitmap.PixelWidth != frame.VisibleWidth || bitmap.PixelHeight != frame.VisibleHeight)
                            {
                                bitmap = new WriteableBitmap(frame.VisibleWidth, frame.VisibleHeight, 96, 96, PixelFormats.Bgr32, null);
                                image.Source = bitmap;
                            }

                            bitmap.WritePixels(new Int32Rect(0, 0, frame.VisibleWidth, frame.VisibleHeight), pixels, frame.VisibleWidth * 4, 0);
                        },
                        DispatcherPriority.Render,
                        closing,
                        UiTimeout);
                    shown++;
                    inPass++;

                    var rest = interval - frameWatch.Elapsed;
                    if (rest > TimeSpan.Zero && closing.WaitHandle.WaitOne(rest))
                    {
                        return Summary("ウィンドウが閉じられた");
                    }
                }

                if (inPass == 0)
                {
                    return Summary("失敗: 1 周しても 1 枚も復号できなかったので、繰り返さない");
                }

                Console.WriteLine($"  {passes} 周目: {inPass} 枚を {passWatch.Elapsed.TotalSeconds:0.0} 秒で出した({inPass / Math.Max(passWatch.Elapsed.TotalSeconds, 0.001):0.0} fps)");
            }
        }
        catch (OperationCanceledException)
        {
            return Summary("ウィンドウが閉じられた");
        }
        catch (TimeoutException)
        {
            return Summary($"失敗: 画面のスレッドが {UiTimeout.TotalSeconds:0} 秒応えなかった(Dispatcher.Invoke の時間切れ)");
        }
        finally
        {
            if (!summarized)
            {
                // 復号か描画の例外で抜ける所。例外の中身は、この後の「失敗:」の行に出る。
                Console.WriteLine(Summary("復号か描画の失敗で止まった"));
            }

            // 復号の側が先に終わったとき(失敗、上限)は、ウィンドウを閉じて Run を戻す。閉じた後なら何も起きない。
            if (!closing.IsCancellationRequested)
            {
                window.Dispatcher.InvokeAsync(window.Close);
            }
        }
    }
}
