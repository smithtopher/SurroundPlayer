import Foundation

/// Everything a preset controls. Mirrors MilkDrop's per-frame variables: the warp
/// field, the wave, custom shapes, borders, video echo and the composite ops.
struct VisParams {
    // feedback / warp
    var decay: Float = 0.97
    var zoom: Float = 1.0
    var zoomExp: Float = 1.0
    var zoomRad: Float = 0          // zoom varies with distance from centre
    var rot: Float = 0
    var rotRad: Float = 0
    var warp: Float = 1.0
    var warpSpeed: Float = 1.0
    var warpScale: Float = 1.0
    var cx: Float = 0.5, cy: Float = 0.5
    var dx: Float = 0, dy: Float = 0
    var sx: Float = 1, sy: Float = 1
    var wrap = true

    // wave
    var waveMode = 0                // 0 circle, 1 xy scope, 2 line, 3 spectrum, 4 dual lines, 5 dot spiral
    var waveR: Float = 1, waveG: Float = 1, waveB: Float = 1, waveA: Float = 0.8
    var waveScale: Float = 1
    var waveX: Float = 0.5, waveY: Float = 0.5
    var waveThick: Float = 2        // pixels at 1080p
    var waveAdditive = true

    // borders (drawn into the feedback, so they get warped too)
    var obSize: Float = 0, obR: Float = 0, obG: Float = 0, obB: Float = 0, obA: Float = 0
    var ibSize: Float = 0, ibR: Float = 0, ibG: Float = 0, ibB: Float = 0, ibA: Float = 0

    var shapes: [ShapeParams] = [ShapeParams(), ShapeParams()]

    // composite (display only)
    var echoZoom: Float = 1, echoAlpha: Float = 0
    var echoOrient = 0              // 0 none, 1 flip x, 2 flip y, 3 both
    var hue: Float = 0              // radians
    var glow: Float = 0
    var kaleido = 0                 // segments, 0 = off
    var chroma: Float = 0
    var saturation: Float = 1
    var gamma: Float = 1
    var contrast: Float = 1
    var vignette: Float = 0.3
    var darkenCenter: Float = 0
    var brighten = false, darken = false, solarize = false, invert = false

    static let floatPaths: [WritableKeyPath<VisParams, Float>] = [
        \.decay, \.zoom, \.zoomExp, \.zoomRad, \.rot, \.rotRad, \.warp, \.warpSpeed, \.warpScale,
        \.cx, \.cy, \.dx, \.dy, \.sx, \.sy,
        \.waveR, \.waveG, \.waveB, \.waveA, \.waveScale, \.waveX, \.waveY, \.waveThick,
        \.obSize, \.obR, \.obG, \.obB, \.obA, \.ibSize, \.ibR, \.ibG, \.ibB, \.ibA,
        \.echoZoom, \.echoAlpha, \.hue, \.glow, \.chroma, \.saturation, \.gamma, \.contrast, \.vignette, \.darkenCenter,
    ]

    /// Blend between presets: continuous values interpolate, switches flip halfway.
    static func lerp(_ a: VisParams, _ b: VisParams, _ t: Float) -> VisParams {
        var r = t < 0.5 ? a : b
        for kp in floatPaths { r[keyPath: kp] = a[keyPath: kp] + (b[keyPath: kp] - a[keyPath: kp]) * t }
        // fade the wave through the mode switch instead of popping
        if a.waveMode != b.waveMode { r.waveA *= abs(t - 0.5) * 2 }
        if a.kaleido != b.kaleido { r.glow += (1 - abs(t - 0.5) * 2) * 0.3 }
        r.shapes = zip(a.shapes, b.shapes).map { ShapeParams.lerp($0, $1, t) }
        return r
    }
}

struct ShapeParams {
    var enabled = false
    var sides: Float = 4
    var x: Float = 0.5, y: Float = 0.5
    var rad: Float = 0.1
    var ang: Float = 0
    var r: Float = 1, g: Float = 1, b: Float = 1, a: Float = 0.5        // centre colour
    var r2: Float = 0, g2: Float = 0, b2: Float = 0, a2: Float = 0      // edge colour
    var borderA: Float = 0
    var additive = true

    static func lerp(_ a: ShapeParams, _ b: ShapeParams, _ t: Float) -> ShapeParams {
        if a.enabled != b.enabled {
            // fade the appearing/disappearing shape
            var s = a.enabled ? a : b
            let k = a.enabled ? 1 - t : t
            s.a *= k; s.a2 *= k; s.borderA *= k
            return s
        }
        var s = t < 0.5 ? a : b
        func m(_ x: Float, _ y: Float) -> Float { x + (y - x) * t }
        s.sides = m(a.sides, b.sides); s.x = m(a.x, b.x); s.y = m(a.y, b.y); s.rad = m(a.rad, b.rad); s.ang = m(a.ang, b.ang)
        s.r = m(a.r, b.r); s.g = m(a.g, b.g); s.b = m(a.b, b.b); s.a = m(a.a, b.a)
        s.r2 = m(a.r2, b.r2); s.g2 = m(a.g2, b.g2); s.b2 = m(a.b2, b.b2); s.a2 = m(a.a2, b.a2)
        s.borderA = m(a.borderA, b.borderA)
        return s
    }
}

/// Per-frame inputs, like MilkDrop's `time`, `bass`, `bass_att`, …
struct FrameVars {
    var time: Float
    var progress: Float      // seconds since this preset started
    var bass: Float, mid: Float, treb: Float
    var bassAtt: Float, midAtt: Float, trebAtt: Float
    var volume: Float
    var beat: Bool
    var beats: Int           // beats counted since the preset started
}

struct VisPreset {
    let name: String
    let base: VisParams
    let perFrame: (inout VisParams, FrameVars) -> Void
}

private func rainbow(_ t: Float, _ speed: Float = 1, phase: Float = 0) -> (Float, Float, Float) {
    let a = t * speed + phase
    return (0.5 + 0.5 * sin(a), 0.5 + 0.5 * sin(a + 2.094), 0.5 + 0.5 * sin(a + 4.189))
}

extension VisPreset {
    static let all: [VisPreset] = [
        VisPreset(name: "Nebula Drift", base: {
            var p = VisParams()
            p.decay = 0.975; p.zoom = 1.012; p.warp = 1.2; p.waveMode = 0; p.waveScale = 0.9
            p.glow = 0.6; p.vignette = 0.4
            return p
        }()) { p, v in
            p.rot = 0.004 * sin(v.time * 0.21)
            p.zoom = 1.008 + 0.02 * (v.bassAtt - 1)
            (p.waveR, p.waveG, p.waveB) = rainbow(v.time, 0.37)
            p.hue = v.time * 0.05
            p.waveScale = 0.8 + 0.4 * v.volume
        },

        VisPreset(name: "Bass Tunnel", base: {
            var p = VisParams()
            p.decay = 0.96; p.zoom = 1.04; p.zoomExp = 1.3; p.warp = 0.2; p.waveMode = 2
            p.darkenCenter = 0.8; p.glow = 0.4; p.waveThick = 3
            p.obSize = 0.01; p.obA = 0.6
            return p
        }()) { p, v in
            p.zoom = 1.02 + 0.07 * max(0, v.bass - 0.9)
            p.rot = 0.02 * sin(v.time * 0.4)
            (p.obR, p.obG, p.obB) = rainbow(v.time, 0.6, phase: 1)
            (p.waveR, p.waveG, p.waveB) = rainbow(v.time, 0.6, phase: 3)
            p.waveY = 0.5 + 0.1 * sin(v.time * 0.7)
        },

        VisPreset(name: "Kaleidoscope Bloom", base: {
            var p = VisParams()
            p.decay = 0.97; p.zoom = 1.006; p.warp = 2.0; p.warpScale = 1.4; p.waveMode = 1; p.waveScale = 1.4
            p.kaleido = 6; p.glow = 0.9; p.saturation = 1.25
            return p
        }()) { p, v in
            p.rot = 0.01 + 0.01 * sin(v.time * 0.13)
            (p.waveR, p.waveG, p.waveB) = rainbow(v.time, 0.5)
            p.hue = 0.6 * sin(v.time * 0.05)
            p.cx = 0.5 + 0.05 * sin(v.time * 0.31); p.cy = 0.5 + 0.05 * cos(v.time * 0.27)
        },

        VisPreset(name: "Liquid Mirror", base: {
            var p = VisParams()
            p.decay = 0.982; p.zoom = 0.995; p.warp = 3.5; p.warpSpeed = 0.7; p.warpScale = 0.6
            p.waveMode = 4; p.echoAlpha = 0.5; p.echoOrient = 1; p.echoZoom = 1.0; p.glow = 0.3
            return p
        }()) { p, v in
            p.warp = 2.5 + 2 * v.midAtt
            (p.waveR, p.waveG, p.waveB) = rainbow(v.time, 0.25, phase: 2)
            p.dy = 0.002 * sin(v.time * 0.3)
        },

        VisPreset(name: "Solar Flare", base: {
            var p = VisParams()
            p.decay = 0.95; p.zoom = 1.02; p.warp = 0.6; p.waveMode = 3; p.waveY = 0.12; p.waveScale = 1.2
            p.glow = 0.7; p.contrast = 1.1
            return p
        }()) { p, v in
            p.zoom = 1.015 + 0.03 * (v.trebAtt - 1)
            p.dy = 0.004 + 0.004 * v.bass
            p.waveR = 1; p.waveG = 0.45 + 0.3 * sin(v.time); p.waveB = 0.1
            p.solarize = v.beats % 8 >= 4
        },

        VisPreset(name: "Vortex", base: {
            var p = VisParams()
            p.decay = 0.97; p.zoom = 1.0; p.zoomRad = 0.04; p.rotRad = 0.06; p.warp = 0.4; p.waveMode = 5
            p.waveA = 1; p.glow = 0.5
            return p
        }()) { p, v in
            p.rot = 0.015 * (v.bassAtt - 0.8)
            p.rotRad = 0.05 + 0.04 * sin(v.time * 0.2)
            (p.waveR, p.waveG, p.waveB) = rainbow(v.time, 1.3)
            p.hue = v.time * 0.1
        },

        VisPreset(name: "Electric Lattice", base: {
            var p = VisParams()
            p.decay = 0.94; p.zoom = 1.01; p.warp = 0.1; p.waveMode = 2; p.waveA = 0.5
            p.glow = 0.8; p.wrap = false
            p.shapes[0] = ShapeParams(enabled: true, sides: 4, rad: 0.2, r: 0, g: 0.6, b: 1, a: 0.08, r2: 0, g2: 0.1, b2: 0.4, a2: 0.0, borderA: 0.8)
            p.shapes[1] = ShapeParams(enabled: true, sides: 3, rad: 0.12, r: 1, g: 0.2, b: 0.6, a: 0.1, borderA: 0.6)
            return p
        }()) { p, v in
            p.shapes[0].ang = v.time * 0.3
            p.shapes[0].rad = 0.15 + 0.1 * v.bassAtt
            p.shapes[1].ang = -v.time * 0.5
            p.shapes[1].x = 0.5 + 0.25 * cos(v.time * 0.4)
            p.shapes[1].y = 0.5 + 0.25 * sin(v.time * 0.4)
            p.rot = 0.01 * sin(v.time * 0.17)
            (p.waveR, p.waveG, p.waveB) = rainbow(v.time, 0.4)
        },

        VisPreset(name: "Event Horizon", base: {
            var p = VisParams()
            p.decay = 0.985; p.zoom = 0.975; p.warp = 0.8; p.waveMode = 0; p.waveScale = 0.5
            p.glow = 0.5; p.vignette = 0.6; p.saturation = 1.3
            return p
        }()) { p, v in
            p.zoom = 0.985 - 0.02 * max(0, v.bass - 1)
            p.rot = 0.008 * cos(v.time * 0.11)
            (p.waveR, p.waveG, p.waveB) = rainbow(v.time, 0.2, phase: 4)
            p.waveScale = 0.75 + 0.35 * v.trebAtt
        },

        VisPreset(name: "Chromatic Rain", base: {
            var p = VisParams()
            p.decay = 0.965; p.zoom = 1.0; p.warp = 1.5; p.warpScale = 2; p.dy = -0.008; p.waveMode = 4
            p.chroma = 0.012; p.glow = 0.4
            return p
        }()) { p, v in
            p.dy = -0.006 - 0.008 * v.bassAtt
            p.dx = 0.002 * sin(v.time * 0.5)
            (p.waveR, p.waveG, p.waveB) = rainbow(v.time, 0.8)
            p.chroma = 0.006 + 0.01 * v.treb
        },

        VisPreset(name: "Prism Engine", base: {
            var p = VisParams()
            p.decay = 0.97; p.zoom = 1.015; p.warp = 0.5; p.waveMode = 1; p.kaleido = 3; p.glow = 0.6
            p.shapes[0] = ShapeParams(enabled: true, sides: 3, rad: 0.18, r: 1, g: 1, b: 1, a: 0.12, r2: 0.2, g2: 0.0, b2: 0.5, a2: 0.05, borderA: 0.5)
            return p
        }()) { p, v in
            p.shapes[0].ang = v.time * 0.7
            p.shapes[0].rad = 0.1 + 0.12 * v.midAtt
            (p.shapes[0].r, p.shapes[0].g, p.shapes[0].b) = rainbow(v.time, 0.3)
            p.hue = v.time * 0.08
            p.kaleido = v.beats % 16 < 8 ? 3 : 5
        },

        VisPreset(name: "Pulse Grid", base: {
            var p = VisParams()
            p.decay = 0.93; p.zoom = 1.0; p.sx = 1.01; p.sy = 0.99; p.warp = 0.3; p.waveMode = 3; p.waveY = 0.5; p.waveA = 0.4
            p.ibSize = 0.02; p.ibA = 0.5; p.obSize = 0.005; p.obA = 1; p.wrap = false; p.glow = 0.6
            return p
        }()) { p, v in
            p.sx = 1.0 + 0.02 * (v.bass - 1)
            p.sy = 1.0 + 0.02 * (v.treb - 1)
            (p.ibR, p.ibG, p.ibB) = rainbow(v.time, 0.9)
            (p.obR, p.obG, p.obB) = rainbow(v.time, 0.9, phase: 3)
            p.ibA = min(1, 0.3 * v.trebAtt)
            (p.waveR, p.waveG, p.waveB) = rainbow(v.time, 0.5, phase: 1)
        },

        VisPreset(name: "Aurora", base: {
            var p = VisParams()
            p.decay = 0.985; p.zoom = 1.0; p.warp = 2.2; p.warpSpeed = 0.5; p.warpScale = 2.5; p.dy = 0.004
            p.waveMode = 2; p.waveY = 0.25; p.waveThick = 4; p.waveA = 0.5; p.glow = 1.0; p.saturation = 1.4
            return p
        }()) { p, v in
            p.dy = 0.003 + 0.004 * v.midAtt
            p.waveR = 0.2 + 0.2 * sin(v.time * 0.3); p.waveG = 0.8; p.waveB = 0.5 + 0.4 * sin(v.time * 0.21)
            p.hue = 0.5 * sin(v.time * 0.04)
            p.waveY = 0.2 + 0.05 * sin(v.time * 0.6)
        },

        VisPreset(name: "Inversion Field", base: {
            var p = VisParams()
            p.decay = 0.96; p.zoom = 1.03; p.zoomExp = 0.8; p.warp = 1.0; p.waveMode = 0; p.echoAlpha = 0.35
            p.echoZoom = 1.3; p.echoOrient = 3; p.glow = 0.4
            return p
        }()) { p, v in
            p.rot = 0.03 * sin(v.time * 0.23)
            (p.waveR, p.waveG, p.waveB) = rainbow(v.time, 0.7, phase: 5)
            p.invert = v.beats % 4 == 3
            p.echoZoom = 1.2 + 0.2 * sin(v.time * 0.3)
        },
    ]
}
