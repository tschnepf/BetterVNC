using System.Runtime.InteropServices;
using BetterVnc.Media.Interop;

namespace BetterVnc.Media;

/// <summary>
/// A decoded picture in NV12: either in system memory (<see cref="Y"/>, <see cref="UV"/>) or, from a hardware decoder,
/// a slice of a Direct3D 11 texture (<see cref="IsTexture"/>; hand it to <see cref="VideoRenderer.Present(in DecodedFrame)"/>).
/// Pointers and textures are valid only inside the callback that delivers the frame.
/// </summary>
public readonly struct DecodedFrame
{
    public DecodedFrame(IntPtr y, IntPtr uv, int stride, int width, int height, long timestampMicros)
    {
        Y = y; UV = uv; Stride = stride; Width = width; Height = height; TimestampMicros = timestampMicros;
    }

    internal DecodedFrame(IntPtr texture, uint subresource, int offsetX, int offsetY, int width, int height, long timestampMicros)
    {
        Texture = texture; Subresource = subresource; OffsetX = offsetX; OffsetY = offsetY;
        Width = width; Height = height; TimestampMicros = timestampMicros;
    }

    /// <summary>First visible luma byte (zero for a texture frame).</summary>
    public IntPtr Y { get; }
    /// <summary>First visible interleaved chroma pair (zero for a texture frame).</summary>
    public IntPtr UV { get; }
    public int Stride { get; }
    public int Width { get; }
    public int Height { get; }
    public long TimestampMicros { get; }

    /// <summary>The picture lives on the GPU (hardware decode); <see cref="ToBgrx"/> is not available.</summary>
    public bool IsTexture => Texture != IntPtr.Zero;
    /// <summary>ID3D11Texture2D (NV12, often a texture array the decoder owns) and the slice holding this picture.</summary>
    internal IntPtr Texture { get; }
    internal uint Subresource { get; }
    /// <summary>Where the visible picture starts inside the texture (the decoder's surfaces are padded to whole macroblocks).</summary>
    internal int OffsetX { get; }
    internal int OffsetY { get; }

    /// <summary>Writes the picture as BGRX, Width*4 bytes per row, into <paramref name="bgrx"/> (at least Width*Height*4 bytes).</summary>
    public unsafe void ToBgrx(byte[] bgrx)
    {
        if (IsTexture) throw new InvalidOperationException("The picture is a GPU texture; present it with VideoRenderer.");
        if (bgrx.Length < Width * Height * 4) throw new ArgumentException("Buffer too small.");
        fixed (byte* d = bgrx)
            PixelConvert.Nv12ToBgrx((byte*)Y, Stride, (byte*)UV, Stride, Width, Height, d, Width * 4);
    }
}

/// <summary>
/// H.264 decoding with Microsoft's Media Foundation decoder in low-latency mode: every access unit fed in comes
/// out as a picture right away (the agent sends no B-frames). Resolution changes mid-stream (a new SPS) are
/// followed automatically; <see cref="Width"/> and <see cref="Height"/> are the visible size of the last picture.
/// Given a <see cref="D3D11VideoDevice"/>, the decoder gets that device's manager (MFT_MESSAGE_SET_D3D_MANAGER) and
/// decodes with DXVA on the GPU, delivering texture frames; if the decoder or the driver refuses, it decodes in
/// software exactly as without a device (<see cref="IsHardware"/>, <see cref="HardwareFailure"/>).
/// All methods must be called from one thread.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    public delegate void FrameHandler(in DecodedFrame frame);

    private IMFActivate? _activate;
    private IMFTransform _mft = null!;
    private IMFSample? _outputSample;
    private bool _providesSamples;
    private int _codedWidth, _codedHeight, _stride, _offsetX, _offsetY;
    private bool _disposed;

    public string Name { get; private set; } = "";
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>The decoder accepted the Direct3D device and decodes on the GPU (DXVA).</summary>
    public bool IsHardware { get; private set; }
    /// <summary>Why a device was given but decoding runs in software; null otherwise.</summary>
    public string? HardwareFailure { get; private set; }

    public H264Decoder() : this(null) { }

    public H264Decoder(D3D11VideoDevice? device)
    {
        MediaRuntime.EnsureStarted();
        if (device != null)
        {
            try
            {
                Initialize(Activate(), device);
                return;
            }
            catch (Exception ex)
            {
                HardwareFailure = ex.Message;
                Teardown();
                IsHardware = false;
            }
        }
        try { Initialize(Activate(), null); }
        catch { Teardown(); throw; }
    }

    private static IMFActivate Activate()
    {
        var input = new MFT_REGISTER_TYPE_INFO { MajorType = MFGuids.MFMediaType_Video, Subtype = MFGuids.MFVideoFormat_H264 };
        var output = new MFT_REGISTER_TYPE_INFO { MajorType = MFGuids.MFMediaType_Video, Subtype = MFGuids.MFVideoFormat_NV12 };
        MF.Check(MF.MFTEnumEx(MFGuids.MFT_CATEGORY_VIDEO_DECODER, MF.MFT_ENUM_FLAG_SYNCMFT | MF.MFT_ENUM_FLAG_SORTANDFILTER, &input, &output, out var array, out var count), "MFTEnumEx");
        if (count == 0) throw new InvalidOperationException("No H.264 decoder is registered with Media Foundation.");
        var activates = MF.TakeActivates(array, count);
        for (int i = 1; i < activates.Length; i++) MF.Release(activates[i]);
        return activates[0];
    }

    private void Initialize(IMFActivate activate, D3D11VideoDevice? device)
    {
        _activate = activate;
        Name = MF.GetString((IMFAttributes)activate, MFGuids.MFT_FRIENDLY_NAME_Attribute) ?? "H.264 decoder";
        MF.Check(activate.ActivateObject(MFGuids.IID_IMFTransform, out var p), "activate decoder");
        _mft = (IMFTransform)Marshal.GetObjectForIUnknown(p);
        Marshal.Release(p);
        bool d3d11Aware = false;
        if (_mft.GetAttributes(out var attrs) >= 0 && attrs != null)
        {
            attrs.SetUINT32(MFGuids.MF_LOW_LATENCY, 1);
            d3d11Aware = attrs.GetUINT32(MFGuids.MF_SA_D3D11_AWARE, out var aware) >= 0 && aware != 0;
            MF.Release(attrs);
        }
        if (_mft is ICodecAPI codec)
        {
            var v = Variant.Bool(true);
            codec.SetValue(MFGuids.CODECAPI_AVLowLatencyMode, ref v);
        }
        if (device != null)
        {
            if (!d3d11Aware) throw new InvalidOperationException($"{Name} does not take a Direct3D 11 device");
            var manager = device.AcquireManager();
            if (manager == IntPtr.Zero)
                throw new InvalidOperationException(device.SupportsVideo ? $"{device.AdapterName} cannot sample NV12 textures" : $"{device.AdapterName} has no video support");
            int hr;
            try { hr = _mft.ProcessMessage(MF.MFT_MESSAGE_SET_D3D_MANAGER, (UIntPtr)(ulong)manager); }
            finally { Marshal.Release(manager); }
            MF.Check(hr, "MFT_MESSAGE_SET_D3D_MANAGER");
            IsHardware = true;
        }

        MF.Check(MF.MFCreateMediaType(out var type), "MFCreateMediaType");
        var a = (IMFAttributes)type;
        a.SetGUID(MFGuids.MF_MT_MAJOR_TYPE, MFGuids.MFMediaType_Video);
        a.SetGUID(MFGuids.MF_MT_SUBTYPE, MFGuids.MFVideoFormat_H264);
        a.SetUINT32(MFGuids.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
        int hrType = _mft.SetInputType(0, type, 0);
        MF.Release(type);
        MF.Check(hrType, "set H.264 input type");
        NegotiateOutput();
        MF.Check(_mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, UIntPtr.Zero), "begin streaming");
        MF.Check(_mft.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_START_OF_STREAM, UIntPtr.Zero), "start of stream");
    }

    /// <summary>Picks the NV12 output type the decoder proposes and reads the picture geometry from it.</summary>
    private void NegotiateOutput()
    {
        int hr = MF.MF_E_NO_MORE_TYPES;
        for (uint i = 0; _mft.GetOutputAvailableType(0, i, out var candidate) >= 0; i++)
        {
            var ca = (IMFAttributes)candidate;
            bool nv12 = ca.GetGUID(MFGuids.MF_MT_SUBTYPE, out var sub) >= 0 && sub == MFGuids.MFVideoFormat_NV12;
            if (nv12)
            {
                hr = _mft.SetOutputType(0, candidate, 0);
                if (hr >= 0) ReadGeometry(ca);
            }
            MF.Release(candidate);
            if (nv12) break;
        }
        MF.Check(hr, "set NV12 output type");

        MF.Check(_mft.GetOutputStreamInfo(0, out var info), "GetOutputStreamInfo");
        _providesSamples = (info.Flags & MF.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
        MF.Release(_outputSample);
        _outputSample = null;
        if (!_providesSamples)
        {
            uint size = Math.Max(info.Size, (uint)(_codedWidth * _codedHeight * 3 / 2));
            MF.Check(MF.MFCreateSample(out var sample), "MFCreateSample");
            MF.Check(MF.MFCreateAlignedMemoryBuffer(size, MF.MF_64_BYTE_ALIGNMENT, out var buffer), "MFCreateAlignedMemoryBuffer");
            sample.AddBuffer(buffer);
            MF.Release(buffer);
            _outputSample = sample;
        }
    }

    private void ReadGeometry(IMFAttributes type)
    {
        type.GetUINT64(MFGuids.MF_MT_FRAME_SIZE, out var size);
        _codedWidth = (int)(size >> 32);
        _codedHeight = (int)(size & 0xFFFFFFFF);
        _stride = type.GetUINT32(MFGuids.MF_MT_DEFAULT_STRIDE, out var stride) >= 0 && stride > 0 ? (int)stride : _codedWidth;
        _offsetX = _offsetY = 0;
        Width = _codedWidth;
        Height = _codedHeight;
        var aperture = MF.GetBlob(type, MFGuids.MF_MT_MINIMUM_DISPLAY_APERTURE);
        if (aperture is { Length: >= 16 })
        {
            fixed (byte* p = aperture)
            {
                var area = *(MFVideoArea*)p;
                if (area.Width > 0 && area.Height > 0 && area.OffsetX >= 0 && area.OffsetY >= 0 && area.OffsetX + area.Width <= _codedWidth && area.OffsetY + area.Height <= _codedHeight)
                {
                    _offsetX = area.OffsetX & ~1;
                    _offsetY = area.OffsetY & ~1;
                    Width = area.Width;
                    Height = area.Height;
                }
            }
        }
    }

    /// <summary>
    /// Feeds one Annex B access unit and delivers the pictures that come out (usually exactly one). Returns the
    /// number of pictures delivered.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> annexB, long timestampMicros, FrameHandler onFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (annexB.Length == 0) return 0;
        MF.Check(MF.MFCreateMemoryBuffer((uint)annexB.Length, out var buffer), "MFCreateMemoryBuffer");
        IMFSample? sample = null;
        int delivered = 0;
        try
        {
            MF.Check(buffer.Lock(out var p, out _, out _), "lock input buffer");
            try { annexB.CopyTo(new Span<byte>((void*)p, annexB.Length)); }
            finally { buffer.Unlock(); }
            buffer.SetCurrentLength((uint)annexB.Length);
            MF.Check(MF.MFCreateSample(out sample), "MFCreateSample");
            sample.AddBuffer(buffer);
            sample.SetSampleTime(timestampMicros * 10);
            int hr = _mft.ProcessInput(0, sample, 0);
            if (hr == MF.MF_E_NOTACCEPTING)
            {
                delivered += DrainOutput(onFrame);
                hr = _mft.ProcessInput(0, sample, 0);
            }
            MF.Check(hr, "ProcessInput");
        }
        finally
        {
            MF.Release(sample);
            MF.Release(buffer);
        }
        delivered += DrainOutput(onFrame);
        return delivered;
    }

    private int DrainOutput(FrameHandler onFrame)
    {
        int delivered = 0;
        while (true)
        {
            var buf = new MFT_OUTPUT_DATA_BUFFER { StreamId = 0 };
            if (!_providesSamples) buf.Sample = Marshal.GetComInterfaceForObject(ResetOutputSample(), typeof(IMFSample));
            int hr;
            try { hr = _mft.ProcessOutput(0, 1, &buf, out _); }
            finally { if (!_providesSamples) Marshal.Release(buf.Sample); }
            if (buf.Events != IntPtr.Zero) Marshal.Release(buf.Events);
            if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT) return delivered;
            if (hr == MF.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                NegotiateOutput();
                continue;
            }
            MF.Check(hr, "ProcessOutput");
            IMFSample sample;
            bool release = false;
            if (_providesSamples)
            {
                if (buf.Sample == IntPtr.Zero) continue;
                sample = (IMFSample)Marshal.GetObjectForIUnknown(buf.Sample);
                Marshal.Release(buf.Sample);
                release = true;
            }
            else sample = _outputSample!;
            try { Deliver(sample, onFrame); }
            finally { if (release) MF.Release(sample); }
            delivered++;
        }
    }

    private void Deliver(IMFSample sample, FrameHandler onFrame)
    {
        sample.GetSampleTime(out var time);
        MF.Check(sample.GetBufferByIndex(0, out var buffer), "GetBufferByIndex");
        try
        {
            // A hardware decoder hands out one slice of its texture array; it goes back to the decoder's pool when the
            // sample is released, so the consumer copies it inside the callback.
            if (buffer is IMFDXGIBuffer dxgi && dxgi.GetResource(D3D11.IID_ID3D11Texture2D, out var texture) >= 0 && texture != IntPtr.Zero)
            {
                try
                {
                    dxgi.GetSubresourceIndex(out var index);
                    var gpuFrame = new DecodedFrame(texture, index, _offsetX, _offsetY, Width, Height, time / 10);
                    onFrame(in gpuFrame);
                }
                finally { Marshal.Release(texture); }
                return;
            }

            IntPtr scan0;
            int pitch;
            long planeBytes;
            var buffer2d = buffer as IMF2DBuffer;
            if (buffer2d != null && buffer2d.Lock2D(out scan0, out pitch) >= 0)
            {
                buffer2d.GetContiguousLength(out var contiguous);
                planeBytes = contiguous;
            }
            else
            {
                buffer2d = null;
                MF.Check(buffer.Lock(out scan0, out _, out var length), "lock output buffer");
                pitch = _stride;
                planeBytes = length;
            }
            try
            {
                // Where the UV plane starts: the packed size tells the real coded height even if the type lied.
                int codedHeight = _codedHeight;
                if (pitch > 0 && planeBytes > 0)
                {
                    int derived = (int)(planeBytes * 2 / (3L * (buffer2d != null ? _codedWidth : pitch)));
                    if (derived >= Height && derived <= _codedHeight + 64) codedHeight = derived;
                }
                byte* y = (byte*)scan0 + (long)_offsetY * pitch + _offsetX;
                byte* uv = (byte*)scan0 + (long)codedHeight * pitch + (long)(_offsetY / 2) * pitch + _offsetX;
                var frame = new DecodedFrame((IntPtr)y, (IntPtr)uv, pitch, Width, Height, time / 10);
                onFrame(in frame);
            }
            finally
            {
                if (buffer2d != null) buffer2d.Unlock2D();
                else buffer.Unlock();
            }
        }
        finally { MF.Release(buffer); }
    }

    /// <summary>Asks for every picture the decoder still holds; the end of a stream, not something to do per frame (the decoder stops accepting input afterwards).</summary>
    public int Drain(FrameHandler onFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MF.Check(_mft.ProcessMessage(MF.MFT_MESSAGE_COMMAND_DRAIN, UIntPtr.Zero), "drain");
        return DrainOutput(onFrame);
    }

    /// <summary>Drops whatever the decoder holds (after a gap in the stream, before the next key frame).</summary>
    public void Flush()
    {
        if (_disposed) return;
        _mft.ProcessMessage(MF.MFT_MESSAGE_COMMAND_FLUSH, UIntPtr.Zero);
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
        // Hand the device back before the decoder goes (a decoder that keeps it pins the GPU surfaces it allocated).
        if (IsHardware) try { _mft?.ProcessMessage(MF.MFT_MESSAGE_SET_D3D_MANAGER, UIntPtr.Zero); } catch { }
        MF.Release(_outputSample);
        _outputSample = null;
        MF.Release(_mft);
        _mft = null!;
        try { _activate?.ShutdownObject(); } catch { }
        MF.Release(_activate);
        _activate = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Teardown();
    }
}
