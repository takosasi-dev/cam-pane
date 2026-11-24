// NV12 の 1 枚を、WPF に渡せる BGRA(上の行から、B・G・R・A の順)へ直す。見せる範囲だけを写す。
// 並びと式は Microsoft Learn の "Recommended 8-Bit YUV Formats for Video Rendering" の NV12 の節と、
// "Converting 8-bit YUV to RGB888" の式(BT.601、Y は 16〜235)のとおり。
namespace CamPane.Probe;

internal static class Nv12
{
    internal static void ToBgra(byte[] nv12, FrameLayout layout, byte[] bgra)
    {
        var chromaPlane = layout.Stride * layout.Height; // U と V が交互に並ぶ面の先頭。行の幅(ストライド)は Y の面と同じ
        var target = 0;
        for (var y = 0; y < layout.VisibleHeight; y++)
        {
            var sourceY = layout.CropY + y;
            var lumaRow = (sourceY * layout.Stride) + layout.CropX;
            var chromaRow = chromaPlane + ((sourceY / 2) * layout.Stride);
            for (var x = 0; x < layout.VisibleWidth; x++)
            {
                var chroma = chromaRow + ((layout.CropX + x) & ~1); // 横 2 画素で 1 組。U が先、V が後
                var c = (298 * (nv12[lumaRow + x] - 16)) + 128;
                var d = nv12[chroma] - 128;
                var e = nv12[chroma + 1] - 128;
                bgra[target++] = Clip((c + (516 * d)) >> 8);
                bgra[target++] = Clip((c - (100 * d) - (208 * e)) >> 8);
                bgra[target++] = Clip((c + (409 * e)) >> 8);
                bgra[target++] = 255; // 透明にしない(透明だと、取り込みの黒と見分けられない)
            }
        }
    }

    private static byte Clip(int value) => (byte)Math.Clamp(value, 0, 255);
}
