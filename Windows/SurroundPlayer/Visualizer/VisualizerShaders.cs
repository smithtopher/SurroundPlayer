// HLSL port of Sources/SurroundPlayer/Visualizer/VisualizerShaders.swift (Metal).
// Pipeline per frame, in the spirit of MilkDrop 2:
//   1. warp:      previous frame -> current frame through a per-pixel zoom/rotate/warp
//                 field, faded by `decay` (this feedback loop is the whole look)
//   2. geometry:  waveforms, shapes and borders drawn into the current frame
//   3. blur:      half-res Gaussian blur of the frame (MilkDrop 2's blur textures)
//   4. composite: frame -> screen with kaleidoscope, echo, glow, hue, chromatic
//                 aberration and the classic brighten/darken/solarize/invert ops
// Conventions match Metal: uv origin top-left, NDC y up.
namespace SurroundPlayer.Visualizer;

internal static class VisualizerShaders
{
    public const string Source = @"
struct VOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VOut fs_vertex(uint vid : SV_VertexID) {
    float2 p = float2((vid << 1) & 2, vid & 2);
    VOut o;
    o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
    o.uv = float2(p.x, 1.0 - p.y);
    return o;
}

cbuffer WarpU : register(b0) {
    float time, decay, zoom, zoomExp;
    float rot, warpAmt, warpSpeed, warpScale;
    float cx, cy, dx, dy;
    float sx, sy, zoomRad, rotRad;
    float aspect, wrapMode, wpad0, wpad1;
};

Texture2D<float4> tex0 : register(t0);
Texture2D<float4> tex1 : register(t1);
SamplerState sRep : register(s0);
SamplerState sClamp : register(s1);
SamplerState sMirror : register(s2);

float4 warp_fs(VOut i) : SV_Target {
    float2 c = float2(cx, 1.0 - cy);
    float2 p = i.uv - c;
    p.x *= aspect;
    float rad = length(p) * 1.4142;
    float z = max(zoom + zoomRad * rad, 0.01);
    z = pow(z, pow(max(zoomExp, 0.01), rad * 2.0 - 1.0));
    p /= z;
    float r = rot + rotRad * rad;
    float cr = cos(r), sr = sin(r);
    p = float2(p.x * cr - p.y * sr, p.x * sr + p.y * cr);
    p /= float2(max(sx, 0.01), max(sy, 0.01));
    float t = time * warpSpeed;
    float k = 1.0 / max(warpScale, 0.01);
    float2 w;
    w.x = sin(t * 0.333 + k * (p.x * 7.1 - p.y * 5.3)) + cos(t * 0.753 - k * (p.x * 4.7 - p.y * 8.9));
    w.y = cos(t * 0.375 - k * (p.x * 6.2 + p.y * 7.7)) + sin(t * 0.825 + k * (p.x * 5.9 + p.y * 4.1));
    p += warpAmt * 0.0035 * w;
    p -= float2(dx, -dy);
    p.x /= aspect;
    float2 uv = p + c;
    float3 col = wrapMode > 0.5 ? tex0.Sample(sRep, uv).rgb : tex0.Sample(sClamp, uv).rgb;
    col = col * decay - 0.0015;
    return float4(max(col, 0.0), 1.0);
}

struct GIn { float4 pos : POSITION; float4 color : COLOR; };
struct GOut { float4 pos : SV_Position; float4 color : COLOR; };
GOut geom_vs(GIn v) { GOut o; o.pos = v.pos; o.color = v.color; return o; }
float4 geom_fs(GOut i) : SV_Target { return i.color; }

float4 copy_fs(VOut i) : SV_Target { return float4(tex0.Sample(sClamp, i.uv).rgb, 1.0); }

cbuffer BlurU : register(b0) { float2 dir; float2 bpad; };
static const float bw[5] = { 0.2270270270, 0.1945945946, 0.1216216216, 0.0540540541, 0.0162162162 };
float4 blur_fs(VOut i) : SV_Target {
    float3 c = tex0.Sample(sClamp, i.uv).rgb * bw[0];
    [unroll] for (int n = 1; n < 5; n++) {
        c += tex0.Sample(sClamp, i.uv + dir * n).rgb * bw[n];
        c += tex0.Sample(sClamp, i.uv - dir * n).rgb * bw[n];
    }
    return float4(c, 1.0);
}

cbuffer CompU : register(b0) {
    float ctime, gamma, echoZoom, echoAlpha;
    float echoOrient, hue, glow, kaleido;
    float chroma, solarize, invertOp, brighten;
    float darken, darkenCenter, caspect, contrast;
    float vignette, saturation, fade, cpad0;
};

float4 comp_fs(VOut i) : SV_Target {
    float2 uv = i.uv;
    if (kaleido >= 2.0) {
        float2 p = uv - 0.5;
        p.x *= caspect;
        float r = length(p);
        float a = atan2(p.y, p.x) + ctime * 0.05;
        float seg = 6.2831853 / floor(kaleido);
        a = abs(fmod(a + 100.0 * seg, seg) - seg * 0.5);
        p = r * float2(cos(a), sin(a));
        p.x /= caspect;
        uv = p + 0.5;
    }
    float2 d = (uv - 0.5) * chroma;
    float3 col = float3(tex0.Sample(sMirror, uv + d).r, tex0.Sample(sMirror, uv).g, tex0.Sample(sMirror, uv - d).b);
    if (echoAlpha > 0.001) {
        float2 e = (uv - 0.5) / max(echoZoom, 0.01);
        int o = int(echoOrient + 0.5);
        if (o == 1 || o == 3) e.x = -e.x;
        if (o == 2 || o == 3) e.y = -e.y;
        col = lerp(col, tex0.Sample(sMirror, e + 0.5).rgb, echoAlpha);
    }
    col += tex1.Sample(sMirror, uv).rgb * glow;
    if (hue != 0.0) {
        float3 k = (float3)0.57735;
        float ca = cos(hue);
        col = col * ca + cross(k, col) * sin(hue) + k * dot(k, col) * (1.0 - ca);
    }
    float l = dot(col, float3(0.299, 0.587, 0.114));
    col = lerp((float3)l, col, saturation);
    col *= gamma;
    col = max((col - 0.5) * contrast + 0.5, 0.0);
    if (darkenCenter > 0.0) {
        float2 p = i.uv - 0.5;
        p.x *= caspect;
        col *= lerp(1.0, smoothstep(0.0, 0.35, length(p)), darkenCenter);
    }
    col = saturate(col);
    if (brighten > 0.5) col = sqrt(col);
    if (darken > 0.5) col = col * col;
    if (solarize > 0.5) col = col * (1.0 - col) * 4.0;
    if (invertOp > 0.5) col = 1.0 - col;
    float2 v = i.uv - 0.5;
    col *= 1.0 - vignette * dot(v, v) * 2.0;
    return float4(col * fade, 1.0);
}
";
}
