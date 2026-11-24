// 1 つの確認を試して、結果か、失敗の中身(例外の型と文)を文字で返す。
// Probe は「どの呼び出しが通って、どれが通らないか」を出すのが仕事なので、ここだけは例外を広く受けて表示に変える。
namespace CamPane.Probe;

internal static class Attempt
{
    internal static string Read(Func<string> read)
    {
#pragma warning disable CA1031 // 握りつぶさず、失敗の中身をそのまま出力に出す
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return $"失敗: {ex.GetType().Name}: {ex.Message}";
        }
#pragma warning restore CA1031
    }
}
