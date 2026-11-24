// Media Foundation の H.264 デコーダー(同期の MFT だけ)に NAL を 1 つずつ入れ、NV12 の絵を 1 枚ずつ取り出す。
// 非同期の MFT(ハードウェアのデコーダー)は、同期の呼び方では動かないので使わない。D3D のマネージャーも渡さない。
// 手順は Microsoft Learn の Basic MFT Processing Model / Handling Stream Changes / H.264 Video Decoder のとおり。
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Variant;

namespace CamPane.Probe;

/// <summary>
/// 出力の絵の並び。Width・Height は符号化の大きさ(バッファの中の並び)、Visible… は見せる範囲(display aperture)。
/// NV12 は、Y の面(Stride × Height)のすぐ後ろに、U と V が交互に並ぶ面(Stride × Height / 2)が続く。
/// </summary>
internal readonly record struct FrameLayout(int Width, int Height, int Stride, int CropX, int CropY, int VisibleWidth, int VisibleHeight)
{
    internal int MinLength => Stride * (Height + ((Height + 1) / 2));

    public override string ToString() =>
        $"{VisibleWidth}x{VisibleHeight}(符号化の大きさ {Width}x{Height}、ストライド {Stride}、見せる範囲の左上 {CropX},{CropY})";
}

internal sealed unsafe class H264Decoder : IDisposable
{
    private const int MaxCandidates = 8;
    private const int MaxOutputTypes = 64;
    private const int MaxStreamChanges = 8;
    private const int MaxNotAccepting = 16;
    private const int MaxFrameBytes = 64 * 1024 * 1024;
    private const long FrameDuration = 333_333; // 100 ナノ秒の単位。30 fps と仮に置く(ファイルに時刻が無いため)

    private readonly byte[] _data;
    private readonly List<Nal> _nals;
    private readonly bool _verbose;
    private readonly bool _lowLatency;
    private IMFTransform* _mft;
    private IMFSample* _ownSample; // 自分で確保した出力のサンプル。MFT が確保する型なら null のまま
    private bool _mftAllocates;
    private bool _drained;
    private int _streamChanges;
    private long _pictures;

    internal H264Decoder(byte[] data, List<Nal> nals, bool verbose, bool lowLatency = false)
    {
        _data = data;
        _nals = nals;
        _verbose = verbose;
        _lowLatency = lowLatency;
        try
        {
            Open();
            Send(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING);
            Send(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal string Name { get; private set; } = "";

    internal FrameLayout Layout { get; private set; }

    /// <summary>直近の 1 枚(NV12)。有効なのは先頭から Nv12Length バイト。</summary>
    internal byte[] Nv12 { get; private set; } = [];

    internal int Nv12Length { get; private set; }

    /// <summary>デコーダーに入れ終えた NAL の数。</summary>
    internal int Fed { get; private set; }

    /// <summary>出し切り(END_OF_STREAM と DRAIN)を頼んだ後なら true。</summary>
    internal bool Drained => _drained;

    /// <summary>次の 1 枚を Nv12 に入れて true を返す。NAL を全部入れ、残りも出し切った後は false を返す。</summary>
    internal bool TryReadFrame()
    {
        var notAccepting = 0;
        // 1 回りごとに「NAL を 1 つ入れる・形式を決め直す・出し切りを頼む」のどれかが進むので、回る数は NAL の数で頭打ちになる。
        for (var step = 0; step < _nals.Count + MaxStreamChanges + MaxNotAccepting + 8; step++)
        {
            var output = new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = _mftAllocates ? null : _ownSample };
            uint status = 0;
            var hr = _mft->ProcessOutput(0, 1, &output, &status);
            if (output.pEvents != null)
            {
                output.pEvents->Release();
            }

            try
            {
                if (hr.Succeeded)
                {
                    if ((output.dwStatus & (uint)_MFT_OUTPUT_DATA_BUFFER_FLAGS.MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE) != 0)
                    {
                        continue;
                    }

                    if (output.pSample == null)
                    {
                        throw new InvalidOperationException("IMFTransform::ProcessOutput は成功を返したが、サンプルが空だった");
                    }

                    CopyFrame(output.pSample);
                    return true;
                }
            }
            finally
            {
                if (_mftAllocates && output.pSample != null)
                {
                    output.pSample->Release();
                }
            }

            if (hr == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                if (++_streamChanges > MaxStreamChanges)
                {
                    throw new InvalidOperationException($"MF_E_TRANSFORM_STREAM_CHANGE が {MaxStreamChanges} 回を超えた");
                }

                NegotiateOutput(afterStreamChange: true);
                continue;
            }

            if (hr != HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                Mf.Check(hr, $"IMFTransform::ProcessOutput(NAL を {Fed} 個入れた所)");
            }

            if (Fed < _nals.Count)
            {
                var nal = _nals[Fed];
                var inputResult = Feed(nal);
                if (inputResult == HRESULT.MF_E_NOTACCEPTING)
                {
                    // 出力が溜まっている合図。出力を引いてから、同じ NAL を入れ直す。
                    if (++notAccepting > MaxNotAccepting)
                    {
                        throw new InvalidOperationException($"IMFTransform::ProcessInput が MF_E_NOTACCEPTING を {MaxNotAccepting} 回続けて返した(NAL #{Fed})");
                    }

                    continue;
                }

                Mf.Check(inputResult, $"IMFTransform::ProcessInput(NAL #{Fed}、型 {nal.Type}、{nal.Length} バイト)");
                notAccepting = 0;
                Fed++;
                if (nal.IsSlice)
                {
                    _pictures++;
                }
            }
            else if (!_drained)
            {
                Send(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM);
                Send(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN);
                _drained = true;
            }
            else
            {
                return false;
            }
        }

        throw new InvalidOperationException($"1 枚を取り出すまでの手数が上限を超えた(NAL を {Fed} 個入れた所)");
    }

    public void Dispose()
    {
        ReleaseOwnSample();
        if (_mft != null)
        {
            _mft->Release();
            _mft = null;
        }
    }

    // 始まり・終わりの知らせと、出し切りの頼み。失敗しても復号は続け(出せる分は出す)、失敗は必ず出力に出す。
    private void Send(MFT_MESSAGE_TYPE message)
    {
        var hr = _mft->ProcessMessage(message, 0);
        if (hr.Failed)
        {
            Console.WriteLine($"IMFTransform::ProcessMessage({message}) が失敗: {Mf.Describe(hr)}(続ける)");
        }
    }

    // 同期の MFT の候補を列挙の順に試し、入力(H264_ES)と出力(NV12)が決まった最初の 1 つを使う。
    // 列挙の条件は入力 = H264 なので、H264_ES を受けない候補があり得る。落ちた候補は理由を出して次へ進む。
    private void Open()
    {
        using var list = Mf.EnumH264Decoders(MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER);
        for (var i = 0; i < Math.Min(list.Count, MaxCandidates); i++)
        {
            Name = Mf.Label(list[i]);
            Log($"デコーダー: {Name}(同期の MFT の列挙の {i + 1} 番目 / {list.Count} 個)");
            try
            {
                var iid = IMFTransform.IID_Guid;
                IMFTransform* mft = null;
                Mf.Check(list[i]->ActivateObject(&iid, (void**)&mft), "IMFActivate::ActivateObject(IMFTransform)");
                _mft = mft;
                RefuseAsync();
                if (_lowLatency)
                {
                    ApplyLowLatency();
                }

                SetInputType();
                NegotiateOutput(afterStreamChange: false);
                return;
            }
            catch (COMException ex)
            {
                Skip(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                Skip(ex.Message);
            }
        }

        throw new InvalidOperationException($"同期の H.264 デコーダーの候補 {list.Count} 個のうち、使える物が無かった");

        void Skip(string why)
        {
            Console.WriteLine($"  この候補は使えなかった({Name}): {why}");
            Dispose();
        }
    }

    // Learn: MF_TRANSFORM_ASYNC が 0 でなければ非同期。GetAttributes か GetUINT32 が失敗するなら同期。
    private void RefuseAsync()
    {
        IMFAttributes* attributes = null;
        if (_mft->GetAttributes(&attributes).Failed || attributes == null)
        {
            return;
        }

        try
        {
            if (attributes->GetUINT32(PInvoke.MF_TRANSFORM_ASYNC, out uint isAsync).Succeeded && isAsync != 0)
            {
                throw new InvalidOperationException($"{Name} は非同期の MFT だった。この Probe は同期の呼び方しかしないので使わない");
            }
        }
        finally
        {
            attributes->Release();
        }
    }

    // 型を決める前に低遅延を頼む。CODECAPI_AVLowLatencyMode と MF_LOW_LATENCY は同じ GUID(codecapi.h 617 行 / mfapi.h 3456 行)で、
    // 同じキーを ICodecAPI と IMFAttributes の 2 つの経路で入れる。失敗しても投げない(投げると Open が次の候補へ進み、
    // (a) と別のデコーダーを比べてしまう)。HRESULT と読み戻しを全部出す。Learn: Microsoft の H.264 デコーダーは VT_UI4 を使う。
    private void ApplyLowLatency()
    {
        var key = typeof(CODECAPI_AVLowLatencyMode).GUID;
        Console.WriteLine($"低遅延: キー {key:B}(CODECAPI_AVLowLatencyMode。MF_LOW_LATENCY と{(key == PInvoke.MF_LOW_LATENCY ? "同じ" : "違う")} GUID)");
        ICodecAPI* codec = null;
        IMFAttributes* attributes = null;
        try
        {
            var iid = ICodecAPI.IID_Guid;
            var hr = _mft->QueryInterface(&iid, (void**)&codec);
            Console.WriteLine($"  IMFTransform::QueryInterface(ICodecAPI): {Mf.Describe(hr)}");
            if (hr.Failed)
            {
                codec = null;
            }
            else
            {
                Console.WriteLine($"  ICodecAPI::IsSupported: {Mf.Describe(codec->IsSupported(key))}");
                var value = default(VARIANT);
                value.vt = VARENUM.VT_UI4;
                value.ulVal = 1;
                hr = codec->SetValue(key, value);
                Console.WriteLine($"  ICodecAPI::SetValue(VT_UI4 1): {Mf.Describe(hr)}");
                if (hr.Failed)
                {
                    value = default;
                    value.vt = VARENUM.VT_BOOL;
                    value.boolVal = VARIANT_BOOL.VARIANT_TRUE;
                    Console.WriteLine($"  ICodecAPI::SetValue(VT_BOOL TRUE): {Mf.Describe(codec->SetValue(key, value))}");
                }
            }

            hr = _mft->GetAttributes(&attributes);
            Console.WriteLine($"  IMFTransform::GetAttributes: {Mf.Describe(hr)}");
            if (hr.Failed || attributes == null)
            {
                attributes = null;
            }
            else
            {
                Console.WriteLine($"  IMFAttributes::SetUINT32(MF_LOW_LATENCY, 1): {Mf.Describe(attributes->SetUINT32(PInvoke.MF_LOW_LATENCY, 1))}");
                hr = attributes->GetUINT32(PInvoke.MF_LOW_LATENCY, out uint stored);
                Console.WriteLine($"  読み戻し IMFAttributes::GetUINT32: {Mf.Describe(hr)}{(hr.Succeeded ? $" 値 {stored}" : "")}");
            }

            if (codec != null)
            {
                hr = codec->GetValue(key, out VARIANT current);
                Console.WriteLine($"  読み戻し ICodecAPI::GetValue: {Mf.Describe(hr)}{(hr.Succeeded ? $" 型 {current.vt} 値 {(current.vt == VARENUM.VT_BOOL ? current.boolVal.Value : current.ulVal)}" : "")}");
                if (hr.Succeeded)
                {
                    _ = PInvoke.VariantClear(ref current);
                }
            }
        }
        finally
        {
            if (codec != null)
            {
                codec->Release();
            }

            if (attributes != null)
            {
                attributes->Release();
            }
        }
    }

    // Learn: 入力の type は major type と subtype の 2 つだけでよく、大きさは後で MF_E_TRANSFORM_STREAM_CHANGE で知らされる。
    // NAL を 1 つずつ入れるので、1 サンプルが絵の断片でよい H264_ES にする。
    private void SetInputType()
    {
        IMFMediaType* type = null;
        Mf.Check(PInvoke.MFCreateMediaType(&type), "MFCreateMediaType");
        try
        {
            Mf.Check(type->SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video), "IMFMediaType::SetGUID(MF_MT_MAJOR_TYPE)");
            Mf.Check(type->SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264_ES), "IMFMediaType::SetGUID(MF_MT_SUBTYPE)");
            Mf.Check(_mft->SetInputType(0, type, 0), "IMFTransform::SetInputType(MFVideoFormat_H264_ES)");
        }
        finally
        {
            type->Release();
        }
    }

    // デコーダーが出せる形式から NV12 を探して決める。最初の 1 回は仮の形式で、絵の大きさは STREAM_CHANGE の後に決まる。
    private void NegotiateOutput(bool afterStreamChange)
    {
        var offered = new List<string>();
        for (uint index = 0; index < MaxOutputTypes; index++)
        {
            IMFMediaType* type = null;
            var hr = _mft->GetOutputAvailableType(0, index, &type);
            if (hr == HRESULT.MF_E_NO_MORE_TYPES)
            {
                break;
            }

            Mf.Check(hr, $"IMFTransform::GetOutputAvailableType(0, {index})");
            try
            {
                Mf.Check(type->GetGUID(PInvoke.MF_MT_SUBTYPE, out Guid subtype), "IMFMediaType::GetGUID(MF_MT_SUBTYPE)");
                offered.Add(FourCc(subtype));
                if (subtype != PInvoke.MFVideoFormat_NV12)
                {
                    continue;
                }

                Mf.Check(_mft->SetOutputType(0, type, 0), "IMFTransform::SetOutputType(MFVideoFormat_NV12)");
                Layout = ReadLayout(type, required: afterStreamChange);
                Log($"出力の形式: NV12({(afterStreamChange ? "MF_E_TRANSFORM_STREAM_CHANGE の後" : "仮")})  絵: {Layout}");
                PrepareOutputSample();
                return;
            }
            finally
            {
                type->Release();
            }
        }

        throw new InvalidOperationException($"デコーダーが出せる形式に NV12 が無かった(出た物: {string.Join(", ", offered)})");
    }

    // ストライドは Learn の Uncompressed Video Buffers のとおり、MF_MT_DEFAULT_STRIDE、無ければ MFGetStrideForBitmapInfoHeader で得る。
    // 仮の形式(required = false)では、読めない・範囲の外でも止めずに「未定」(default)を返す。
    private static FrameLayout ReadLayout(IMFMediaType* type, bool required)
    {
        FrameLayout Fail(string why) => required ? throw new InvalidOperationException(why) : default;

        var hr = type->GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out ulong size);
        if (hr.Failed)
        {
            return Fail($"IMFMediaType::GetUINT64(MF_MT_FRAME_SIZE) が失敗: {Mf.Describe(hr)}");
        }

        var width = (int)(size >> 32);          // Learn: 上位 32 ビットが幅、下位 32 ビットが高さ
        var height = (int)(size & 0xFFFF_FFFF);
        if (width is < 16 or > 4096 || height is < 16 or > 4096)
        {
            return Fail($"絵の大きさ {width}x{height} は 16〜4096 の外(仕様書 §9.2)");
        }

        int stride;
        if (type->GetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, out uint rawStride).Succeeded)
        {
            stride = (int)rawStride; // Learn: UINT32 で入っているが、符号つきとして読む
        }
        else
        {
            var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(PInvoke.MFVideoFormat_NV12.ToByteArray());
            hr = PInvoke.MFGetStrideForBitmapInfoHeader(fourCc, (uint)width, out stride);
            if (hr.Failed)
            {
                return Fail($"MFGetStrideForBitmapInfoHeader(NV12, {width}) が失敗: {Mf.Describe(hr)}");
            }
        }

        if (stride < width)
        {
            return Fail($"ストライド {stride} が幅 {width} より小さい(負なら下から上の並び。YUV では想定していない)");
        }

        var key = PInvoke.MF_MT_MINIMUM_DISPLAY_APERTURE;
        MFVideoArea area = default;
        if (type->GetBlob(&key, (byte*)&area, (uint)sizeof(MFVideoArea), null).Succeeded
            && area.OffsetX.value >= 0 && area.OffsetY.value >= 0 && area.Area.cx > 0 && area.Area.cy > 0
            && area.OffsetX.value + area.Area.cx <= width && area.OffsetY.value + area.Area.cy <= height)
        {
            return new FrameLayout(width, height, stride, area.OffsetX.value, area.OffsetY.value, area.Area.cx, area.Area.cy);
        }

        return new FrameLayout(width, height, stride, 0, 0, width, height);
    }

    // Learn(ProcessOutput): PROVIDES_SAMPLES なら MFT がサンプルを確保する。それ以外は、呼ぶ側が cbSize 以上のバッファを渡す。
    private void PrepareOutputSample()
    {
        Mf.Check(_mft->GetOutputStreamInfo(0, out MFT_OUTPUT_STREAM_INFO info), "IMFTransform::GetOutputStreamInfo");
        ReleaseOwnSample();
        _mftAllocates = (info.dwFlags & (uint)_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        Log($"出力のサンプル: {(_mftAllocates ? "MFT が確保する" : "この Probe が確保する")}(dwFlags=0x{info.dwFlags:X} cbSize={info.cbSize} cbAlignment={info.cbAlignment})");
        if (_mftAllocates)
        {
            return;
        }

        var size = Math.Max(Math.Max(info.cbSize, (uint)Layout.MinLength), 1u);
        if (size > MaxFrameBytes)
        {
            throw new InvalidOperationException($"出力のバッファの大きさ {size} バイトが上限 {MaxFrameBytes} を超えた");
        }

        IMFMediaBuffer* buffer = null;
        IMFSample* sample = null;
        try
        {
            // Learn(MFCreateAlignedMemoryBuffer): そろえる単位は「単位 - 1」で渡す(16 バイトなら 0x0F)。
            Mf.Check(
                info.cbAlignment > 1
                    ? PInvoke.MFCreateAlignedMemoryBuffer(size, info.cbAlignment - 1, &buffer)
                    : PInvoke.MFCreateMemoryBuffer(size, &buffer),
                $"MFCreateMemoryBuffer({size})");
            Mf.Check(PInvoke.MFCreateSample(&sample), "MFCreateSample");
            Mf.Check(sample->AddBuffer(buffer), "IMFSample::AddBuffer");
            _ownSample = sample;
            sample = null;
        }
        finally
        {
            if (sample != null)
            {
                sample->Release();
            }

            if (buffer != null)
            {
                buffer->Release();
            }
        }
    }

    // NAL を 1 つ、開始コードつきのままサンプルに包んで入れる。戻り値は ProcessInput の HRESULT。
    private HRESULT Feed(Nal nal)
    {
        IMFMediaBuffer* buffer = null;
        IMFSample* sample = null;
        try
        {
            Mf.Check(PInvoke.MFCreateMemoryBuffer((uint)nal.Length, &buffer), $"MFCreateMemoryBuffer({nal.Length})");
            Mf.Check(buffer->Lock(out byte* destination, out uint capacity, out _), "IMFMediaBuffer::Lock(入力)");
            try
            {
                _data.AsSpan(nal.Offset, nal.Length).CopyTo(new Span<byte>(destination, checked((int)capacity)));
            }
            finally
            {
                Mf.Check(buffer->Unlock(), "IMFMediaBuffer::Unlock(入力)");
            }

            Mf.Check(buffer->SetCurrentLength((uint)nal.Length), "IMFMediaBuffer::SetCurrentLength");
            Mf.Check(PInvoke.MFCreateSample(&sample), "MFCreateSample");
            Mf.Check(sample->AddBuffer(buffer), "IMFSample::AddBuffer");
            Mf.Check(sample->SetSampleTime(_pictures * FrameDuration), "IMFSample::SetSampleTime");
            Mf.Check(sample->SetSampleDuration(FrameDuration), "IMFSample::SetSampleDuration");
            return _mft->ProcessInput(0, sample, 0);
        }
        finally
        {
            if (sample != null)
            {
                sample->Release();
            }

            if (buffer != null)
            {
                buffer->Release();
            }
        }
    }

    // 出力のバッファを、長さを確かめてから配列に写す。色の変換は配列の上で行う(範囲の外を読めば例外になり、黙って落ちない)。
    // Learn: IMFMediaBuffer::Lock で得る並びは、余白のない最小のストライド(MF_MT_DEFAULT_STRIDE)になる。
    private void CopyFrame(IMFSample* sample)
    {
        IMFMediaBuffer* buffer = null;
        Mf.Check(sample->ConvertToContiguousBuffer(&buffer), "IMFSample::ConvertToContiguousBuffer");
        try
        {
            Mf.Check(buffer->Lock(out byte* source, out _, out uint length), "IMFMediaBuffer::Lock(出力)");
            try
            {
                if (Layout.MinLength == 0 || length < Layout.MinLength || length > MaxFrameBytes)
                {
                    throw new InvalidOperationException($"出力のバッファの長さ {length} バイトが見込みと合わない(見込み {Layout.MinLength} バイト以上。絵: {Layout})");
                }

                if (Nv12.Length < length)
                {
                    Nv12 = new byte[length];
                }

                Marshal.Copy((IntPtr)source, Nv12, 0, (int)length);
                if (Nv12Length == 0)
                {
                    // 並びの見込み(ストライド × 高さ × 1.5)が合っているかを、長さで見比べられるようにする。
                    Log($"最初の 1 枚: NAL を {Fed} 個入れた所で出た。バッファ {length} バイト(見込み {Layout.MinLength} バイト)");
                }

                Nv12Length = (int)length;
            }
            finally
            {
                Mf.Check(buffer->Unlock(), "IMFMediaBuffer::Unlock(出力)");
            }

            if (!_mftAllocates)
            {
                // 同じバッファを次の 1 枚にも使うので、長さを 0 に戻しておく。
                Mf.Check(buffer->SetCurrentLength(0), "IMFMediaBuffer::SetCurrentLength(0)");
            }
        }
        finally
        {
            buffer->Release();
        }
    }

    private void ReleaseOwnSample()
    {
        if (_ownSample != null)
        {
            _ownSample->Release();
            _ownSample = null;
        }
    }

    private static string FourCc(Guid subtype)
    {
        var bytes = subtype.ToByteArray();
        return bytes[..4].All(b => b is >= 0x20 and < 0x7F) ? System.Text.Encoding.ASCII.GetString(bytes, 0, 4) : subtype.ToString();
    }

    private void Log(string line)
    {
        if (_verbose)
        {
            Console.WriteLine(line);
        }
    }
}
