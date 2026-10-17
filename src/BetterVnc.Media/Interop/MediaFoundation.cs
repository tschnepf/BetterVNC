using System.Runtime.InteropServices;

namespace BetterVnc.Media.Interop;

// Hand-written COM interop for the slice of Media Foundation the H.264 encoder and decoder wrappers use.
// Interfaces that derive from IMFAttributes repeat its 33 vtable slots as placeholders (COM interop does not
// inherit vtables); callers cast the object to IMFAttributes (a QueryInterface) when they need attributes.
// GUIDs were checked against the Windows SDK headers; vtable orders against mfobjects.h / mftransform.h.

internal static unsafe class MF
{
    public const uint MF_VERSION = 0x00020070;
    public const uint MF_EVENT_FLAG_NO_WAIT = 1;
    public const int S_OK = 0, S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_TRANSFORM_TYPE_NOT_SET = unchecked((int)0xC00D6D60);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    public const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
    public const int MF_E_ATTRIBUTENOTFOUND = unchecked((int)0xC00D36E6);
    public const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);

    public const uint METransformNeedInput = 601, METransformHaveOutput = 602, METransformDrainComplete = 603, METransformMarker = 604;
    public const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
    public const uint MFT_OUTPUT_DATA_BUFFER_FORMAT_CHANGE = 0x100;
    public const uint MFT_ENUM_FLAG_SYNCMFT = 1, MFT_ENUM_FLAG_ASYNCMFT = 2, MFT_ENUM_FLAG_HARDWARE = 4, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    public const uint MFT_MESSAGE_COMMAND_FLUSH = 0, MFT_MESSAGE_COMMAND_DRAIN = 1;
    public const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000, MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    public const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002, MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
    public const uint MFVideoInterlace_Progressive = 2;
    public const uint MF_64_BYTE_ALIGNMENT = 63;
    public const ushort VT_BOOL = 11, VT_UI4 = 19, VT_UI8 = 21;
    public const uint eAVEncCommonRateControlMode_CBR = 0, eAVEncCommonRateControlMode_LowDelayVBR = 4;
    public const uint eAVEncH264VProfile_Base = 66, eAVEncH264VProfile_Main = 77, eAVEncH264VProfile_High = 100;

    [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] public static extern int MFShutdown();
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);
    [DllImport("mfplat.dll")] public static extern int MFCreateAlignedMemoryBuffer(uint maxLength, uint alignmentFlags, out IMFMediaBuffer buffer);
    [DllImport("mfplat.dll")] public static extern int MFTEnumEx(in Guid category, uint flags, MFT_REGISTER_TYPE_INFO* input, MFT_REGISTER_TYPE_INFO* output, out IntPtr activates, out uint count);

    public static ulong Pack(uint hi, uint lo) => ((ulong)hi << 32) | lo;

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new MediaException(what, hr);
    }

    /// <summary>Reads a string attribute, or null when it is absent.</summary>
    public static string? GetString(IMFAttributes attrs, in Guid key)
    {
        if (attrs.GetAllocatedString(key, out var p, out _) < 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static byte[]? GetBlob(IMFAttributes attrs, in Guid key)
    {
        if (attrs.GetAllocatedBlob(key, out var p, out var size) < 0 || p == IntPtr.Zero) return null;
        try
        {
            var data = new byte[size];
            Marshal.Copy(p, data, 0, (int)size);
            return data;
        }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>Converts the IMFActivate** array MFTEnumEx returns into RCWs and frees the native array.</summary>
    public static IMFActivate[] TakeActivates(IntPtr array, uint count)
    {
        var result = new IMFActivate[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                var p = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                result[i] = (IMFActivate)Marshal.GetObjectForIUnknown(p);
                Marshal.Release(p);
            }
        }
        finally { Marshal.FreeCoTaskMem(array); }
        return result;
    }

    public static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}

/// <summary>A Media Foundation or Direct3D call failed; <see cref="Exception.HResult"/> carries the code.</summary>
public sealed class MediaException : Exception
{
    public MediaException(string what, int hr) : base($"{what} failed (0x{hr:X8}{Describe(hr)})") { HResult = hr; }

    private static string Describe(int hr) => hr switch
    {
        MF.MF_E_NOTACCEPTING => ", MF_E_NOTACCEPTING",
        MF.MF_E_INVALIDMEDIATYPE => ", MF_E_INVALIDMEDIATYPE",
        MF.MF_E_TRANSFORM_TYPE_NOT_SET => ", MF_E_TRANSFORM_TYPE_NOT_SET",
        MF.MF_E_ATTRIBUTENOTFOUND => ", MF_E_ATTRIBUTENOTFOUND",
        unchecked((int)0x80070005) => ", access denied",
        unchecked((int)0x887A0004) => ", DXGI_ERROR_UNSUPPORTED",
        unchecked((int)0x887A0022) => ", DXGI_ERROR_NOT_CURRENTLY_AVAILABLE",
        unchecked((int)0x887A0026) => ", DXGI_ERROR_ACCESS_LOST",
        _ => "",
    };
}

internal static class MFGuids
{
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    public static readonly Guid MF_MT_MINIMUM_DISPLAY_APERTURE = new("d7388766-18fe-48c6-a177-ee894867c8c4");

    public static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid MF_SA_D3D11_AWARE = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid MFT_ENUM_HARDWARE_URL_Attribute = new("2fb866ac-b078-4942-ab6c-003d05cda674");
    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    public static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid CODECAPI_AVEncCommonMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid CODECAPI_AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid CODECAPI_AVEncVideoMaxNumRefFrame = new("964829ed-94f9-43b4-b74d-ef40944b69a0");

    public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_REGISTER_TYPE_INFO
{
    public Guid MajorType;
    public Guid Subtype;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_DATA_BUFFER
{
    public uint StreamId;
    public IntPtr Sample;   // IMFSample*
    public uint Status;
    public IntPtr Events;   // IMFCollection*
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_STREAM_INFO
{
    public uint Flags;
    public uint Size;
    public uint Alignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_INPUT_STREAM_INFO
{
    public long MaxLatency;
    public uint Flags;
    public uint Size;
    public uint MaxLookahead;
    public uint Alignment;
}

/// <summary>Enough of a VARIANT for ICodecAPI (VT_UI4 / VT_BOOL / VT_UI8); 24 bytes on x64 like the real one.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Variant
{
    public ushort Type;
    public ushort R1, R2, R3;
    public ulong Data;
    public ulong Data2;

    public static Variant UInt32(uint value) => new() { Type = MF.VT_UI4, Data = value };
    public static Variant Bool(bool value) => new() { Type = MF.VT_BOOL, Data = value ? 0xFFFFu : 0u };
    public uint AsUInt32 => (uint)Data;
}

/// <summary>MFVideoArea: the visible part of a decoded picture (MF_MT_MINIMUM_DISPLAY_APERTURE).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFVideoArea
{
    public ushort OffsetXFract; public short OffsetX;
    public ushort OffsetYFract; public short OffsetY;
    public int Width;
    public int Height;
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid key, IntPtr value);
    [PreserveSig] int GetItemType(in Guid key, out int type);
    [PreserveSig] int CompareItem(in Guid key, IntPtr value, out int result);
    [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] int GetDouble(in Guid key, out double value);
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, IntPtr value, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, byte* buffer, uint bufferSize, out uint blobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid key, out IntPtr buffer, out uint size);
    [PreserveSig] int GetUnknown(in Guid key, in Guid riid, out IntPtr ppv);
    [PreserveSig] int SetItem(in Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    [PreserveSig] int SetDouble(in Guid key, double value);
    [PreserveSig] int SetGUID(in Guid key, in Guid value);
    [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(in Guid key, byte* buffer, uint size);
    [PreserveSig] int SetUnknown(in Guid key, IntPtr unknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

/// <summary>Only ever used through <see cref="IMFAttributes"/> (cast) or passed back to Media Foundation.</summary>
[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    void _Attr0();
    void _Attr1();
    void _Attr2();
    void _Attr3();
    void _Attr4();
    void _Attr5();
    void _Attr6();
    void _Attr7();
    void _Attr8();
    void _Attr9();
    void _Attr10();
    void _Attr11();
    void _Attr12();
    void _Attr13();
    void _Attr14();
    void _Attr15();
    void _Attr16();
    void _Attr17();
    void _Attr18();
    void _Attr19();
    void _Attr20();
    void _Attr21();
    void _Attr22();
    void _Attr23();
    void _Attr24();
    void _Attr25();
    void _Attr26();
    void _Attr27();
    void _Attr28();
    void _Attr29();
    [PreserveSig] int GetMajorType(out Guid majorType);
    [PreserveSig] int IsCompressedFormat(out int compressed);
    [PreserveSig] int IsEqual(IMFMediaType other, out uint flags);
    [PreserveSig] int GetRepresentation(Guid representation, out IntPtr value);
    [PreserveSig] int FreeRepresentation(Guid representation, IntPtr value);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    void _Attr0();
    void _Attr1();
    void _Attr2();
    void _Attr3();
    void _Attr4();
    void _Attr5();
    void _Attr6();
    void _Attr7();
    void _Attr8();
    void _Attr9();
    void _Attr10();
    void _Attr11();
    void _Attr12();
    void _Attr13();
    void _Attr14();
    void _Attr15();
    void _Attr16();
    void _Attr17();
    void _Attr18();
    void _Attr19();
    void _Attr20();
    void _Attr21();
    void _Attr22();
    void _Attr23();
    void _Attr24();
    void _Attr25();
    void _Attr26();
    void _Attr27();
    void _Attr28();
    void _Attr29();
    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(uint index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint length);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate
{
    void _Attr0();
    void _Attr1();
    void _Attr2();
    void _Attr3();
    void _Attr4();
    void _Attr5();
    void _Attr6();
    void _Attr7();
    void _Attr8();
    void _Attr9();
    void _Attr10();
    void _Attr11();
    void _Attr12();
    void _Attr13();
    void _Attr14();
    void _Attr15();
    void _Attr16();
    void _Attr17();
    void _Attr18();
    void _Attr19();
    void _Attr20();
    void _Attr21();
    void _Attr22();
    void _Attr23();
    void _Attr24();
    void _Attr25();
    void _Attr26();
    void _Attr27();
    void _Attr28();
    void _Attr29();
    [PreserveSig] int ActivateObject(in Guid riid, out IntPtr ppv);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[ComImport, Guid("df598932-f10c-4e39-bba2-c308f101daa3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEvent
{
    void _Attr0();
    void _Attr1();
    void _Attr2();
    void _Attr3();
    void _Attr4();
    void _Attr5();
    void _Attr6();
    void _Attr7();
    void _Attr8();
    void _Attr9();
    void _Attr10();
    void _Attr11();
    void _Attr12();
    void _Attr13();
    void _Attr14();
    void _Attr15();
    void _Attr16();
    void _Attr17();
    void _Attr18();
    void _Attr19();
    void _Attr20();
    void _Attr21();
    void _Attr22();
    void _Attr23();
    void _Attr24();
    void _Attr25();
    void _Attr26();
    void _Attr27();
    void _Attr28();
    void _Attr29();
    [PreserveSig] int GetType(out uint type);
    [PreserveSig] int GetExtendedType(out Guid extendedType);
    [PreserveSig] int GetStatus(out int status);
    [PreserveSig] int GetValue(IntPtr value);
}

[ComImport, Guid("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEventGenerator
{
    [PreserveSig] int GetEvent(uint flags, out IMFMediaEvent evt);
    [PreserveSig] int BeginGetEvent(IntPtr callback, IntPtr state);
    [PreserveSig] int EndGetEvent(IntPtr result, out IMFMediaEvent evt);
    [PreserveSig] int QueueEvent(uint type, in Guid extendedType, int status, IntPtr value);
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint length);
    [PreserveSig] int SetCurrentLength(uint length);
    [PreserveSig] int GetMaxLength(out uint length);
}

[ComImport, Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMF2DBuffer
{
    [PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
    [PreserveSig] int Unlock2D();
    [PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
    [PreserveSig] int IsContiguousFormat(out int contiguous);
    [PreserveSig] int GetContiguousLength(out uint length);
    [PreserveSig] int ContiguousCopyTo(IntPtr destination, uint size);
    [PreserveSig] int ContiguousCopyFrom(IntPtr source, uint size);
}

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMFTransform
{
    [PreserveSig] int GetStreamLimits(out uint inputMin, out uint inputMax, out uint outputMin, out uint outputMax);
    [PreserveSig] int GetStreamCount(out uint inputs, out uint outputs);
    [PreserveSig] int GetStreamIDs(uint inputSize, uint* inputs, uint outputSize, uint* outputs);
    [PreserveSig] int GetInputStreamInfo(uint id, out MFT_INPUT_STREAM_INFO info);
    [PreserveSig] int GetOutputStreamInfo(uint id, out MFT_OUTPUT_STREAM_INFO info);
    [PreserveSig] int GetAttributes(out IMFAttributes attributes);
    [PreserveSig] int GetInputStreamAttributes(uint id, out IMFAttributes attributes);
    [PreserveSig] int GetOutputStreamAttributes(uint id, out IMFAttributes attributes);
    [PreserveSig] int DeleteInputStream(uint id);
    [PreserveSig] int AddInputStreams(uint count, uint* ids);
    [PreserveSig] int GetInputAvailableType(uint id, uint index, out IMFMediaType type);
    [PreserveSig] int GetOutputAvailableType(uint id, uint index, out IMFMediaType type);
    [PreserveSig] int SetInputType(uint id, IMFMediaType? type, uint flags);
    [PreserveSig] int SetOutputType(uint id, IMFMediaType? type, uint flags);
    [PreserveSig] int GetInputCurrentType(uint id, out IMFMediaType type);
    [PreserveSig] int GetOutputCurrentType(uint id, out IMFMediaType type);
    [PreserveSig] int GetInputStatus(uint id, out uint flags);
    [PreserveSig] int GetOutputStatus(out uint flags);
    [PreserveSig] int SetOutputBounds(long lower, long upper);
    [PreserveSig] int ProcessEvent(uint id, IMFMediaEvent evt);
    [PreserveSig] int ProcessMessage(uint message, UIntPtr param);
    [PreserveSig] int ProcessInput(uint id, IMFSample sample, uint flags);
    [PreserveSig] int ProcessOutput(uint flags, uint count, MFT_OUTPUT_DATA_BUFFER* buffers, out uint status);
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(in Guid api);
    [PreserveSig] int IsModifiable(in Guid api);
    [PreserveSig] int GetParameterRange(in Guid api, IntPtr min, IntPtr max, IntPtr delta);
    [PreserveSig] int GetParameterValues(in Guid api, out IntPtr values, out uint count);
    [PreserveSig] int GetDefaultValue(in Guid api, IntPtr value);
    [PreserveSig] int GetValue(in Guid api, ref Variant value);
    [PreserveSig] int SetValue(in Guid api, ref Variant value);
    [PreserveSig] int RegisterForEvent(in Guid api, IntPtr userData);
    [PreserveSig] int UnregisterForEvent(in Guid api);
    [PreserveSig] int SetAllDefaults();
    [PreserveSig] int SetValueWithNotify(in Guid api, IntPtr value, out IntPtr changed, out uint changedCount);
    [PreserveSig] int SetAllDefaultsWithNotify(out IntPtr changed, out uint changedCount);
    [PreserveSig] int GetAllSettings(IntPtr stream);
    [PreserveSig] int SetAllSettings(IntPtr stream);
    [PreserveSig] int SetAllSettingsWithNotify(IntPtr stream, out IntPtr changed, out uint changedCount);
}
