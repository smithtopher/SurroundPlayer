// Metal shaders for the visualiser, compiled at runtime (the Command Line Tools have no
// offline `metal` compiler). Pipeline per frame, in the spirit of MilkDrop 2:
//   1. warp:      previous frame -> current frame through a per-pixel zoom/rotate/warp
//                 field, faded by `decay` (this feedback loop is the whole look)
//   2. geometry:  waveforms, shapes and borders drawn into the current frame
//   3. blur:      half-res Gaussian blur of the frame (MilkDrop 2's blur textures)
//   4. composite: frame -> screen with kaleidoscope, echo, glow, hue, chromatic
//                 aberration and the classic brighten/darken/solarize/invert ops
// Only the composite is display-only; everything before it feeds back.
let visualizerShaderSource = """
#include <metal_stdlib>
using namespace metal;

struct VOut { float4 pos [[position]]; float2 uv; };

vertex VOut fs_vertex(uint vid [[vertex_id]]) {
    float2 p = float2((vid << 1) & 2, vid & 2);
    VOut o;
    o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
    o.uv = float2(p.x, 1.0 - p.y);
    return o;
}

struct WarpU {
    float time, decay, zoom, zoomExp;
    float rot, warp, warpSpeed, warpScale;
    float cx, cy, dx, dy;
    float sx, sy, zoomRad, rotRad;
    float aspect, wrap, pad0, pad1;
};

fragment float4 warp_fs(VOut in [[stage_in]], texture2d<float> prev [[texture(0)]], constant WarpU &u [[buffer(0)]]) {
    constexpr sampler sRep(filter::linear, address::repeat);
    constexpr sampler sClamp(filter::linear, address::clamp_to_edge);
    float2 c = float2(u.cx, 1.0 - u.cy);
    float2 p = in.uv - c;
    p.x *= u.aspect;
    float rad = length(p) * 1.4142;
    float zoom = max(u.zoom + u.zoomRad * rad, 0.01);
    zoom = pow(zoom, pow(max(u.zoomExp, 0.01), rad * 2.0 - 1.0));
    p /= zoom;
    float r = u.rot + u.rotRad * rad;
    float cr = cos(r), sr = sin(r);
    p = float2(p.x * cr - p.y * sr, p.x * sr + p.y * cr);
    p /= float2(max(u.sx, 0.01), max(u.sy, 0.01));
    float t = u.time * u.warpSpeed;
    float k = 1.0 / max(u.warpScale, 0.01);
    float2 w;
    w.x = sin(t * 0.333 + k * (p.x * 7.1 - p.y * 5.3)) + cos(t * 0.753 - k * (p.x * 4.7 - p.y * 8.9));
    w.y = cos(t * 0.375 - k * (p.x * 6.2 + p.y * 7.7)) + sin(t * 0.825 + k * (p.x * 5.9 + p.y * 4.1));
    p += u.warp * 0.0035 * w;
    p -= float2(u.dx, -u.dy);
    p.x /= u.aspect;
    float2 uv = p + c;
    float3 col = u.wrap > 0.5 ? prev.sample(sRep, uv).rgb : prev.sample(sClamp, uv).rgb;
    col = col * u.decay - 0.0015;
    return float4(max(col, 0.0), 1.0);
}

struct GVert { float4 pos; float4 color; };
struct GOut { float4 pos [[position]]; float4 color; };

vertex GOut geom_vs(uint vid [[vertex_id]], const device GVert *v [[buffer(0)]]) {
    GOut o;
    o.pos = v[vid].pos;
    o.color = v[vid].color;
    return o;
}

fragment float4 geom_fs(GOut in [[stage_in]]) { return in.color; }

fragment float4 copy_fs(VOut in [[stage_in]], texture2d<float> src [[texture(0)]]) {
    constexpr sampler s(filter::linear, address::clamp_to_edge);
    return float4(src.sample(s, in.uv).rgb, 1.0);
}

fragment float4 blur_fs(VOut in [[stage_in]], texture2d<float> src [[texture(0)]], constant float2 &dir [[buffer(0)]]) {
    constexpr sampler s(filter::linear, address::clamp_to_edge);
    const float w[5] = { 0.2270270270, 0.1945945946, 0.1216216216, 0.0540540541, 0.0162162162 };
    float3 c = src.sample(s, in.uv).rgb * w[0];
    for (int i = 1; i < 5; i++) {
        c += src.sample(s, in.uv + dir * float(i)).rgb * w[i];
        c += src.sample(s, in.uv - dir * float(i)).rgb * w[i];
    }
    return float4(c, 1.0);
}

struct CompU {
    float time, gamma, echoZoom, echoAlpha;
    float echoOrient, hue, glow, kaleido;
    float chroma, solarize, invert, brighten;
    float darken, darkenCenter, aspect, contrast;
    float vignette, saturation, fade, pad0;
};

fragment float4 comp_fs(VOut in [[stage_in]], texture2d<float> mainT [[texture(0)]], texture2d<float> blurT [[texture(1)]],
                        constant CompU &u [[buffer(0)]]) {
    constexpr sampler s(filter::linear, address::mirrored_repeat);
    float2 uv = in.uv;
    if (u.kaleido >= 2.0) {
        float2 p = uv - 0.5;
        p.x *= u.aspect;
        float r = length(p);
        float a = atan2(p.y, p.x) + u.time * 0.05;
        float seg = 6.2831853 / floor(u.kaleido);
        a = abs(fmod(a + 100.0 * seg, seg) - seg * 0.5);
        p = r * float2(cos(a), sin(a));
        p.x /= u.aspect;
        uv = p + 0.5;
    }
    float2 d = (uv - 0.5) * u.chroma;
    float3 col = float3(mainT.sample(s, uv + d).r, mainT.sample(s, uv).g, mainT.sample(s, uv - d).b);
    if (u.echoAlpha > 0.001) {
        float2 e = (uv - 0.5) / max(u.echoZoom, 0.01);
        int o = int(u.echoOrient + 0.5);
        if (o == 1 || o == 3) e.x = -e.x;
        if (o == 2 || o == 3) e.y = -e.y;
        col = mix(col, mainT.sample(s, e + 0.5).rgb, u.echoAlpha);
    }
    col += blurT.sample(s, uv).rgb * u.glow;
    if (u.hue != 0.0) {
        float3 k = float3(0.57735);
        float ca = cos(u.hue);
        col = col * ca + cross(k, col) * sin(u.hue) + k * dot(k, col) * (1.0 - ca);
    }
    float l = dot(col, float3(0.299, 0.587, 0.114));
    col = mix(float3(l), col, u.saturation);
    col *= u.gamma;
    col = max((col - 0.5) * u.contrast + 0.5, 0.0);
    if (u.darkenCenter > 0.0) {
        float2 p = in.uv - 0.5;
        p.x *= u.aspect;
        col *= mix(1.0, smoothstep(0.0, 0.35, length(p)), u.darkenCenter);
    }
    col = clamp(col, 0.0, 1.0);
    if (u.brighten > 0.5) col = sqrt(col);
    if (u.darken > 0.5) col = col * col;
    if (u.solarize > 0.5) col = col * (1.0 - col) * 4.0;
    if (u.invert > 0.5) col = 1.0 - col;
    float2 v = in.uv - 0.5;
    col *= 1.0 - u.vignette * dot(v, v) * 2.0;
    return float4(col * u.fade, 1.0);
}
"""
