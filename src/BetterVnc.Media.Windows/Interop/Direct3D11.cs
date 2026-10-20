using System.Runtime.InteropServices;

namespace BetterVnc.Media.Interop;

// Raw-pointer interop for the Direct3D 11 / DXGI objects the video renderer and the hardware decoder share. These are
// called from the decode thread and the UI thread alike; a [ComImport] RCW is bound to the apartment that created it
// and would try (and fail) to marshal a D3D interface across apartments, so these go through vtable slots directly.
// Slot numbers count IUnknown's three methods and follow d3d11.h / dxgi.h / dxgi1_2.h / d3dcommon.h.

internal static unsafe class Com
{
    public static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    public static IntPtr QueryInterface(IntPtr obj, in Guid iid)
    {
        return Marshal.QueryInterface(obj, in iid, out var p) >= 0 ? p : IntPtr.Zero;
    }

    public static void Release(ref IntPtr obj)
    {
        if (obj != IntPtr.Zero) Marshal.Release(obj);
        obj = IntPtr.Zero;
    }
}

internal static unsafe class D3D11
{
    public const uint CREATE_DEVICE_BGRA_SUPPORT = 0x20, CREATE_DEVICE_VIDEO_SUPPORT = 0x800;
    public const int FEATURE_LEVEL_10_0 = 0xa000, FEATURE_LEVEL_10_1 = 0xa100, FEATURE_LEVEL_11_0 = 0xb000, FEATURE_LEVEL_11_1 = 0xb100;
    public const int FORMAT_R8G8_UNORM = 49, FORMAT_R8_UNORM = 61, FORMAT_B8G8R8A8_UNORM = 87, FORMAT_NV12 = 103;
    public const int USAGE_DEFAULT = 0, USAGE_STAGING = 3;
    public const uint BIND_CONSTANT_BUFFER = 0x4, BIND_SHADER_RESOURCE = 0x8, BIND_RENDER_TARGET = 0x20;
    public const uint CPU_ACCESS_READ = 0x20000, MAP_READ = 1;
    public const uint FORMAT_SUPPORT_TEXTURE2D = 0x20, FORMAT_SUPPORT_SHADER_SAMPLE = 0x200;
    public const int SRV_DIMENSION_TEXTURE2D = 4;
    public const int FILTER_MIN_MAG_LINEAR_MIP_POINT = 0x14, TEXTURE_ADDRESS_CLAMP = 3, COMPARISON_NEVER = 1;
    public const int PRIMITIVE_TOPOLOGY_TRIANGLESTRIP = 5;

    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int* featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);

    // ID3D11Device
    public static int CreateBuffer(IntPtr dev, D3D11_BUFFER_DESC* desc, out IntPtr buffer)
    {
        IntPtr r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_BUFFER_DESC*, void*, IntPtr*, int>)Com.Slot(dev, 3))(dev, desc, null, &r); buffer = r; return hr;
    }
    public static int CreateTexture2D(IntPtr dev, D3D11_TEXTURE2D_DESC* desc, out IntPtr texture)
    {
        IntPtr r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void*, IntPtr*, int>)Com.Slot(dev, 5))(dev, desc, null, &r); texture = r; return hr;
    }
    public static int CreateShaderResourceView(IntPtr dev, IntPtr resource, D3D11_SHADER_RESOURCE_VIEW_DESC* desc, out IntPtr view)
    {
        IntPtr r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, D3D11_SHADER_RESOURCE_VIEW_DESC*, IntPtr*, int>)Com.Slot(dev, 7))(dev, resource, desc, &r); view = r; return hr;
    }
    public static int CreateRenderTargetView(IntPtr dev, IntPtr resource, out IntPtr view)
    {
        IntPtr r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, IntPtr*, int>)Com.Slot(dev, 9))(dev, resource, null, &r); view = r; return hr;
    }
    public static int CreateVertexShader(IntPtr dev, byte* code, nuint length, out IntPtr shader)
    {
        IntPtr r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Com.Slot(dev, 12))(dev, code, length, IntPtr.Zero, &r); shader = r; return hr;
    }
    public static int CreatePixelShader(IntPtr dev, byte* code, nuint length, out IntPtr shader)
    {
        IntPtr r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Com.Slot(dev, 15))(dev, code, length, IntPtr.Zero, &r); shader = r; return hr;
    }
    public static int CreateSamplerState(IntPtr dev, D3D11_SAMPLER_DESC* desc, out IntPtr sampler)
    {
        IntPtr r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_SAMPLER_DESC*, IntPtr*, int>)Com.Slot(dev, 23))(dev, desc, &r); sampler = r; return hr;
    }
    public static uint CheckFormatSupport(IntPtr dev, int format)
    {
        uint support; return ((delegate* unmanaged[Stdcall]<IntPtr, int, uint*, int>)Com.Slot(dev, 29))(dev, format, &support) >= 0 ? support : 0;
    }
    public static int GetDeviceRemovedReason(IntPtr dev) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(dev, 39))(dev);

    // ID3D11DeviceContext
    public static void VSSetConstantBuffers(IntPtr ctx, uint start, uint count, IntPtr* buffers) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Com.Slot(ctx, 7))(ctx, start, count, buffers);
    public static void PSSetShaderResources(IntPtr ctx, uint start, uint count, IntPtr* views) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Com.Slot(ctx, 8))(ctx, start, count, views);
    public static void PSSetShader(IntPtr ctx, IntPtr shader) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)Com.Slot(ctx, 9))(ctx, shader, null, 0);
    public static void PSSetSamplers(IntPtr ctx, uint start, uint count, IntPtr* samplers) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Com.Slot(ctx, 10))(ctx, start, count, samplers);
    public static void VSSetShader(IntPtr ctx, IntPtr shader) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, uint, void>)Com.Slot(ctx, 11))(ctx, shader, null, 0);
    public static void Draw(IntPtr ctx, uint vertexCount, uint startVertex) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)Com.Slot(ctx, 13))(ctx, vertexCount, startVertex);
    public static int Map(IntPtr ctx, IntPtr resource, uint subresource, uint mapType, out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        D3D11_MAPPED_SUBRESOURCE m; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Com.Slot(ctx, 14))(ctx, resource, subresource, mapType, 0, &m); mapped = m; return hr;
    }
    public static void Unmap(IntPtr ctx, IntPtr resource, uint subresource) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Com.Slot(ctx, 15))(ctx, resource, subresource);
    public static void PSSetConstantBuffers(IntPtr ctx, uint start, uint count, IntPtr* buffers) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)Com.Slot(ctx, 16))(ctx, start, count, buffers);
    public static void IASetInputLayout(IntPtr ctx, IntPtr layout) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Com.Slot(ctx, 17))(ctx, layout);
    public static void IASetPrimitiveTopology(IntPtr ctx, int topology) => ((delegate* unmanaged[Stdcall]<IntPtr, int, void>)Com.Slot(ctx, 24))(ctx, topology);
    public static void OMSetRenderTargets(IntPtr ctx, uint count, IntPtr* views) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)Com.Slot(ctx, 33))(ctx, count, views, IntPtr.Zero);
    public static void RSSetViewports(IntPtr ctx, uint count, D3D11_VIEWPORT* viewports) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, D3D11_VIEWPORT*, void>)Com.Slot(ctx, 44))(ctx, count, viewports);
    public static void CopySubresourceRegion(IntPtr ctx, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, D3D11_BOX* box) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)Com.Slot(ctx, 46))(ctx, dst, dstSub, x, y, z, src, srcSub, box);
    public static void CopyResource(IntPtr ctx, IntPtr dst, IntPtr src) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Com.Slot(ctx, 47))(ctx, dst, src);
    public static void UpdateSubresource(IntPtr ctx, IntPtr dst, uint dstSub, void* data, uint rowPitch, uint depthPitch) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, D3D11_BOX*, void*, uint, uint, void>)Com.Slot(ctx, 48))(ctx, dst, dstSub, null, data, rowPitch, depthPitch);
    public static void ClearRenderTargetView(IntPtr ctx, IntPtr view, float* rgba) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, void>)Com.Slot(ctx, 50))(ctx, view, rgba);
    public static void ClearState(IntPtr ctx) => ((delegate* unmanaged[Stdcall]<IntPtr, void>)Com.Slot(ctx, 110))(ctx);
    public static void Flush(IntPtr ctx) => ((delegate* unmanaged[Stdcall]<IntPtr, void>)Com.Slot(ctx, 111))(ctx);

    // ID3D11Texture2D
    public static void GetDesc(IntPtr texture, out D3D11_TEXTURE2D_DESC desc)
    {
        D3D11_TEXTURE2D_DESC d; ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Com.Slot(texture, 10))(texture, &d); desc = d;
    }

    // ID3D10Multithread
    public static void SetMultithreadProtected(IntPtr mt, bool on) => ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Com.Slot(mt, 5))(mt, on ? 1 : 0);
}

internal static unsafe class Dxgi
{
    public const int USAGE_RENDER_TARGET_OUTPUT = 0x20;
    public const int SCALING_STRETCH = 0, SWAP_EFFECT_FLIP_SEQUENTIAL = 3, SWAP_EFFECT_FLIP_DISCARD = 4, ALPHA_MODE_IGNORE = 3;
    public const uint MWA_NO_WINDOW_CHANGES = 1, MWA_NO_ALT_ENTER = 2;
    public const uint VENDOR_MICROSOFT = 0x1414, DEVICE_BASIC_RENDER = 0x8c;

    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_IDXGIDevice1 = new("77db970f-6276-48ba-ba28-070143b4392c");
    public static readonly Guid IID_IDXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");

    // IDXGIObject
    public static int GetParent(IntPtr obj, in Guid iid, out IntPtr parent)
    {
        IntPtr r; int hr; fixed (Guid* g = &iid) hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Com.Slot(obj, 6))(obj, g, &r); parent = r; return hr;
    }
    // IDXGIDevice / IDXGIDevice1
    public static int GetAdapter(IntPtr device, out IntPtr adapter)
    {
        IntPtr r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Slot(device, 7))(device, &r); adapter = r; return hr;
    }
    public static int SetGPUThreadPriority(IntPtr device, int priority) => ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Com.Slot(device, 10))(device, priority);
    public static int SetMaximumFrameLatency(IntPtr device1, uint frames) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Com.Slot(device1, 12))(device1, frames);
    // IDXGIAdapter
    public static int GetDesc(IntPtr adapter, out DXGI_ADAPTER_DESC desc)
    {
        DXGI_ADAPTER_DESC d; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC*, int>)Com.Slot(adapter, 8))(adapter, &d); desc = d; return hr;
    }
    // IDXGIFactory / IDXGIFactory2
    public static int MakeWindowAssociation(IntPtr factory, IntPtr hwnd, uint flags) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Com.Slot(factory, 8))(factory, hwnd, flags);
    public static int CreateSwapChainForHwnd(IntPtr factory2, IntPtr device, IntPtr hwnd, DXGI_SWAP_CHAIN_DESC1* desc, out IntPtr swapChain)
    {
        IntPtr r; int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, DXGI_SWAP_CHAIN_DESC1*, void*, IntPtr, IntPtr*, int>)Com.Slot(factory2, 15))(factory2, device, hwnd, desc, null, IntPtr.Zero, &r);
        swapChain = r; return hr;
    }
    // IDXGISwapChain
    public static int Present(IntPtr swapChain, uint syncInterval, uint flags) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)Com.Slot(swapChain, 8))(swapChain, syncInterval, flags);
    public static int GetBuffer(IntPtr swapChain, uint index, in Guid iid, out IntPtr buffer)
    {
        IntPtr r; int hr; fixed (Guid* g = &iid) hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)Com.Slot(swapChain, 9))(swapChain, index, g, &r); buffer = r; return hr;
    }
    public static int ResizeBuffers(IntPtr swapChain, uint count, uint width, uint height, int format, uint flags) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, int, uint, int>)Com.Slot(swapChain, 13))(swapChain, count, width, height, format, flags);
}

internal static unsafe class D3DCompiler
{
    public const uint OPTIMIZATION_LEVEL3 = 1 << 15;

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile")]
    private static extern int D3DCompileNative(byte* source, nuint length, byte* sourceName, IntPtr defines, IntPtr include, byte* entryPoint, byte* target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    /// <summary>Compiles one HLSL entry point to bytecode; throws with the compiler's message on an error.</summary>
    public static byte[] Compile(string source, string entryPoint, string target)
    {
        var src = System.Text.Encoding.ASCII.GetBytes(source);
        var entry = System.Text.Encoding.ASCII.GetBytes(entryPoint + "\0");
        var prof = System.Text.Encoding.ASCII.GetBytes(target + "\0");
        IntPtr code, errors;
        int hr;
        fixed (byte* s = src)
        fixed (byte* e = entry)
        fixed (byte* p = prof)
            hr = D3DCompileNative(s, (nuint)src.Length, null, IntPtr.Zero, IntPtr.Zero, e, p, OPTIMIZATION_LEVEL3, 0, out code, out errors);
        try
        {
            if (hr < 0)
            {
                string message = errors != IntPtr.Zero ? Marshal.PtrToStringAnsi(BlobPointer(errors), (int)BlobSize(errors)) : "";
                throw new MediaException($"compile shader {entryPoint} ({message.Trim()})", hr);
            }
            var bytes = new byte[(int)BlobSize(code)];
            Marshal.Copy(BlobPointer(code), bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Com.Release(ref code);
            Com.Release(ref errors);
        }
    }

    // ID3DBlob
    private static IntPtr BlobPointer(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Com.Slot(blob, 3))(blob);
    private static nuint BlobSize(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)Com.Slot(blob, 4))(blob);
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_BUFFER_DESC
{
    public uint ByteWidth;
    public int Usage;
    public uint BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride;
}

/// <summary>D3D11_SHADER_RESOURCE_VIEW_DESC for a Texture2D view (the union is 16 bytes; only its first two fields are used).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_SHADER_RESOURCE_VIEW_DESC
{
    public int Format;
    public int ViewDimension;
    public uint MostDetailedMip, MipLevels, Reserved0, Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct D3D11_SAMPLER_DESC
{
    public int Filter, AddressU, AddressV, AddressW;
    public float MipLodBias;
    public uint MaxAnisotropy;
    public int ComparisonFunc;
    public fixed float BorderColor[4];
    public float MinLod, MaxLod;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_VIEWPORT
{
    public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_SWAP_CHAIN_DESC1
{
    public uint Width, Height;
    public int Format;
    public int Stereo;
    public uint SampleCount, SampleQuality;
    public int BufferUsage;
    public uint BufferCount;
    public int Scaling, SwapEffect, AlphaMode;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DXGI_ADAPTER_DESC
{
    public fixed char Description[128];
    public uint VendorId, DeviceId, SubSysId, Revision;
    public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public long AdapterLuid;
}
