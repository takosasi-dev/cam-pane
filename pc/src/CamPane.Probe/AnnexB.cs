// 生の H.264(Annex B)のファイルを読み、開始コード(00 00 01、または 00 00 00 01)で NAL に区切る。
// 200 MB を超えるファイルは読まない。ファイルの中身(映像)は出力に書かず、数だけを出す。
using System.IO;

namespace CamPane.Probe;

/// <summary>1 つの NAL。Offset と Length は開始コードを含む。Type は nal_unit_type(中身が空なら -1)。</summary>
internal readonly record struct Nal(int Offset, int Length, int Type)
{
    /// <summary>絵のスライス(1 = IDR でない絵、5 = IDR の絵)かどうか。</summary>
    internal bool IsSlice => Type is 1 or 5;
}

internal static class AnnexB
{
    private const long MaxFileBytes = 200L * 1024 * 1024;

    internal static (byte[] Data, List<Nal> Nals) Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"ファイルがありません: {info.Name}");
        }

        if (info.Length is 0 or > MaxFileBytes)
        {
            throw new InvalidDataException($"ファイルの大きさが {info.Length} バイトです。1 バイト以上、200 MB 以下のファイルだけを読みます");
        }

        var data = File.ReadAllBytes(info.FullName);
        var nals = Split(data, out var leading);
        if (nals.Count == 0)
        {
            throw new InvalidDataException("開始コード(00 00 01)が 1 つもありません。生の H.264(Annex B)のファイルではないようです");
        }

        Console.WriteLine($"ファイル: {info.Name}  {data.Length} バイト");
        Console.WriteLine(
            $"NAL: {nals.Count} 個(SPS {nals.Count(n => n.Type == 7)} / PPS {nals.Count(n => n.Type == 8)} / " +
            $"IDR の絵 {nals.Count(n => n.Type == 5)} / IDR でない絵 {nals.Count(n => n.Type == 1)} / " +
            $"そのほか {nals.Count(n => n.Type is not (1 or 5 or 7 or 8))})  最初の開始コードの前 {leading} バイト");
        return (data, nals);
    }

    /// <summary>開始コードの位置で区切る。各 NAL は、自分の開始コードの先頭から、次の開始コードの手前までとする。</summary>
    internal static List<Nal> Split(ReadOnlySpan<byte> data, out int leading)
    {
        ReadOnlySpan<byte> startCode = [0, 0, 1];
        var nals = new List<Nal>();
        leading = 0;
        var start = -1;   // 今の NAL の開始コードの先頭
        var payload = 0;  // 今の NAL の中身(開始コードの次)の先頭
        var search = 0;
        while (true)
        {
            var found = data[search..].IndexOf(startCode);
            if (found < 0)
            {
                break;
            }

            var code = search + found;
            // 4 バイトの開始コード(00 00 00 01)の先頭の 00 は、次の NAL の側に付ける。
            var codeStart = code > payload && data[code - 1] == 0 ? code - 1 : code;
            if (start >= 0)
            {
                nals.Add(Make(data, start, payload, codeStart));
            }
            else
            {
                leading = codeStart;
            }

            start = codeStart;
            payload = code + 3;
            search = payload;
        }

        if (start >= 0)
        {
            nals.Add(Make(data, start, payload, data.Length));
        }

        return nals;
    }

    private static Nal Make(ReadOnlySpan<byte> data, int start, int payload, int end) =>
        new(start, end - start, payload < end ? data[payload] & 0x1F : -1);
}
