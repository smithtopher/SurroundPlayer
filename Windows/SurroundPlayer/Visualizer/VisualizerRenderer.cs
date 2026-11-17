// D3D11 port of Sources/SurroundPlayer/Visualizer/VisualizerRenderer.swift (Metal).
// MilkDrop-style renderer with its own device (it never touches the video decoder's),
// presented on a SwapChainPanel. Owned by the window so the feedback image and preset
// survive the stage being hidden and shown.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SurroundPlayer.Visualizer;

public sealed unsafe partial class VisualizerRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GVert { public Vector4 Pos, Color; public GVert(Vector4 p, Vector4 c) { Pos = p; Color = c; } }

    private sealed class Target : IDisposable
    {
        public ID3D11Texture2D Tex = null!;
        public ID3D11RenderTargetView Rtv = null!;
        public ID3D11ShaderResourceView Srv = null!;
        public int W, H;
        public void Dispose() { Srv.Dispose(); Rtv.Dispose(); Tex.Dispose(); }
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VertexShader _fsVs, _geomVs;
    private readonly ID3D11PixelShader _warpPs, _geomPs, _copyPs, _blurPs, _compPs;
    private readonly ID3D11InputLayout _geomLayout;
    private readonly ID3D11BlendState _blendAdd, _blendAlpha;
    private readonly ID3D11RasterizerState _noCull;
    private readonly ID3D11SamplerState[] _samplers;
    private readonly ID3D11Buffer _warpCb, _compCb, _blurCb;
    private ID3D11Buffer? _vb;
    private int _vbCapacity;
    private Target[] _feedback = Array.Empty<Target>();   // ping-pong
    private Target? _blurA, _blurB;
    private int _current;
    private readonly AudioAnalyzer _analyzer = new();
    private readonly List<GVert> _geometry = new();

    private IDXGISwapChain1? _swap;
    private ID3D11RenderTargetView? _swapRtv;
    private int _swapW = 1, _swapH = 1;

    // presets
    private int[] _order = Array.Empty<int>();
    private int _orderPos, _presetIndex;
    private int? _previousIndex;
    private double _blendStart, _presetStart;
    private const double BlendDuration = 2.5;
    private int _presetBeats;
    private readonly Random _rng = new();
    public bool Locked { get; set; }
    public event Action<string>? PresetChanged;

    /// Fetches the audio leaving the speakers (interleaved L/R), once per frame.
    public AudioAnalyzer.Fetch FetchSamples { get; set; } = (_, _) => 0;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrame;
    private float _fadeIn;
    private double? _manualTime;   // fixed-step clock for snapshots; null = real time
    private double Now => _manualTime ?? _clock.Elapsed.TotalSeconds;

    public string PresetName => VisPreset.All[_presetIndex].Name;
    public long FramesRendered { get; private set; }
    public string DebugStatus => $"vis frames={FramesRendered} signal={(_analyzer.HasSignal ? "yes" : "no")} bass={_analyzer.Bass:F2} preset={PresetName}";

    public VisualizerRenderer()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _device!, out _ctx!).CheckError();
        string src = VisualizerShaders.Source;
        ReadOnlyMemory<byte> Cs(string entry, string profile) => Compiler.Compile(src, entry, "visualizer.hlsl", profile);
        _fsVs = _device.CreateVertexShader(Cs("fs_vertex", "vs_5_0").Span);
        var geomVsCode = Cs("geom_vs", "vs_5_0");
        _geomVs = _device.CreateVertexShader(geomVsCode.Span);
        _warpPs = _device.CreatePixelShader(Cs("warp_fs", "ps_5_0").Span);
        _geomPs = _device.CreatePixelShader(Cs("geom_fs", "ps_5_0").Span);
        _copyPs = _device.CreatePixelShader(Cs("copy_fs", "ps_5_0").Span);
        _blurPs = _device.CreatePixelShader(Cs("blur_fs", "ps_5_0").Span);
        _compPs = _device.CreatePixelShader(Cs("comp_fs", "ps_5_0").Span);
        _geomLayout = _device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32A32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 16, 0),
        }, geomVsCode.Span);
        _blendAdd = _device.CreateBlendState(Blend(Vortice.Direct3D11.Blend.One));
        _blendAlpha = _device.CreateBlendState(Blend(Vortice.Direct3D11.Blend.InverseSourceAlpha));
        _noCull = _device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        _samplers = new[]
        {
            _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Wrap)),
            _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp)),
            _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Mirror)),
        };
        _warpCb = Cb(80);
        _compCb = Cb(80);
        _blurCb = Cb(16);
        Reshuffle();
        _presetIndex = _order[0];
    }

    private static BlendDescription Blend(Vortice.Direct3D11.Blend dest)
    {
        var d = new BlendDescription();
        d.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Vortice.Direct3D11.Blend.SourceAlpha, DestinationBlend = dest, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Vortice.Direct3D11.Blend.Zero, DestinationBlendAlpha = Vortice.Direct3D11.Blend.One,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        return d;
    }

    private ID3D11Buffer Cb(uint size) =>
        _device.CreateBuffer(new BufferDescription(size, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

    // ------------------------------------------------------------------ preset control

    private void Reshuffle()
    {
        _order = new int[VisPreset.All.Length];
        for (int i = 0; i < _order.Length; i++) _order[i] = i;
        _rng.Shuffle(_order);
        _orderPos = 0;
    }

    public void NextPreset() => SwitchTo(NextInOrder(), Now);

    public void PreviousPreset()
    {
        _orderPos = (_orderPos - 2 + _order.Length) % _order.Length;
        SwitchTo(NextInOrder(), Now);
    }

    public void SelectPreset(int index) => SwitchTo(index, Now);

    private int NextInOrder()
    {
        _orderPos++;
        if (_orderPos >= _order.Length)
        {
            int last = _presetIndex;
            Reshuffle();
            if (_order[0] == last) (_order[0], _order[^1]) = (_order[^1], _order[0]);
        }
        return _order[_orderPos % _order.Length];
    }

    private void SwitchTo(int index, double now)
    {
        if (index == _presetIndex) return;
        _previousIndex = _presetIndex;
        _presetIndex = index;
        _blendStart = now;
        _presetStart = now;
        _presetBeats = 0;
        PresetChanged?.Invoke(VisPreset.All[index].Name);
    }

    // ------------------------------------------------------------------ panel

    public void Attach(SwapChainPanel panel)
    {
        _swapW = Math.Max(1, (int)(panel.ActualWidth * panel.CompositionScaleX));
        _swapH = Math.Max(1, (int)(panel.ActualHeight * panel.CompositionScaleY));
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _swap = factory.CreateSwapChainForComposition(_device, new SwapChainDescription1
        {
            Width = (uint)_swapW, Height = (uint)_swapH, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
            Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Ignore,
        });
        VideoRenderer.SetPanelSwapChain(panel, _swap.NativePointer);
        SetScale(panel.CompositionScaleX, panel.CompositionScaleY);
        using var back = _swap.GetBuffer<ID3D11Texture2D>(0);
        _swapRtv = _device.CreateRenderTargetView(back);
    }

    public void Resize(double width, double height, float scaleX, float scaleY)
    {
        if (_swap == null) return;
        int w = Math.Max(1, (int)(width * scaleX)), h = Math.Max(1, (int)(height * scaleY));
        SetScale(scaleX, scaleY);
        if (w == _swapW && h == _swapH) return;
        _swapW = w;
        _swapH = h;
        _swapRtv?.Dispose();
        _swap.ResizeBuffers(2, (uint)w, (uint)h, Format.Unknown, SwapChainFlags.None).CheckError();
        using var back = _swap.GetBuffer<ID3D11Texture2D>(0);
        _swapRtv = _device.CreateRenderTargetView(back);
    }

    private void SetScale(float sx, float sy)
    {
        using var s2 = _swap!.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (s2 != null) s2.MatrixTransform = new Matrix3x2(1 / sx, 0, 0, 1 / sy, 0, 0);
    }

    /// One frame to the panel (UI thread, CompositionTarget.Rendering).
    public void RenderToPanel()
    {
        if (_swap == null || _swapRtv == null) return;
        Render(_swapRtv, _swapW, _swapH);
        _swap.Present(0, PresentFlags.None);
    }

    // ------------------------------------------------------------------ frame

    private void Render(ID3D11RenderTargetView target, int width, int height)
    {
        double t = Now;
        float dt = _manualTime != null ? 1f / 60 : (float)Math.Min(0.1, t - _lastFrame);
        _lastFrame = t;
        _analyzer.Update(dt, t, FetchSamples);
        _fadeIn = Math.Min(1, _fadeIn + dt * 0.8f);

        // auto-advance: on a strong beat after 18 s, or regardless after 40 s
        if (_analyzer.Beat) _presetBeats++;
        double age = t - _presetStart;
        if (!Locked && _previousIndex == null && ((age > 18 && _analyzer.Beat && _analyzer.Bass > 1.7f) || age > 40))
            SwitchTo(NextInOrder(), t);

        var p = CurrentParams(t);
        EnsureTextures(width, height);
        if (_feedback.Length != 2 || _blurA == null || _blurB == null) return;
        var prev = _feedback[_current];
        var cur = _feedback[1 - _current];
        float w = cur.W, h = cur.H, aspect = w / h;

        // 1+2: warp the previous frame, then draw waves/shapes/borders on top
        float* wu = stackalloc float[20]
        {
            (float)t, p.decay, p.zoom, p.zoomExp,
            p.rot, p.warp, p.warpSpeed, p.warpScale,
            p.cx, p.cy, p.dx, p.dy,
            p.sx, p.sy, p.zoomRad, p.rotRad,
            aspect, p.wrap ? 1 : 0, 0, 0,
        };
        Upload(_warpCb, wu, 80);
        FullscreenPass(_warpPs, cur, new[] { prev.Srv }, _warpCb);

        _ctx.OMSetRenderTargets(cur.Rtv);
        _ctx.RSSetViewport(new Viewport(0, 0, w, h));
        foreach (bool additive in new[] { false, true })
        {
            _geometry.Clear();
            BuildGeometry(p, w, h, (float)t, additive);
            if (_geometry.Count == 0) continue;
            DrawGeometry(additive);
        }

        // 3: half-res blur for glow
        FullscreenPass(_copyPs, _blurA, new[] { cur.Srv }, null);
        float* dir = stackalloc float[4] { 1.5f / _blurA.W, 0, 0, 0 };
        Upload(_blurCb, dir, 16);
        FullscreenPass(_blurPs, _blurB, new[] { _blurA.Srv }, _blurCb);
        dir[0] = 0; dir[1] = 1.5f / _blurA.H;
        Upload(_blurCb, dir, 16);
        FullscreenPass(_blurPs, _blurA, new[] { _blurB.Srv }, _blurCb);

        // 4: composite to the target
        float* cu = stackalloc float[20]
        {
            (float)t, p.gamma, p.echoZoom, p.echoAlpha,
            p.echoOrient, p.hue, p.glow, p.kaleido,
            p.chroma, p.solarize ? 1 : 0, p.invert ? 1 : 0, p.brighten ? 1 : 0,
            p.darken ? 1 : 0, p.darkenCenter, aspect, p.contrast,
            p.vignette, p.saturation, _fadeIn, 0,
        };
        Upload(_compCb, cu, 80);
        FullscreenPass(_compPs, target, width, height, new[] { cur.Srv, _blurA.Srv }, _compCb);
        _current = 1 - _current;
        FramesRendered++;
    }

    private void Upload(ID3D11Buffer cb, float* data, int bytes)
    {
        var m = _ctx.Map(cb, 0, MapMode.WriteDiscard);
        Buffer.MemoryCopy(data, (void*)m.DataPointer, bytes, bytes);
        _ctx.Unmap(cb, 0);
    }

    private void FullscreenPass(ID3D11PixelShader ps, Target target, ID3D11ShaderResourceView[] srvs, ID3D11Buffer? cb) =>
        FullscreenPass(ps, target.Rtv, target.W, target.H, srvs, cb);

    private void FullscreenPass(ID3D11PixelShader ps, ID3D11RenderTargetView rtv, int w, int h,
                                ID3D11ShaderResourceView[] srvs, ID3D11Buffer? cb)
    {
        _ctx.OMSetRenderTargets(rtv);
        _ctx.OMSetBlendState(null);
        _ctx.RSSetState(_noCull);
        _ctx.RSSetViewport(new Viewport(0, 0, w, h));
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.IASetInputLayout(null);
        _ctx.VSSetShader(_fsVs);
        _ctx.PSSetShader(ps);
        _ctx.PSSetSamplers(0, _samplers);
        _ctx.PSSetShaderResources(0, srvs);
        if (cb != null) _ctx.PSSetConstantBuffer(0, cb);
        _ctx.Draw(3, 0);
        _ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
    }

    private void DrawGeometry(bool additive)
    {
        int count = _geometry.Count;
        int bytes = count * sizeof(GVert);
        if (_vb == null || _vbCapacity < bytes)
        {
            _vb?.Dispose();
            _vbCapacity = Math.Max(bytes, 64 * 1024);
            _vb = _device.CreateBuffer(new BufferDescription((uint)_vbCapacity, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        }
        var m = _ctx.Map(_vb, 0, MapMode.WriteDiscard);
        var span = CollectionsMarshal.AsSpan(_geometry);
        fixed (GVert* src = span) Buffer.MemoryCopy(src, (void*)m.DataPointer, _vbCapacity, bytes);
        _ctx.Unmap(_vb, 0);
        _ctx.OMSetBlendState(additive ? _blendAdd : _blendAlpha);
        _ctx.RSSetState(_noCull);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.IASetInputLayout(_geomLayout);
        _ctx.IASetVertexBuffer(0, _vb, (uint)sizeof(GVert));
        _ctx.VSSetShader(_geomVs);
        _ctx.PSSetShader(_geomPs);
        _ctx.Draw((uint)count, 0);
        _ctx.OMSetBlendState(null);
    }

    // ------------------------------------------------------------------ params

    private FrameVars Vars(double time) =>
        new((float)time, (float)(time - _presetStart), _analyzer.Bass, _analyzer.Mid, _analyzer.Treb,
            _analyzer.BassAtt, _analyzer.MidAtt, _analyzer.TrebAtt, _analyzer.Volume, _analyzer.Beat, _presetBeats);

    private VisParams Evaluate(int index, double time)
    {
        var preset = VisPreset.All[index];
        var p = preset.Base.Clone();
        preset.PerFrame(p, Vars(time));
        return p;
    }

    private VisParams CurrentParams(double time)
    {
        var p = Evaluate(_presetIndex, time);
        if (_previousIndex is not int prevIndex) return p;
        double k = (time - _blendStart) / BlendDuration;
        if (k >= 1) { _previousIndex = null; return p; }
        float s = (float)(k * k * (3 - 2 * k));   // smoothstep
        return VisParams.Lerp(Evaluate(prevIndex, time), p, s);
    }

    // ------------------------------------------------------------------ textures

    private Target MakeTarget(int w, int h, Format f = Format.R16G16B16A16_Float)
    {
        var t = new Target { W = w, H = h };
        t.Tex = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = f,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        t.Rtv = _device.CreateRenderTargetView(t.Tex);
        t.Srv = _device.CreateShaderResourceView(t.Tex);
        return t;
    }

    private void EnsureTextures(int width, int height)
    {
        // cap the feedback resolution: the look is soft anyway and 4K full screen is costly
        double scale = Math.Min(1, 2048.0 / Math.Max(width, height));
        int w = Math.Max(64, (int)(width * scale)), h = Math.Max(64, (int)(height * scale));
        if (_feedback.Length == 2 && _feedback[0].W == w && _feedback[0].H == h) return;
        var old = _feedback.Length == 2 ? _feedback[_current] : null;
        var fresh = new[] { MakeTarget(w, h), MakeTarget(w, h) };
        foreach (var t in fresh)
        {
            // carry over the old image when resizing (e.g. entering full screen), else clear
            if (old != null) FullscreenPass(_copyPs, t, new[] { old.Srv }, null);
            else _ctx.ClearRenderTargetView(t.Rtv, new Color4(0, 0, 0, 1));
        }
        foreach (var t in _feedback) t.Dispose();
        _blurA?.Dispose();
        _blurB?.Dispose();
        _feedback = fresh;
        _blurA = MakeTarget(w / 2, h / 2);
        _blurB = MakeTarget(w / 2, h / 2);
        _current = 0;
    }

    // ------------------------------------------------------------------ geometry (0…1, y up)

    private static Vector4 Ndc(Vector2 p) => new(p.X * 2 - 1, p.Y * 2 - 1, 0, 1);

    private void Tri(Vector2 a, Vector2 b, Vector2 c, Vector4 ca, Vector4 cb, Vector4 cc)
    {
        _geometry.Add(new GVert(Ndc(a), ca));
        _geometry.Add(new GVert(Ndc(b), cb));
        _geometry.Add(new GVert(Ndc(c), cc));
    }

    private void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector4 col)
    {
        Tri(a, b, c, col, col, col);
        Tri(a, c, d, col, col, col);
    }

    /// Thick polyline; `thick` in pixels at 1080p.
    private void Line(ReadOnlySpan<Vector2> pts, Vector4 color, float thick, float width, float height)
    {
        if (pts.Length < 2) return;
        float px = thick * height / 1080 * 0.5f;
        float sx = 1 / width, sy = 1 / height;
        for (int i = 0; i < pts.Length - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            var d = new Vector2((b.X - a.X) * width, (b.Y - a.Y) * height);
            float len = d.Length();
            if (len < 1e-4f) continue;
            d /= len;
            var n = new Vector2(-d.Y * px * sx, d.X * px * sy);
            Quad(a + n, b + n, b - n, a - n, color);
        }
    }

    private readonly Vector2[] _pts = new Vector2[520];

    private void BuildGeometry(VisParams p, float w, float h, float t, bool additive)
    {
        float aspect = w / h;
        var L = _analyzer.Left;
        var R = _analyzer.Right;
        // wave
        if (p.waveAdditive == additive)
        {
            float alpha = p.waveA * (0.55f + 0.45f * MathF.Min(1.5f, _analyzer.Volume * 2));
            if (!_analyzer.HasSignal) alpha *= 0.3f;
            var col = new Vector4(p.waveR, p.waveG, p.waveB, alpha);
            float sc = p.waveScale;
            switch (p.waveMode)
            {
                case 0: // circle
                {
                    const int n = 256;
                    for (int i = 0; i <= n; i++)
                    {
                        int k = i % n;
                        // blend the seam so the loop closes smoothly
                        float edge = (float)k / n;
                        float s = L[k * 2 % 512] * (1 - edge) + L[(n - k) % n * 2 % 512] * edge;
                        float r = (0.22f + 0.25f * s) * sc;
                        float a = (float)i / n * 2 * MathF.PI + t * 0.2f;
                        _pts[i] = new Vector2(p.waveX + r * MathF.Cos(a) / aspect, p.waveY + r * MathF.Sin(a));
                    }
                    Line(_pts.AsSpan(0, n + 1), col, p.waveThick, w, h);
                    break;
                }
                case 1: // xy scope, rotated 45°
                    for (int i = 0; i < 512; i++)
                    {
                        float u = (L[i] - R[i]) * 0.7f, v = (L[i] + R[i]) * 0.7f;
                        _pts[i] = new Vector2(p.waveX + u * 0.45f * sc / aspect, p.waveY + v * 0.45f * sc);
                    }
                    Line(_pts.AsSpan(0, 512), col, p.waveThick, w, h);
                    break;
                case 2: // horizontal line
                {
                    float amp = 0.25f * sc;
                    for (int i = 0; i < 512; i++) _pts[i] = new Vector2(i / 511f, p.waveY + L[i] * amp);
                    Line(_pts.AsSpan(0, 512), col, p.waveThick, w, h);
                    break;
                }
                case 3: // mirrored spectrum bars, log-spaced
                {
                    const int bands = 64;
                    for (int b = 0; b < bands; b++)
                    {
                        int lo = (int)MathF.Pow(2, (float)b / bands * 8.5f);
                        int hi = Math.Max(lo + 1, (int)MathF.Pow(2, (float)(b + 1) / bands * 8.5f));
                        float m = 0;
                        for (int i = lo; i < Math.Min(hi, 512); i++) m = MathF.Max(m, _analyzer.Spectrum[i]);
                        float x0 = (float)b / bands, x1 = x0 + 0.8f / bands;
                        float hgt = m * 0.35f * sc;
                        foreach (float side in new[] { -1f, 1f })
                        {
                            if (!(side > 0 || p.waveY > 0.3f)) continue;
                            float y0 = p.waveY, y1 = p.waveY + hgt * side;
                            Quad(new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1), col);
                        }
                    }
                    break;
                }
                case 4: // two lines, L above, R below
                {
                    float amp = 0.18f * sc;
                    float yTop = p.waveY + 0.15f, yBottom = p.waveY - 0.15f;
                    for (int i = 0; i < 512; i++) _pts[i] = new Vector2(i / 511f, yTop + L[i] * amp);
                    Line(_pts.AsSpan(0, 512), col, p.waveThick, w, h);
                    for (int i = 0; i < 512; i++) _pts[i] = new Vector2(i / 511f, yBottom + R[i] * amp);
                    Line(_pts.AsSpan(0, 512), col, p.waveThick, w, h);
                    break;
                }
                default: // dot spiral
                {
                    float ds = 2.5f * p.waveThick / 1080;
                    for (int i = 0; i < 512; i += 2)
                    {
                        float f = i / 512f;
                        float a = f * 6 * MathF.PI + t * 0.5f;
                        float r = (0.05f + 0.4f * f + 0.06f * L[i]) * sc;
                        var c = new Vector2(p.waveX + r * MathF.Cos(a) / aspect, p.waveY + r * MathF.Sin(a));
                        Quad(c + new Vector2(-ds / aspect, -ds), c + new Vector2(ds / aspect, -ds),
                             c + new Vector2(ds / aspect, ds), c + new Vector2(-ds / aspect, ds), col);
                    }
                    break;
                }
            }
        }
        // shapes
        foreach (var s in p.shapes)
        {
            if (!s.enabled || s.additive != additive) continue;
            int n = Math.Max(3, (int)MathF.Round(s.sides));
            var c = new Vector2(s.x, s.y);
            var ci = new Vector4(s.r, s.g, s.b, s.a);
            var co = new Vector4(s.r2, s.g2, s.b2, s.a2);
            for (int i = 0; i <= n; i++)
            {
                float a = s.ang + (float)i / n * 2 * MathF.PI + MathF.PI / n;
                _pts[i] = c + new Vector2(s.rad * MathF.Cos(a) / aspect, s.rad * MathF.Sin(a));
            }
            for (int i = 0; i < n; i++) Tri(c, _pts[i], _pts[i + 1], ci, co, co);
            if (s.borderA > 0.001f)
                Line(_pts.AsSpan(0, n + 1), new Vector4(MathF.Min(1, s.r + 0.3f), MathF.Min(1, s.g + 0.3f), MathF.Min(1, s.b + 0.3f), s.borderA),
                     2, w, h);
        }
        // borders (alpha-blended pass only)
        if (!additive)
        {
            void Frame(float inset, float size, Vector4 col)
            {
                if (size <= 0.0001f || col.W <= 0.001f) return;
                float a = inset, b = inset + size;
                Quad(new(a, a), new(1 - a, a), new(1 - a, b), new(a, b), col);
                Quad(new(a, 1 - b), new(1 - a, 1 - b), new(1 - a, 1 - a), new(a, 1 - a), col);
                Quad(new(a, b), new(b, b), new(b, 1 - b), new(a, 1 - b), col);
                Quad(new(1 - b, b), new(1 - a, b), new(1 - a, 1 - b), new(1 - b, 1 - b), col);
            }
            Frame(0, p.obSize, new Vector4(p.obR, p.obG, p.obB, p.obA));
            Frame(p.obSize, p.ibSize, new Vector4(p.ibR, p.ibG, p.ibB, p.ibA));
        }
    }

    // ------------------------------------------------------------------ snapshots

    /// Development aid: renders each preset for 4 s of synthetic music (kick drum + melody)
    /// offscreen and writes a PNG per preset (SurroundPlayer.exe --vis-snapshot <dir>).
    public static Task WriteSnapshotsAsync(string dir, int width = 960, int height = 540) =>
        SnapshotPng.WriteAllAsync(dir, width, height);

    internal List<(string, byte[])> RenderSnapshots(int width, int height)
    {
        var result = new List<(string, byte[])>();
        double phase = 0, t0 = 0;
        double[] notes = { 220.0, 277.2, 329.6, 440.0 };
        FetchSamples = (buf, n) =>
        {
            for (int i = 0; i < n; i++)
            {
                double tt = t0 + i / 48000.0;
                double beatPos = tt % 0.5;
                double kick = Math.Sin(2 * Math.PI * 55 * beatPos) * Math.Exp(-beatPos * 14) * 0.9;
                double note = notes[(int)(tt * 2) % 4];
                phase += 2 * Math.PI * note / 48000;
                double mel = Math.Sin(phase) * 0.25 + Math.Sin(phase * 3.01) * 0.08;
                buf[i * 2] = (float)(kick + mel);
                buf[i * 2 + 1] = (float)(kick + mel * 0.6 + Math.Sin(phase * 1.5) * 0.1);
            }
            return n;
        };
        using var target = MakeTarget(width, height, Format.B8G8R8A8_UNorm);
        using var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        Locked = true;
        _manualTime = 0;
        for (int index = 0; index < VisPreset.All.Length; index++)
        {
            SelectPreset(index);
            _previousIndex = null;
            for (int f = 0; f < 240; f++)   // 4 s at 60 fps
            {
                _manualTime = _presetStart + f / 60.0 + 0.001;
                t0 = _manualTime.Value;
                Render(target.Rtv, width, height);
            }
            _ctx.CopyResource(staging, target.Tex);
            var m = _ctx.Map(staging, 0, MapMode.Read);
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                Marshal.Copy(m.DataPointer + y * (int)m.RowPitch, pixels, y * width * 4, width * 4);
            _ctx.Unmap(staging, 0);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            result.Add((VisPreset.All[index].Name, pixels));
            _presetStart = _manualTime.Value;
        }
        return result;
    }
}

internal static class SnapshotPng
{
    public static async Task WriteAllAsync(string dir, int width, int height)
    {
        Directory.CreateDirectory(dir);
        List<(string name, byte[] bgra)> shots;
        using (var vis = new VisualizerRenderer()) shots = vis.RenderSnapshots(width, height);
        for (int i = 0; i < shots.Count; i++)
            await WriteAsync(Path.Combine(dir, $"{i:00} {shots[i].name}.png"), shots[i].bgra, width, height);
    }

    public static async Task WriteAsync(string path, byte[] bgra, int w, int h)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        enc.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                         (uint)w, (uint)h, 96, 96, bgra);
        await enc.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
    }
}

public sealed unsafe partial class VisualizerRenderer
{
    public void Dispose()
    {
        foreach (var t in _feedback) t.Dispose();
        _blurA?.Dispose();
        _blurB?.Dispose();
        _vb?.Dispose();
        _swapRtv?.Dispose();
        _swap?.Dispose();
        foreach (var s in _samplers) s.Dispose();
        _warpCb.Dispose(); _compCb.Dispose(); _blurCb.Dispose();
        _noCull.Dispose(); _blendAdd.Dispose(); _blendAlpha.Dispose();
        _geomLayout.Dispose();
        _compPs.Dispose(); _blurPs.Dispose(); _copyPs.Dispose(); _geomPs.Dispose(); _warpPs.Dispose();
        _geomVs.Dispose(); _fsVs.Dispose();
        _ctx.Dispose();
        _device.Dispose();
    }
}
