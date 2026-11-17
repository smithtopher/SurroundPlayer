// D3D11 video presenter on a SwapChainPanel. The engine hands over frames already timed
// to the audio clock (on its presenter thread), so each one is drawn and presented at
// once, like the macOS app's "display immediately" layer.
//   - hardware frames: NV12 / P010 slices of the decoder's texture array, converted to RGB
//     in a pixel shader (BT.601 / 709 / 2020, limited or full range)
//   - software frames: BGRA in CPU memory, uploaded to a dynamic texture
// The decoder shares our ID3D11Device (pc_set_d3d11_device), so there are no copies.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SurroundPlayer;

public sealed unsafe class VideoRenderer : IDisposable
{
    private const string Hlsl = @"
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
cbuffer C : register(b0) { float4 uvScale; float4 m0; float4 m1; float4 m2; };
VSOut vs(uint id : SV_VertexID) {
    VSOut o;
    float2 t = float2(id & 1, id >> 1);
    o.pos = float4(t.x * 2 - 1, 1 - t.y * 2, 0, 1);
    o.uv = t * uvScale.xy;
    return o;
}
Texture2DArray<float> texY : register(t0);
Texture2DArray<float2> texUV : register(t1);
Texture2D<float4> texRGB : register(t0);
SamplerState samp : register(s0);
float4 ps_yuv(VSOut i) : SV_Target {
    float4 v = float4(texY.Sample(samp, float3(i.uv, 0)), texUV.Sample(samp, float3(i.uv, 0)), 1);
    return float4(saturate(float3(dot(m0, v), dot(m1, v), dot(m2, v))), 1);
}
float4 ps_rgb(VSOut i) : SV_Target { return float4(texRGB.Sample(samp, i.uv).rgb, 1); }
";

    private readonly object _lock = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psYuv, _psRgb;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _cb;
    private IDXGISwapChain1? _swap;
    private ID3D11RenderTargetView? _rtv;
    private int _width = 1, _height = 1;
    private IntPtr _last;            // retained native frame currently on screen
    private bool _enabled = true;
    private ID3D11Texture2D? _bgraTex;
    private ID3D11ShaderResourceView? _bgraSrv;
    private int _bgraW, _bgraH;
    private readonly Dictionary<(IntPtr, int), (ID3D11ShaderResourceView y, ID3D11ShaderResourceView uv)> _srvs = new();

    public IntPtr DevicePointer => _device.NativePointer;
    /// Frames drawn to the swap chain (debug log).
    public long FramesPresented;

    public VideoRenderer()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        // VideoSupport: the decoder (D3D11VA) runs on this device too
        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
                                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                                levels, out _device!, out _ctx!).CheckError();
        var vsCode = Compiler.Compile(Hlsl, "vs", "video.hlsl", "vs_5_0");
        _vs = _device.CreateVertexShader(vsCode.Span);
        _psYuv = _device.CreatePixelShader(Compiler.Compile(Hlsl, "ps_yuv", "video.hlsl", "ps_5_0").Span);
        _psRgb = _device.CreatePixelShader(Compiler.Compile(Hlsl, "ps_rgb", "video.hlsl", "ps_5_0").Span);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _cb = _device.CreateBuffer(new BufferDescription(64, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    /// Creates the swap chain and attaches it to the panel (UI thread).
    public void Attach(SwapChainPanel panel)
    {
        lock (_lock)
        {
            _width = Math.Max(1, (int)(panel.ActualWidth * panel.CompositionScaleX));
            _height = Math.Max(1, (int)(panel.ActualHeight * panel.CompositionScaleY));
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var desc = new SwapChainDescription1
            {
                Width = (uint)_width, Height = (uint)_height,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
            };
            _swap = factory.CreateSwapChainForComposition(_device, desc);
            SetPanelSwapChain(panel, _swap.NativePointer);
            SetScale(panel.CompositionScaleX, panel.CompositionScaleY);
            CreateTarget();
            Draw();
        }
    }

    public void Resize(double width, double height, float scaleX, float scaleY)
    {
        lock (_lock)
        {
            if (_swap == null) return;
            int w = Math.Max(1, (int)(width * scaleX)), h = Math.Max(1, (int)(height * scaleY));
            if (w == _width && h == _height) { SetScale(scaleX, scaleY); return; }
            _width = w;
            _height = h;
            _rtv?.Dispose();
            _rtv = null;
            _swap.ResizeBuffers(2, (uint)w, (uint)h, Format.Unknown, SwapChainFlags.None).CheckError();
            SetScale(scaleX, scaleY);
            CreateTarget();
            Draw();
        }
    }

    /// The visualiser hides the video: frames keep coming but aren't drawn.
    public bool Enabled
    {
        get => _enabled;
        set { lock (_lock) { _enabled = value; if (value) ReleaseLast(); Draw(); } }
    }

    /// Engine presenter thread: show this frame now.
    public void Present(IntPtr frame)
    {
        lock (_lock)
        {
            if (!_enabled || _swap == null) return;
            Native.pc_frame_retain(frame);
            ReleaseLast();
            _last = frame;
            Draw();
        }
    }

    /// Black screen (track change / stop); drops cached views of the old decoder's textures.
    public void Clear()
    {
        lock (_lock)
        {
            ReleaseLast();
            ClearViews();
            Draw();
        }
    }

    private void ClearViews()
    {
        foreach (var v in _srvs.Values) { v.y.Dispose(); v.uv.Dispose(); }
        _srvs.Clear();
    }

    private void ReleaseLast()
    {
        if (_last != IntPtr.Zero) Native.pc_frame_release(_last);
        _last = IntPtr.Zero;
    }

    private void SetScale(float sx, float sy)
    {
        // the buffers are in physical pixels; undo the panel's DPI scaling
        using var s2 = _swap!.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (s2 != null) s2.MatrixTransform = new Matrix3x2(1 / sx, 0, 0, 1 / sy, 0, 0);
    }

    private void CreateTarget()
    {
        using var back = _swap!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(back);
    }

    private void Draw()
    {
        if (_swap == null || _rtv == null) return;
        _ctx.OMSetRenderTargets(_rtv);
        _ctx.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 1));
        if (_last != IntPtr.Zero && _enabled && Native.pc_frame_info(_last, out var fi) == 0) DrawFrame(fi);
        _swap.Present(0, PresentFlags.None);
    }

    private void DrawFrame(in Native.PCFrameInfo fi)
    {
        if (fi.width <= 0 || fi.height <= 0) return;
        // letterbox to the display aspect ratio
        double dar = (double)fi.width * fi.sar_num / ((double)fi.height * fi.sar_den);
        double vw = _width, vh = _width / dar;
        if (vh > _height) { vh = _height; vw = _height * dar; }
        _ctx.RSSetViewport(new Viewport((float)((_width - vw) / 2), (float)((_height - vh) / 2), (float)vw, (float)vh));
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetSampler(0, _sampler);

        if (fi.kind == Native.PC_FRAME_D3D11)
        {
            if (fi.shader_resource == 0 || !GetYuvViews(fi, out var y, out var uv, out int texW, out int texH)) return;
            WriteConstants((float)fi.width / texW, (float)fi.height / texH, fi);
            _ctx.PSSetShader(_psYuv);
            _ctx.PSSetShaderResources(0, new[] { y, uv });
        }
        else if (fi.kind == Native.PC_FRAME_BGRA)
        {
            if (!UploadBgra(fi)) return;
            WriteConstants(1, 1, fi);
            _ctx.PSSetShader(_psRgb);
            _ctx.PSSetShaderResource(0, _bgraSrv!);
        }
        else return;
        _ctx.VSSetConstantBuffer(0, _cb);
        _ctx.PSSetConstantBuffer(0, _cb);
        _ctx.Draw(4, 0);
        FramesPresented++;
        _ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
    }

    private bool GetYuvViews(in Native.PCFrameInfo fi, out ID3D11ShaderResourceView y, out ID3D11ShaderResourceView uv,
                             out int texW, out int texH)
    {
        Marshal.AddRef(fi.texture);
        using var tex = new ID3D11Texture2D(fi.texture);   // our own reference, released by Dispose
        var d = tex.Description;
        texW = (int)d.Width;
        texH = (int)d.Height;
        var key = (fi.texture, fi.array_index);
        if (!_srvs.TryGetValue(key, out var v))
        {
            bool p010 = d.Format == Format.P010;
            if (!p010 && d.Format != Format.NV12) { y = uv = null!; return false; }
            if (_srvs.Count > 96) ClearViews();
            v = (View(tex, p010 ? Format.R16_UNorm : Format.R8_UNorm, fi.array_index),
                 View(tex, p010 ? Format.R16G16_UNorm : Format.R8G8_UNorm, fi.array_index));
            _srvs[key] = v;
        }
        y = v.y;
        uv = v.uv;
        return true;
    }

    private ID3D11ShaderResourceView View(ID3D11Texture2D tex, Format f, int slice) =>
        _device.CreateShaderResourceView(tex, new ShaderResourceViewDescription
        {
            Format = f,
            ViewDimension = ShaderResourceViewDimension.Texture2DArray,
            Texture2DArray = new Texture2DArrayShaderResourceView
            {
                MostDetailedMip = 0, MipLevels = 1, FirstArraySlice = (uint)slice, ArraySize = 1,
            },
        });

    private bool UploadBgra(in Native.PCFrameInfo fi)
    {
        if (_bgraTex == null || _bgraW != fi.width || _bgraH != fi.height)
        {
            _bgraSrv?.Dispose();
            _bgraTex?.Dispose();
            _bgraTex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)fi.width, Height = (uint)fi.height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic, BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write,
            });
            _bgraSrv = _device.CreateShaderResourceView(_bgraTex);
            _bgraW = fi.width;
            _bgraH = fi.height;
        }
        var m = _ctx.Map(_bgraTex, 0, MapMode.WriteDiscard);
        byte* src = (byte*)fi.bgra, dst = (byte*)m.DataPointer;
        int row = fi.width * 4;
        for (int r = 0; r < fi.height; r++)
            Buffer.MemoryCopy(src + (long)r * fi.stride, dst + (long)r * m.RowPitch, m.RowPitch, row);
        _ctx.Unmap(_bgraTex, 0);
        return true;
    }

    // uvScale + the YUV -> RGB matrix (rows act on (Y, Cb, Cr, 1))
    private void WriteConstants(float su, float sv, in Native.PCFrameInfo fi)
    {
        // AVColorSpace: 1 BT709, 5 BT470BG, 6 SMPTE170M, 9/10 BT2020; 2 unspecified
        int cs = fi.color_space;
        if (cs == 2 || cs == 0) cs = fi.height >= 720 ? 1 : 6;
        (float kr, float gb, float gr, float kb) = cs switch
        {
            5 or 6 => (1.402f, 0.344136f, 0.714136f, 1.772f),
            9 or 10 => (1.4746f, 0.16455f, 0.57135f, 1.8814f),
            _ => (1.5748f, 0.1873f, 0.4681f, 1.8556f),
        };
        bool full = fi.color_range == 2;   // AVCOL_RANGE_JPEG
        float ys = full ? 1 : 255f / 219, yo = full ? 0 : -16f / 255 * ys;
        float cScale = full ? 1 : 255f / 224, co = -128f / 255 * cScale;
        var data = stackalloc float[16]
        {
            su, sv, 0, 0,
            ys, 0, kr * cScale, yo + kr * co,
            ys, -gb * cScale, -gr * cScale, yo - (gb + gr) * co,
            ys, kb * cScale, 0, yo + kb * co,
        };
        var m = _ctx.Map(_cb, 0, MapMode.WriteDiscard);
        Buffer.MemoryCopy(data, (void*)m.DataPointer, 64, 64);
        _ctx.Unmap(_cb, 0);
    }

    // ISwapChainPanelNative::SetSwapChain (microsoft.ui.xaml.media.dxinterop.h)
    private static readonly Guid IID_ISwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    private static void SetPanelSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        IntPtr unk = WinRT.MarshalInspectable<object>.FromManaged(panel);
        try
        {
            Marshal.QueryInterface(unk, in IID_ISwapChainPanelNative, out IntPtr native).CheckHr();
            try
            {
                var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*(void***)native)[3];
                setSwapChain(native, swapChain).CheckHr();
            }
            finally { Marshal.Release(native); }
        }
        finally { Marshal.Release(unk); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            ReleaseLast();
            foreach (var v in _srvs.Values) { v.y.Dispose(); v.uv.Dispose(); }
            _srvs.Clear();
            _bgraSrv?.Dispose();
            _bgraTex?.Dispose();
            _rtv?.Dispose();
            _swap?.Dispose();
            _cb.Dispose();
            _sampler.Dispose();
            _psRgb.Dispose();
            _psYuv.Dispose();
            _vs.Dispose();
            _ctx.Dispose();
            _device.Dispose();
        }
    }
}

internal static class HrExtensions
{
    public static void CheckHr(this int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
}
