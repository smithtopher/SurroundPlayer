// Port of Sources/SurroundPlayer/Visualizer/VisualizerPresets.swift: the parameters a
// preset controls (MilkDrop's per-frame variables) and the 13 original presets.
// Field names follow the Swift source so the two stay easy to compare.
using System;
using System.Linq;
using System.Reflection;

namespace SurroundPlayer.Visualizer;

#pragma warning disable IDE1006 // lower-case public fields mirror the Swift names

public sealed class ShapeParams
{
    public bool enabled;
    public float sides = 4;
    public float x = 0.5f, y = 0.5f;
    public float rad = 0.1f;
    public float ang;
    public float r = 1, g = 1, b = 1, a = 0.5f;        // centre colour
    public float r2, g2, b2, a2;                        // edge colour
    public float borderA;
    public bool additive = true;

    public ShapeParams Clone() => (ShapeParams)MemberwiseClone();

    public static ShapeParams Lerp(ShapeParams a, ShapeParams b, float t)
    {
        if (a.enabled != b.enabled)
        {
            // fade the appearing/disappearing shape
            var s0 = (a.enabled ? a : b).Clone();
            float k = a.enabled ? 1 - t : t;
            s0.a *= k; s0.a2 *= k; s0.borderA *= k;
            return s0;
        }
        var s = (t < 0.5f ? a : b).Clone();
        float M(float x, float y) => x + (y - x) * t;
        s.sides = M(a.sides, b.sides); s.x = M(a.x, b.x); s.y = M(a.y, b.y); s.rad = M(a.rad, b.rad); s.ang = M(a.ang, b.ang);
        s.r = M(a.r, b.r); s.g = M(a.g, b.g); s.b = M(a.b, b.b); s.a = M(a.a, b.a);
        s.r2 = M(a.r2, b.r2); s.g2 = M(a.g2, b.g2); s.b2 = M(a.b2, b.b2); s.a2 = M(a.a2, b.a2);
        s.borderA = M(a.borderA, b.borderA);
        return s;
    }
}

public sealed class VisParams
{
    // feedback / warp
    public float decay = 0.97f, zoom = 1, zoomExp = 1, zoomRad, rot, rotRad;
    public float warp = 1, warpSpeed = 1, warpScale = 1;
    public float cx = 0.5f, cy = 0.5f, dx, dy, sx = 1, sy = 1;
    public bool wrap = true;

    // wave: 0 circle, 1 xy scope, 2 line, 3 spectrum, 4 dual lines, 5 dot spiral
    public int waveMode;
    public float waveR = 1, waveG = 1, waveB = 1, waveA = 0.8f;
    public float waveScale = 1, waveX = 0.5f, waveY = 0.5f;
    public float waveThick = 2;     // pixels at 1080p
    public bool waveAdditive = true;

    // borders (drawn into the feedback, so they get warped too)
    public float obSize, obR, obG, obB, obA;
    public float ibSize, ibR, ibG, ibB, ibA;

    public ShapeParams[] shapes = { new(), new() };

    // composite (display only)
    public float echoZoom = 1, echoAlpha;
    public int echoOrient;          // 0 none, 1 flip x, 2 flip y, 3 both
    public float hue;               // radians
    public float glow;
    public int kaleido;             // segments, 0 = off
    public float chroma, saturation = 1, gamma = 1, contrast = 1, vignette = 0.3f, darkenCenter;
    public bool brighten, darken, solarize, invert;

    public VisParams Clone()
    {
        var c = (VisParams)MemberwiseClone();
        c.shapes = shapes.Select(s => s.Clone()).ToArray();
        return c;
    }

    // every float field is interpolated when blending presets (Swift: floatPaths)
    private static readonly FieldInfo[] FloatFields =
        typeof(VisParams).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType == typeof(float)).ToArray();

    /// Blend between presets: continuous values interpolate, switches flip halfway.
    public static VisParams Lerp(VisParams a, VisParams b, float t)
    {
        var r = (t < 0.5f ? a : b).Clone();
        foreach (var f in FloatFields)
        {
            float va = (float)f.GetValue(a)!, vb = (float)f.GetValue(b)!;
            f.SetValue(r, va + (vb - va) * t);
        }
        // fade the wave through the mode switch instead of popping
        if (a.waveMode != b.waveMode) r.waveA *= MathF.Abs(t - 0.5f) * 2;
        if (a.kaleido != b.kaleido) r.glow += (1 - MathF.Abs(t - 0.5f) * 2) * 0.3f;
        r.shapes = a.shapes.Zip(b.shapes, (x, y) => ShapeParams.Lerp(x, y, t)).ToArray();
        return r;
    }
}

/// Per-frame inputs, like MilkDrop's `time`, `bass`, `bass_att`, …
public readonly record struct FrameVars(float time, float progress, float bass, float mid, float treb,
                                        float bassAtt, float midAtt, float trebAtt, float volume, bool beat, int beats);

public sealed class VisPreset
{
    public string Name { get; }
    public VisParams Base { get; }
    public Action<VisParams, FrameVars> PerFrame { get; }

    private VisPreset(string name, Action<VisParams> init, Action<VisParams, FrameVars> perFrame)
    {
        Name = name;
        Base = new VisParams();
        init(Base);
        PerFrame = perFrame;
    }

    private static (float, float, float) Rainbow(float t, float speed = 1, float phase = 0)
    {
        float a = t * speed + phase;
        return (0.5f + 0.5f * MathF.Sin(a), 0.5f + 0.5f * MathF.Sin(a + 2.094f), 0.5f + 0.5f * MathF.Sin(a + 4.189f));
    }

    private static float Sin(float x) => MathF.Sin(x);
    private static float Cos(float x) => MathF.Cos(x);

    public static readonly VisPreset[] All =
    {
        new("Nebula Drift", p =>
        {
            p.decay = 0.975f; p.zoom = 1.012f; p.warp = 1.2f; p.waveMode = 0; p.waveScale = 0.9f;
            p.glow = 0.6f; p.vignette = 0.4f;
        }, (p, v) =>
        {
            p.rot = 0.004f * Sin(v.time * 0.21f);
            p.zoom = 1.008f + 0.02f * (v.bassAtt - 1);
            (p.waveR, p.waveG, p.waveB) = Rainbow(v.time, 0.37f);
            p.hue = v.time * 0.05f;
            p.waveScale = 0.8f + 0.4f * v.volume;
        }),

        new("Bass Tunnel", p =>
        {
            p.decay = 0.96f; p.zoom = 1.04f; p.zoomExp = 1.3f; p.warp = 0.2f; p.waveMode = 2;
            p.darkenCenter = 0.8f; p.glow = 0.4f; p.waveThick = 3;
            p.obSize = 0.01f; p.obA = 0.6f;
        }, (p, v) =>
        {
            p.zoom = 1.02f + 0.07f * MathF.Max(0, v.bass - 0.9f);
            p.rot = 0.02f * Sin(v.time * 0.4f);
            (p.obR, p.obG, p.obB) = Rainbow(v.time, 0.6f, 1);
            (p.waveR, p.waveG, p.waveB) = Rainbow(v.time, 0.6f, 3);
            p.waveY = 0.5f + 0.1f * Sin(v.time * 0.7f);
        }),

        new("Kaleidoscope Bloom", p =>
        {
            p.decay = 0.97f; p.zoom = 1.006f; p.warp = 2.0f; p.warpScale = 1.4f; p.waveMode = 1; p.waveScale = 1.4f;
            p.kaleido = 6; p.glow = 0.9f; p.saturation = 1.25f;
        }, (p, v) =>
        {
            p.rot = 0.01f + 0.01f * Sin(v.time * 0.13f);
            (p.waveR, p.waveG, p.waveB) = Rainbow(v.time, 0.5f);
            p.hue = 0.6f * Sin(v.time * 0.05f);
            p.cx = 0.5f + 0.05f * Sin(v.time * 0.31f); p.cy = 0.5f + 0.05f * Cos(v.time * 0.27f);
        }),

        new("Liquid Mirror", p =>
        {
            p.decay = 0.982f; p.zoom = 0.995f; p.warp = 3.5f; p.warpSpeed = 0.7f; p.warpScale = 0.6f;
            p.waveMode = 4; p.echoAlpha = 0.5f; p.echoOrient = 1; p.echoZoom = 1.0f; p.glow = 0.3f;
        }, (p, v) =>
        {
            p.warp = 2.5f + 2 * v.midAtt;
            (p.waveR, p.waveG, p.waveB) = Rainbow(v.time, 0.25f, 2);
            p.dy = 0.002f * Sin(v.time * 0.3f);
        }),

        new("Solar Flare", p =>
        {
            p.decay = 0.95f; p.zoom = 1.02f; p.warp = 0.6f; p.waveMode = 3; p.waveY = 0.12f; p.waveScale = 1.2f;
            p.glow = 0.7f; p.contrast = 1.1f;
        }, (p, v) =>
        {
            p.zoom = 1.015f + 0.03f * (v.trebAtt - 1);
            p.dy = 0.004f + 0.004f * v.bass;
            p.waveR = 1; p.waveG = 0.45f + 0.3f * Sin(v.time); p.waveB = 0.1f;
            p.solarize = v.beats % 8 >= 4;
        }),

        new("Vortex", p =>
        {
            p.decay = 0.97f; p.zoom = 1.0f; p.zoomRad = 0.04f; p.rotRad = 0.06f; p.warp = 0.4f; p.waveMode = 5;
            p.waveA = 1; p.glow = 0.5f;
        }, (p, v) =>
        {
            p.rot = 0.015f * (v.bassAtt - 0.8f);
            p.rotRad = 0.05f + 0.04f * Sin(v.time * 0.2f);
            (p.waveR, p.waveG, p.waveB) = Rainbow(v.time, 1.3f);
            p.hue = v.time * 0.1f;
        }),

        new("Electric Lattice", p =>
        {
            p.decay = 0.94f; p.zoom = 1.01f; p.warp = 0.1f; p.waveMode = 2; p.waveA = 0.5f;
            p.glow = 0.8f; p.wrap = false;
            p.shapes[0] = new ShapeParams { enabled = true, sides = 4, rad = 0.2f, r = 0, g = 0.6f, b = 1, a = 0.08f,
                                            r2 = 0, g2 = 0.1f, b2 = 0.4f, a2 = 0, borderA = 0.8f };
            p.shapes[1] = new ShapeParams { enabled = true, sides = 3, rad = 0.12f, r = 1, g = 0.2f, b = 0.6f, a = 0.1f, borderA = 0.6f };
        }, (p, v) =>
        {
            p.shapes[0].ang = v.time * 0.3f;
            p.shapes[0].rad = 0.15f + 0.1f * v.bassAtt;
            p.shapes[1].ang = -v.time * 0.5f;
            p.shapes[1].x = 0.5f + 0.25f * Cos(v.time * 0.4f);
            p.shapes[1].y = 0.5f + 0.25f * Sin(v.time * 0.4f);
            p.rot = 0.01f * Sin(v.time * 0.17f);
            (p.waveR, p.waveG, p.waveB) = Rainbow(v.time, 0.4f);
        }),

        new("Event Horizon", p =>
        {
            p.decay = 0.985f; p.zoom = 0.975f; p.warp = 0.8f; p.waveMode = 0; p.waveScale = 0.5f;
            p.glow = 0.5f; p.vignette = 0.6f; p.saturation = 1.3f;
        }, (p, v) =>
        {
            p.zoom = 0.985f - 0.02f * MathF.Max(0, v.bass - 1);
            p.rot = 0.008f * Cos(v.time * 0.11f);
            (p.waveR, p.waveG, p.waveB) = Rainbow(v.time, 0.2f, 4);
            p.waveScale = 0.75f + 0.35f * v.trebAtt;
        }),

        new("Chromatic Rain", p =>
        {
            p.decay = 0.965f; p.zoom = 1.0f; p.warp = 1.5f; p.warpScale = 2; p.dy = -0.008f; p.waveMode = 4;
            p.chroma = 0.012f; p.glow = 0.4f;
        }, (p, v) =>
        {
            p.dy = -0.006f - 0.008f * v.bassAtt;
            p.dx = 0.002f * Sin(v.time * 0.5f);
            (p.waveR, p.waveG, p.waveB) = Rainbow(v.time, 0.8f);
            p.chroma = 0.006f + 0.01f * v.treb;
        }),

        new("Prism Engine", p =>
        {
            p.decay = 0.97f; p.zoom = 1.015f; p.warp = 0.5f; p.waveMode = 1; p.kaleido = 3; p.glow = 0.6f;
            p.shapes[0] = new ShapeParams { enabled = true, sides = 3, rad = 0.18f, r = 1, g = 1, b = 1, a = 0.12f,
                                            r2 = 0.2f, g2 = 0, b2 = 0.5f, a2 = 0.05f, borderA = 0.5f };
        }, (p, v) =>
        {
            p.shapes[0].ang = v.time * 0.7f;
            p.shapes[0].rad = 0.1f + 0.12f * v.midAtt;
            (p.shapes[0].r, p.shapes[0].g, p.shapes[0].b) = Rainbow(v.time, 0.3f);
            p.hue = v.time * 0.08f;
            p.kaleido = v.beats % 16 < 8 ? 3 : 5;
        }),

        new("Pulse Grid", p =>
        {
            p.decay = 0.93f; p.zoom = 1.0f; p.sx = 1.01f; p.sy = 0.99f; p.warp = 0.3f; p.waveMode = 3; p.waveY = 0.5f; p.waveA = 0.4f;
            p.ibSize = 0.02f; p.ibA = 0.5f; p.obSize = 0.005f; p.obA = 1; p.wrap = false; p.glow = 0.6f;
        }, (p, v) =>
        {
            p.sx = 1.0f + 0.02f * (v.bass - 1);
            p.sy = 1.0f + 0.02f * (v.treb - 1);
            (p.ibR, p.ibG, p.ibB) = Rainbow(v.time, 0.9f);
            (p.obR, p.obG, p.obB) = Rainbow(v.time, 0.9f, 3);
            p.ibA = MathF.Min(1, 0.3f * v.trebAtt);
            (p.waveR, p.waveG, p.waveB) = Rainbow(v.time, 0.5f, 1);
        }),

        new("Aurora", p =>
        {
            p.decay = 0.985f; p.zoom = 1.0f; p.warp = 2.2f; p.warpSpeed = 0.5f; p.warpScale = 2.5f; p.dy = 0.004f;
            p.waveMode = 2; p.waveY = 0.25f; p.waveThick = 4; p.waveA = 0.5f; p.glow = 1.0f; p.saturation = 1.4f;
        }, (p, v) =>
        {
            p.dy = 0.003f + 0.004f * v.midAtt;
            p.waveR = 0.2f + 0.2f * Sin(v.time * 0.3f); p.waveG = 0.8f; p.waveB = 0.5f + 0.4f * Sin(v.time * 0.21f);
            p.hue = 0.5f * Sin(v.time * 0.04f);
            p.waveY = 0.2f + 0.05f * Sin(v.time * 0.6f);
        }),

        new("Inversion Field", p =>
        {
            p.decay = 0.96f; p.zoom = 1.03f; p.zoomExp = 0.8f; p.warp = 1.0f; p.waveMode = 0; p.echoAlpha = 0.35f;
            p.echoZoom = 1.3f; p.echoOrient = 3; p.glow = 0.4f;
        }, (p, v) =>
        {
            p.rot = 0.03f * Sin(v.time * 0.23f);
            (p.waveR, p.waveG, p.waveB) = Rainbow(v.time, 0.7f, 5);
            p.invert = v.beats % 4 == 3;
            p.echoZoom = 1.2f + 0.2f * Sin(v.time * 0.3f);
        }),
    };
}
