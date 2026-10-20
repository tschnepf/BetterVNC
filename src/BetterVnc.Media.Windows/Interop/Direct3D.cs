using System.Runtime.InteropServices;

namespace BetterVnc.Media.Interop;

// Hand-written COM interop for the slice of Direct3D 11 / DXGI that Desktop Duplication needs. Only the vtable
// slots up to the last method we call are declared; earlier slots we never call are placeholders (the order
// follows d3d11.h / dxgi.h / dxgi1_2.h).

internal static unsafe class D3D
{
    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const uint D3D11_SDK_VERSION = 7;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const int DXGI_FORMAT_B8G8R8A8_UNORM = 87, DXGI_FORMAT_B8G8R8X8_UNORM = 88;
    public const int D3D11_USAGE_STAGING = 3;
    public const uint D3D11_CPU_ACCESS_READ = 0x20000;
    public const uint D3D11_MAP_READ = 1;
    public const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
    public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    public const int DXGI_ERROR_MORE_DATA = unchecked((int)0x887A0003);
    public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    public const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);
    public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out ID3D11Device device, out int featureLevel, out ID3D11DeviceContext context);
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_TEXTURE2D_DESC
{
    public uint Width, Height, MipLevels, ArraySize;
    public int Format;
    public uint SampleCount, SampleQuality;
    public int Usage;
    public uint BindFlags, CpuAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_MAPPED_SUBRESOURCE
{
    public IntPtr Data;
    public uint RowPitch, DepthPitch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_BOX { public uint Left, Top, Front, Right, Bottom, Back; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DXGI_OUTPUT_DESC
{
    public fixed char DeviceName[32];
    public RECT DesktopCoordinates;
    public int AttachedToDesktop;
    public int Rotation;
    public IntPtr Monitor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_DESC
{
    public uint Width, Height;
    public uint RefreshNumerator, RefreshDenominator;
    public int Format, ScanlineOrdering, Scaling;
    public int Rotation;
    public int DesktopImageInSystemMemory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_FRAME_INFO
{
    public long LastPresentTime;
    public long LastMouseUpdateTime;
    public uint AccumulatedFrames;
    public int RectsCoalesced;
    public int ProtectedContentMaskedOut;
    public POINT PointerPosition;
    public int PointerVisible;
    public uint TotalMetadataBufferSize;
    public uint PointerShapeBufferSize;
}

/// <summary>A region the compositor moved (scrolling, a dragged window): drawn at the destination from the source point.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_MOVE_RECT
{
    public POINT SourcePoint;
    public RECT DestinationRect;
}

[ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11Device
{
    void _CreateBuffer();
    void _CreateTexture1D();
    [PreserveSig] int CreateTexture2D(in D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out ID3D11Texture2D texture);
}

[ComImport, Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface ID3D11DeviceContext
{
    // ID3D11DeviceChild
    void _GetDevice(); void _GetPrivateData(); void _SetPrivateData(); void _SetPrivateDataInterface();
    // ID3D11DeviceContext
    void _VSSetConstantBuffers(); void _PSSetShaderResources(); void _PSSetShader(); void _PSSetSamplers(); void _VSSetShader();
    void _DrawIndexed(); void _Draw();
    [PreserveSig] int Map(ID3D11Resource resource, uint subresource, uint mapType, uint mapFlags, out D3D11_MAPPED_SUBRESOURCE mapped);
    [PreserveSig] void Unmap(ID3D11Resource resource, uint subresource);
    void _PSSetConstantBuffers(); void _IASetInputLayout(); void _IASetVertexBuffers(); void _IASetIndexBuffer();
    void _DrawIndexedInstanced(); void _DrawInstanced(); void _GSSetConstantBuffers(); void _GSSetShader();
    void _IASetPrimitiveTopology(); void _VSSetShaderResources(); void _VSSetSamplers(); void _Begin(); void _End();
    void _GetData(); void _SetPredication(); void _GSSetShaderResources(); void _GSSetSamplers(); void _OMSetRenderTargets();
    void _OMSetRenderTargetsAndUnorderedAccessViews(); void _OMSetBlendState(); void _OMSetDepthStencilState();
    void _SOSetTargets(); void _DrawAuto(); void _DrawIndexedInstancedIndirect(); void _DrawInstancedIndirect();
    void _Dispatch(); void _DispatchIndirect(); void _RSSetState(); void _RSSetViewports(); void _RSSetScissorRects();
    [PreserveSig] void CopySubresourceRegion(ID3D11Resource destination, uint destinationSubresource, uint x, uint y, uint z, ID3D11Resource source, uint sourceSubresource, D3D11_BOX* box);
    [PreserveSig] void CopyResource(ID3D11Resource destination, ID3D11Resource source);
}

/// <summary>Marker: textures are passed to the context as this interface (a QueryInterface on the RCW).</summary>
[ComImport, Guid("dc8e63f3-d12b-4952-b47b-5e45026a862d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11Resource
{
}

[ComImport, Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11Texture2D
{
    // ID3D11DeviceChild
    void _GetDevice(); void _GetPrivateData(); void _SetPrivateData(); void _SetPrivateDataInterface();
    // ID3D11Resource
    void _GetType(); void _SetEvictionPriority(); void _GetEvictionPriority();
    [PreserveSig] void GetDesc(out D3D11_TEXTURE2D_DESC desc);
}

[ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIDevice
{
    void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
    [PreserveSig] int GetAdapter(out IDXGIAdapter adapter);
}

[ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIAdapter
{
    void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
    [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput? output);
}

[ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutput
{
    void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
    [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
}

[ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutput1
{
    // IDXGIObject
    void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
    // IDXGIOutput
    void _GetDesc(); void _GetDisplayModeList(); void _FindClosestMatchingMode(); void _WaitForVBlank(); void _TakeOwnership();
    void _ReleaseOwnership(); void _GetGammaControlCapabilities(); void _SetGammaControl(); void _GetGammaControl();
    void _SetDisplaySurface(); void _GetDisplaySurfaceData(); void _GetFrameStatistics();
    // IDXGIOutput1
    void _GetDisplayModeList1(); void _FindClosestMatchingMode1(); void _GetDisplaySurfaceData1();
    [PreserveSig] int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);
}

[ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutputDuplication
{
    void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
    [PreserveSig] void GetDesc(out DXGI_OUTDUPL_DESC desc);
    [PreserveSig] int AcquireNextFrame(uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, out IDXGIResource? resource);
    [PreserveSig] int GetFrameDirtyRects(uint bufferSize, IntPtr rects, out uint required);
    [PreserveSig] int GetFrameMoveRects(uint bufferSize, IntPtr rects, out uint required);
    void _GetFramePointerShape(); void _MapDesktopSurface(); void _UnMapDesktopSurface();
    [PreserveSig] int ReleaseFrame();
}

/// <summary>Marker: the acquired frame; cast to <see cref="ID3D11Texture2D"/> / <see cref="ID3D11Resource"/> to use it.</summary>
[ComImport, Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIResource
{
}
