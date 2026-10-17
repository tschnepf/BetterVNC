using System.Runtime.InteropServices;
using BetterVnc.Media.Interop;

namespace BetterVnc.Media;

/// <summary>One encoded access unit. Key frames always carry SPS and PPS so a decoder can start on them.</summary>
public readonly record struct EncodedFrame(byte[] Data, bool Keyframe, long TimestampMicros);

/// <summary>
/// H.264 encoding with a Media Foundation transform: the GPU vendor's hardware encoder when one is registered
/// (AMD, Intel, NVIDIA all ship one), otherwise Microsoft's software encoder. Takes BGRA frames, converts them to
/// NV12 straight into the sample buffer and emits Annex B frames. Hardware encoders are asynchronous MFTs, so the
/// caller checks <see cref="WantsInput"/> before <see cref="Encode"/> and calls <see cref="Pump"/> regularly to
/// collect output; the software encoder fits the same protocol with <see cref="WantsInput"/> always true.
/// All methods must be called from one thread.
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    private IMFActivate? _activate;
    private IMFTransform _mft = null!;
    private ICodecAPI? _codec;
    private IMFMediaEventGenerator? _events;
    private IMFSample? _outputSample;
    private bool _providesSamples;
    private bool _async;
    private int _needInput;
    private byte[]? _sequenceHeader;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public string Name { get; private set; } = "";
    public bool IsHardware { get; private set; }
    /// <summary>True when the encoder honours key-frame requests; otherwise the caller recreates the encoder to get one.</summary>
    public bool CanForceKeyframe { get; private set; }
    public bool WantsInput => !_async || _needInput > 0;

    /// <summary>Diagnostic hook: receives one line per initialization step.</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>Names the encoder a new instance would use, or null when Media Foundation has no H.264 encoder at all.</summary>
    public static string? DescribeAvailable(bool preferHardware = true)
    {
        try
        {
            MediaRuntime.EnsureStarted();
            foreach (var hardware in preferHardware ? new[] { true, false } : new[] { false })
            {
                var activates = Enumerate(hardware);
                try
                {
                    if (activates.Length == 0) continue;
                    var name = MF.GetString((IMFAttributes)activates[0], MFGuids.MFT_FRIENDLY_NAME_Attribute) ?? "H.264 encoder";
                    return $"{name} ({(hardware ? "hardware" : "software")})";
                }
                finally { foreach (var a in activates) MF.Release(a); }
            }
        }
        catch (Exception) { }
        return null;
    }

    public H264Encoder(int width, int height, int fps, int bitrateKbps, bool preferHardware = true)
    {
        if ((width & 1) != 0 || (height & 1) != 0 || width <= 0 || height <= 0) throw new ArgumentException("Width and height must be positive and even.");
        Width = width;
        Height = height;
        Fps = Math.Clamp(fps, 1, 240);
        MediaRuntime.EnsureStarted();
        var errors = new List<string>();
        foreach (var hardware in preferHardware ? new[] { true, false } : new[] { false })
        {
            var activates = Enumerate(hardware);
            if (activates.Length == 0) { errors.Add(hardware ? "no hardware H.264 encoder registered" : "no software H.264 encoder registered"); continue; }
            for (int i = 0; i < activates.Length; i++)
            {
                try
                {
                    Initialize(activates[i], hardware, bitrateKbps);
                    for (int j = 0; j < activates.Length; j++) if (j != i) MF.Release(activates[j]);
                    return;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Name}: {ex.Message}");
                    Teardown();
                }
            }
        }
        throw new InvalidOperationException("No usable H.264 encoder: " + string.Join("; ", errors));
    }

    private static IMFActivate[] Enumerate(bool hardware)
    {
        var input = new MFT_REGISTER_TYPE_INFO { MajorType = MFGuids.MFMediaType_Video, Subtype = MFGuids.MFVideoFormat_NV12 };
        var output = new MFT_REGISTER_TYPE_INFO { MajorType = MFGuids.MFMediaType_Video, Subtype = MFGuids.MFVideoFormat_H264 };
        uint flags = (hardware ? MF.MFT_ENUM_FLAG_HARDWARE : MF.MFT_ENUM_FLAG_SYNCMFT) | MF.MFT_ENUM_FLAG_SORTANDFILTER;
        MF.Check(MF.MFTEnumEx(MFGuids.MFT_CATEGORY_VIDEO_ENCODER, flags, &input, &output, out var array, out var count), "MFTEnumEx");
        return count == 0 ? Array.Empty<IMFActivate>() : MF.TakeActivates(array, count);
    }

    private void Initialize(IMFActivate activate, bool hardware, int bitrateKbps)
    {
        _activate = activate;
        Name = MF.GetString((IMFAttributes)activate, MFGuids.MFT_FRIENDLY_NAME_Attribute) ?? (hardware ? "hardware H.264 encoder" : "software H.264 encoder");
        IsHardware = hardware;
        MF.Check(activate.ActivateObject(MFGuids.IID_IMFTransform, out var p), "activate encoder");
        _mft = (IMFTransform)Marshal.GetObjectForIUnknown(p);
        Marshal.Release(p);
        Trace?.Invoke("activated " + Name);

        if (_mft.GetAttributes(out var attrs) >= 0 && attrs != null)
        {
            _async = attrs.GetUINT32(MFGuids.MF_TRANSFORM_ASYNC, out var isAsync) >= 0 && isAsync != 0;
            if (_async) MF.Check(attrs.SetUINT32(MFGuids.MF_TRANSFORM_ASYNC_UNLOCK, 1), "MF_TRANSFORM_ASYNC_UNLOCK");
            attrs.SetUINT32(MFGuids.MF_LOW_LATENCY, 1);
            MF.Release(attrs);
        }
        _codec = _mft as ICodecAPI;
        Trace?.Invoke($"attributes read, async={_async}, codecapi={_codec != null}");
        uint bps = (uint)Math.Clamp((long)bitrateKbps * 1000, 100_000, 400_000_000);
        if (_codec != null)
        {
            // Static tuning first (some encoders only accept it before the types are set), bitrate again afterwards.
            SetCodecValue(MFGuids.CODECAPI_AVEncCommonRateControlMode, Variant.UInt32(MF.eAVEncCommonRateControlMode_CBR));
            SetCodecValue(MFGuids.CODECAPI_AVEncCommonMeanBitRate, Variant.UInt32(bps));
            SetCodecValue(MFGuids.CODECAPI_AVEncMPVGOPSize, Variant.UInt32(0x7FFFFFFF)); // key frames only when asked for
            SetCodecValue(MFGuids.CODECAPI_AVEncMPVDefaultBPictureCount, Variant.UInt32(0));
            SetCodecValue(MFGuids.CODECAPI_AVLowLatencyMode, Variant.Bool(true));
            SetCodecValue(MFGuids.CODECAPI_AVEncCommonQualityVsSpeed, Variant.UInt32(hardware ? 50u : 33u));
        }

        // Output type first (what hardware encoders expect), then the matching NV12 input.
        int hr = 0;
        foreach (var profile in new[] { MF.eAVEncH264VProfile_High, MF.eAVEncH264VProfile_Main, MF.eAVEncH264VProfile_Base })
        {
            var output = NewVideoType(MFGuids.MFVideoFormat_H264);
            var oa = (IMFAttributes)output;
            oa.SetUINT32(MFGuids.MF_MT_AVG_BITRATE, bps);
            oa.SetUINT32(MFGuids.MF_MT_MPEG2_PROFILE, profile);
            hr = _mft.SetOutputType(0, output, 0);
            MF.Release(output);
            if (hr >= 0) break;
        }
        MF.Check(hr, "set H.264 output type");
        Trace?.Invoke("output type set");

        var input = NewVideoType(MFGuids.MFVideoFormat_NV12);
        ((IMFAttributes)input).SetUINT32(MFGuids.MF_MT_DEFAULT_STRIDE, (uint)Width);
        hr = _mft.SetInputType(0, input, 0);
        MF.Release(input);
        if (hr < 0)
        {
            // Some encoders insist on one of their own proposals; find the NV12 one and complete it.
            for (uint i = 0; _mft.GetInputAvailableType(0, i, out var candidate) >= 0; i++)
            {
                var ca = (IMFAttributes)candidate;
                bool nv12 = ca.GetGUID(MFGuids.MF_MT_SUBTYPE, out var sub) >= 0 && sub == MFGuids.MFVideoFormat_NV12;
                if (nv12)
                {
                    ca.SetUINT64(MFGuids.MF_MT_FRAME_SIZE, MF.Pack((uint)Width, (uint)Height));
                    ca.SetUINT64(MFGuids.MF_MT_FRAME_RATE, MF.Pack((uint)Fps, 1));
                    ca.SetUINT64(MFGuids.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
                    ca.SetUINT32(MFGuids.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
                    hr = _mft.SetInputType(0, candidate, 0);
                }
                MF.Release(candidate);
                if (nv12) break;
            }
            MF.Check(hr, "set NV12 input type");
        }
        Trace?.Invoke("input type set");

        if (_codec != null)
        {
            SetCodecValue(MFGuids.CODECAPI_AVEncCommonMeanBitRate, Variant.UInt32(bps));
            CanForceKeyframe = _codec.IsSupported(MFGuids.CODECAPI_AVEncVideoForceKeyFrame) == MF.S_OK;
        }

        MF.Check(_mft.GetOutputStreamInfo(0, out var info), "GetOutputStreamInfo");
        _providesSamples = (info.Flags & MF.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        Trace?.Invoke($"stream info flags=0x{info.Flags:X} size={info.Size} canForce={CanForceKeyframe}");
        if (!_providesSamples)
        {
            uint size = Math.Max(info.Size, (uint)(Width * Height * 2));
            MF.Check(MF.MFCreateSample(out var sample), "MFCreateSample");
            MF.Check(MF.MFCreateAlignedMemoryBuffer(size, MF.MF_64_BYTE_ALIGNMENT, out var buffer), "MFCreateAlignedMemoryBuffer");
            sample.AddBuffer(buffer);
            MF.Release(buffer);
            _outputSample = sample;
        }

        MF.Check(_mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, UIntPtr.Zero), "begin streaming");
        MF.Check(_mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_START_OF_STREAM, UIntPtr.Zero), "start of stream");
        if (_async) _events = (IMFMediaEventGenerator)_mft;
        Trace?.Invoke("streaming");
    }

    private IMFMediaType NewVideoType(Guid subtype)
    {
        MF.Check(MF.MFCreateMediaType(out var type), "MFCreateMediaType");
        var a = (IMFAttributes)type;
        a.SetGUID(MFGuids.MF_MT_MAJOR_TYPE, MFGuids.MFMediaType_Video);
        a.SetGUID(MFGuids.MF_MT_SUBTYPE, subtype);
        a.SetUINT64(MFGuids.MF_MT_FRAME_SIZE, MF.Pack((uint)Width, (uint)Height));
        a.SetUINT64(MFGuids.MF_MT_FRAME_RATE, MF.Pack((uint)Fps, 1));
        a.SetUINT64(MFGuids.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
        a.SetUINT32(MFGuids.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
        return type;
    }

    private bool SetCodecValue(in Guid api, Variant value)
    {
        if (_codec == null) return false;
        int hr = _codec.SetValue(api, ref value);
        Trace?.Invoke($"codecapi {api} -> 0x{hr:X8}");
        return hr >= 0;
    }

    /// <summary>Changes the target bitrate of the running stream. False when this encoder cannot do it live.</summary>
    public bool SetBitrate(int bitrateKbps)
    {
        uint bps = (uint)Math.Clamp((long)bitrateKbps * 1000, 100_000, 400_000_000);
        return SetCodecValue(MFGuids.CODECAPI_AVEncCommonMeanBitRate, Variant.UInt32(bps));
    }

    /// <summary>Makes the next encoded frame an IDR frame. False when the encoder does not support the request.</summary>
    public bool ForceKeyframe()
    {
        return CanForceKeyframe && SetCodecValue(MFGuids.CODECAPI_AVEncVideoForceKeyFrame, Variant.UInt32(1));
    }

    /// <summary>
    /// Submits one BGRA frame (<paramref name="stride"/> bytes per row, at least Width×Height pixels). The caller must
    /// have seen <see cref="WantsInput"/> true. Output appears through <see cref="Pump"/>.
    /// </summary>
    public void Encode(byte* bgra, int stride, long timestampMicros)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!WantsInput) throw new InvalidOperationException("The encoder is not ready for input.");
        uint size = (uint)(Width * Height * 3 / 2);
        MF.Check(MF.MFCreateAlignedMemoryBuffer(size, MF.MF_64_BYTE_ALIGNMENT, out var buffer), "MFCreateAlignedMemoryBuffer");
        IMFSample? sample = null;
        try
        {
            MF.Check(buffer.Lock(out var p, out _, out _), "lock input buffer");
            try
            {
                byte* d = (byte*)p;
                PixelConvert.BgraToNv12(bgra, stride, Width, Height, d, Width, d + Width * Height, Width);
            }
            finally { buffer.Unlock(); }
            buffer.SetCurrentLength(size);
            MF.Check(MF.MFCreateSample(out sample), "MFCreateSample");
            sample.AddBuffer(buffer);
            sample.SetSampleTime(timestampMicros * 10);
            sample.SetSampleDuration(10_000_000L / Fps);
            int hr = _mft.ProcessInput(0, sample, 0);
            if (hr == MF.MF_E_NOTACCEPTING && !_async)
            {
                // The software encoder holds output we have not collected yet.
                var pending = new List<EncodedFrame>();
                Pump(pending);
                _pendingOutput.AddRange(pending);
                hr = _mft.ProcessInput(0, sample, 0);
            }
            MF.Check(hr, "ProcessInput");
            if (_async) _needInput--;
        }
        finally
        {
            MF.Release(sample);
            MF.Release(buffer);
        }
    }

    /// <summary>Convenience for tests: encodes a packed BGRA image.</summary>
    public void Encode(ReadOnlySpan<byte> bgra, int stride, long timestampMicros)
    {
        fixed (byte* p = bgra) Encode(p, stride, timestampMicros);
    }

    private readonly List<EncodedFrame> _pendingOutput = new();

    /// <summary>Collects whatever the encoder has finished; call it often (every frame interval at least).</summary>
    public void Pump(List<EncodedFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingOutput.Count > 0)
        {
            output.AddRange(_pendingOutput);
            _pendingOutput.Clear();
        }
        if (_async)
        {
            while (true)
            {
                int hr = _events!.GetEvent(MF.MF_EVENT_FLAG_NO_WAIT, out var evt);
                if (hr == MF.MF_E_NO_EVENTS_AVAILABLE) break;
                MF.Check(hr, "GetEvent");
                evt.GetType(out var type);
                evt.GetStatus(out var status);
                MF.Release(evt);
                MF.Check(status, "encoder event");
                switch (type)
                {
                    case MF.METransformNeedInput: _needInput++; break;
                    case MF.METransformHaveOutput: ProcessOutput(output); break;
                }
            }
        }
        else
        {
            while (ProcessOutput(output)) { }
        }
    }

    /// <summary>Asks the encoder to finish everything it holds (end of a test, not used while streaming).</summary>
    public void Drain(List<EncodedFrame> output, int timeoutMs = 2000)
    {
        MF.Check(_mft.ProcessMessage(MF.MFT_MESSAGE_COMMAND_DRAIN, UIntPtr.Zero), "drain");
        if (!_async) { while (ProcessOutput(output)) { } return; }
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int hr = _events!.GetEvent(MF.MF_EVENT_FLAG_NO_WAIT, out var evt);
            if (hr == MF.MF_E_NO_EVENTS_AVAILABLE) { Thread.Sleep(1); continue; }
            MF.Check(hr, "GetEvent");
            evt.GetType(out var type);
            MF.Release(evt);
            if (type == MF.METransformHaveOutput) ProcessOutput(output);
            else if (type == MF.METransformDrainComplete) break;
        }
    }

    private bool ProcessOutput(List<EncodedFrame> output)
    {
        var buf = new MFT_OUTPUT_DATA_BUFFER { StreamId = 0 };
        if (!_providesSamples) buf.Sample = Marshal.GetComInterfaceForObject(ResetOutputSample(), typeof(IMFSample));
        int hr;
        try { hr = _mft.ProcessOutput(0, 1, &buf, out _); }
        finally { if (!_providesSamples) Marshal.Release(buf.Sample); }
        if (buf.Events != IntPtr.Zero) Marshal.Release(buf.Events);
        if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
        if (hr == MF.MF_E_TRANSFORM_STREAM_CHANGE)
        {
            if (_mft.GetOutputAvailableType(0, 0, out var type) >= 0)
            {
                _mft.SetOutputType(0, type, 0);
                MF.Release(type);
            }
            _sequenceHeader = null;
            return true;
        }
        MF.Check(hr, "ProcessOutput");
        IMFSample sample;
        bool release = false;
        if (_providesSamples)
        {
            if (buf.Sample == IntPtr.Zero) return true;
            sample = (IMFSample)Marshal.GetObjectForIUnknown(buf.Sample);
            Marshal.Release(buf.Sample);
            release = true;
        }
        else sample = _outputSample!;
        try { output.Add(ReadFrame(sample)); }
        finally { if (release) MF.Release(sample); }
        return true;
    }

    private EncodedFrame ReadFrame(IMFSample sample)
    {
        MF.Check(sample.ConvertToContiguousBuffer(out var buffer), "ConvertToContiguousBuffer");
        byte[] data;
        try
        {
            MF.Check(buffer.Lock(out var p, out _, out var length), "lock output buffer");
            try
            {
                data = new byte[length];
                Marshal.Copy(p, data, 0, (int)length);
            }
            finally { buffer.Unlock(); }
        }
        finally { MF.Release(buffer); }
        sample.GetSampleTime(out var time);
        bool clean = ((IMFAttributes)sample).GetUINT32(MFGuids.MFSampleExtension_CleanPoint, out var cp) >= 0 && cp != 0;

        data = AnnexB.Strip(data, AnnexB.NalFiller); // CBR padding from hardware encoders: bandwidth for nothing
        AnnexB.Inspect(data, out bool idr, out bool sps, out bool pps);
        bool keyframe = idr || clean;
        if (sps && pps) _sequenceHeader = AnnexB.ExtractParameterSets(data);
        else if (keyframe)
        {
            var header = _sequenceHeader ??= ReadSequenceHeader();
            if (header != null)
            {
                var joined = new byte[header.Length + data.Length];
                header.CopyTo(joined, 0);
                data.CopyTo(joined, header.Length);
                data = joined;
            }
        }
        return new EncodedFrame(data, keyframe, time / 10);
    }

    private byte[]? ReadSequenceHeader()
    {
        if (_mft.GetOutputCurrentType(0, out var type) < 0) return null;
        try
        {
            var blob = MF.GetBlob((IMFAttributes)type, MFGuids.MF_MT_MPEG_SEQUENCE_HEADER);
            return blob is { Length: > 4 } ? blob : null;
        }
        finally { MF.Release(type); }
    }

    /// <summary>The reused output sample must look empty again before each ProcessOutput, or the MFT refuses it.</summary>
    private IMFSample ResetOutputSample()
    {
        var sample = _outputSample!;
        if (sample.GetBufferByIndex(0, out var buffer) >= 0)
        {
            buffer.SetCurrentLength(0);
            MF.Release(buffer);
        }
        return sample;
    }

    private void Teardown()
    {
        try { _mft?.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_OF_STREAM, UIntPtr.Zero); } catch { }
        try { _mft?.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_END_STREAMING, UIntPtr.Zero); } catch { }
        MF.Release(_outputSample);
        _outputSample = null;
        _events = null;
        _codec = null;
        MF.Release(_mft);
        _mft = null!;
        try { _activate?.ShutdownObject(); } catch { }
        MF.Release(_activate);
        _activate = null;
        _needInput = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Teardown();
    }
}

/// <summary>Starts Media Foundation once per process; it stays up until the process exits.</summary>
public static class MediaRuntime
{
    private static readonly object Gate = new();
    private static bool _started;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started) return;
            MF.Check(MF.MFStartup(MF.MF_VERSION, 0), "MFStartup");
            _started = true;
        }
    }
}
