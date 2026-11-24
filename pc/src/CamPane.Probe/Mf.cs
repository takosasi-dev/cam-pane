// Media Foundation を呼ぶときの共通の道具。失敗した呼び出しの名前と HRESULT を例外の文にする関数と、
// MFStartup / MFShutdown の対、H.264 デコーダーの列挙(MFTEnumEx)を持つ。宣言は CsWin32 の生成物だけを使う。
using System.Reflection;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace CamPane.Probe;

internal static unsafe class Mf
{
    // NativeMethods.txt に並べた HRESULT の定数から、値 → 名前の表を作る(数字を手で書き写さない)。
    private static readonly Dictionary<int, string> Names = typeof(HRESULT)
        .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(field => field.FieldType == typeof(HRESULT))
        .GroupBy(field => ((HRESULT)field.GetValue(null)!).Value)
        .ToDictionary(group => group.Key, group => group.First().Name);

    internal static string Describe(HRESULT hr) =>
        Names.TryGetValue(hr.Value, out var name) ? $"{hr} {name}" : hr.ToString();

    /// <summary>失敗なら、どの呼び出しがどの HRESULT で失敗したかを文にして投げる。</summary>
    internal static void Check(HRESULT hr, string call)
    {
        if (hr.Failed)
        {
            throw new COMException($"{call} が失敗: {Describe(hr)}", hr.Value);
        }
    }

    /// <summary>MFStartup と MFShutdown の間で body を動かす。</summary>
    internal static string Session(Func<string> body)
    {
        Check(PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL), "MFStartup");
        try
        {
            return body();
        }
        finally
        {
            var hr = PInvoke.MFShutdown();
            if (hr.Failed)
            {
                Console.WriteLine($"MFShutdown が失敗: {Describe(hr)}");
            }
        }
    }

    /// <summary>入力が H.264 の映像のデコーダーを、flags の区分で列挙する。</summary>
    internal static DecoderList EnumH264Decoders(MFT_ENUM_FLAG flags)
    {
        var input = new MFT_REGISTER_TYPE_INFO
        {
            guidMajorType = PInvoke.MFMediaType_Video,
            guidSubtype = PInvoke.MFVideoFormat_H264,
        };
        Check(
            PInvoke.MFTEnumEx(PInvoke.MFT_CATEGORY_VIDEO_DECODER, flags, input, null, out IMFActivate** items, out uint count),
            $"MFTEnumEx({flags})");
        return new DecoderList(items, count);
    }

    /// <summary>列挙で得た 1 件の表示名と CLSID。Learn では、名前の属性が付くのはハードウェアの MFT とあるので、無ければその旨を出す。</summary>
    internal static string Label(IMFActivate* activate)
    {
        var clsidResult = activate->GetGUID(PInvoke.MFT_TRANSFORM_CLSID_Attribute, out Guid clsid);
        var clsidText = clsidResult.Succeeded ? clsid.ToString("B") : $"(CLSID なし: {Describe(clsidResult)})";
        var hr = activate->GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out PWSTR text, out uint length);
        if (hr.Failed)
        {
            return $"(名前なし: {Describe(hr)}) {clsidText}";
        }

        try
        {
            return $"{new string(text.Value, 0, (int)length)} {clsidText}";
        }
        finally
        {
            PInvoke.CoTaskMemFree(text.Value);
        }
    }

    /// <summary>MFTEnumEx が返した配列。Learn のとおり、各要素を Release してから配列を CoTaskMemFree で返す。</summary>
    internal readonly struct DecoderList(IMFActivate** items, uint count) : IDisposable
    {
        internal int Count => (int)count;

        internal IMFActivate* this[int index] =>
            (uint)index < count ? items[index] : throw new ArgumentOutOfRangeException(nameof(index));

        public void Dispose()
        {
            for (uint i = 0; i < count; i++)
            {
                items[i]->Release();
            }

            PInvoke.CoTaskMemFree(items);
        }
    }
}
