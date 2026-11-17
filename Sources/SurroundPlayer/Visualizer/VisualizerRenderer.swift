import AppKit
import Metal
import MetalKit
import simd

private struct WarpUniforms {
    var time: Float, decay: Float, zoom: Float, zoomExp: Float
    var rot: Float, warp: Float, warpSpeed: Float, warpScale: Float
    var cx: Float, cy: Float, dx: Float, dy: Float
    var sx: Float, sy: Float, zoomRad: Float, rotRad: Float
    var aspect: Float, wrap: Float, pad0: Float = 0, pad1: Float = 0
}

private struct CompUniforms {
    var time: Float, gamma: Float, echoZoom: Float, echoAlpha: Float
    var echoOrient: Float, hue: Float, glow: Float, kaleido: Float
    var chroma: Float, solarize: Float, invert: Float, brighten: Float
    var darken: Float, darkenCenter: Float, aspect: Float, contrast: Float
    var vignette: Float, saturation: Float, fade: Float, pad0: Float = 0
}

private struct GVert {
    var pos: SIMD4<Float>
    var color: SIMD4<Float>
}

/// MilkDrop-style renderer. Owned by the player model so the feedback image and
/// preset survive the view being torn down (e.g. going full screen).
final class VisualizerRenderer: NSObject, MTKViewDelegate {
    let device: MTLDevice
    private let queue: MTLCommandQueue
    private let warpPipe, geomAddPipe, geomAlphaPipe, copyPipe, blurPipe: MTLRenderPipelineState
    private var compPipe: MTLRenderPipelineState?
    private var compFormat: MTLPixelFormat = .invalid
    private var feedback: [MTLTexture] = []      // ping-pong
    private var blurA: MTLTexture?, blurB: MTLTexture?
    private var current = 0
    private let analyzer = AudioAnalyzer()
    private var geometry: [GVert] = []

    // presets
    private var order: [Int] = []
    private var orderPos = 0
    private var presetIndex = 0
    private var previousIndex: Int?
    private var blendStart: Double = 0
    private let blendDuration: Double = 2.5
    private var presetStart: Double = 0
    private var presetBeats = 0
    private var prevBeats = 0
    var locked = false
    var onPresetChange: ((String) -> Void)?

    /// Called once per frame to fetch the audio leaving the speakers (interleaved L/R).
    var fetchSamples: (UnsafeMutablePointer<Float>, Int) -> Int = { _, _ in 0 }

    private let start = CACurrentMediaTime()
    private var lastFrame = CACurrentMediaTime()
    private var fadeIn: Float = 0

    var presetName: String { VisPreset.all[presetIndex].name }
    private(set) var framesRendered = 0
    var debugStatus: String {
        String(format: "vis frames=%d signal=%@ bass=%.2f preset=%@", framesRendered, analyzer.hasSignal ? "yes" : "no", analyzer.bass, presetName)
    }

    init?(device: MTLDevice? = MTLCreateSystemDefaultDevice()) {
        guard let device, let queue = device.makeCommandQueue(),
              let lib = try? device.makeLibrary(source: visualizerShaderSource, options: nil) else { return nil }
        self.device = device
        self.queue = queue
        func pipe(_ v: String, _ f: String, _ fmt: MTLPixelFormat, blend: Int = 0) -> MTLRenderPipelineState? {
            let d = MTLRenderPipelineDescriptor()
            d.vertexFunction = lib.makeFunction(name: v)
            d.fragmentFunction = lib.makeFunction(name: f)
            d.colorAttachments[0].pixelFormat = fmt
            if blend != 0 {
                let c = d.colorAttachments[0]!
                c.isBlendingEnabled = true
                c.rgbBlendOperation = .add
                c.alphaBlendOperation = .add
                c.sourceRGBBlendFactor = .sourceAlpha
                c.destinationRGBBlendFactor = blend == 1 ? .one : .oneMinusSourceAlpha
                c.sourceAlphaBlendFactor = .zero
                c.destinationAlphaBlendFactor = .one
            }
            return try? device.makeRenderPipelineState(descriptor: d)
        }
        guard let w = pipe("fs_vertex", "warp_fs", .rgba16Float),
              let ga = pipe("geom_vs", "geom_fs", .rgba16Float, blend: 1),
              let gb = pipe("geom_vs", "geom_fs", .rgba16Float, blend: 2),
              let c = pipe("fs_vertex", "copy_fs", .rgba16Float),
              let b = pipe("fs_vertex", "blur_fs", .rgba16Float) else { return nil }
        warpPipe = w; geomAddPipe = ga; geomAlphaPipe = gb; copyPipe = c; blurPipe = b
        super.init()
        reshuffle()
        presetIndex = order[0]
    }

    // MARK: preset control

    private func reshuffle() {
        order = Array(VisPreset.all.indices).shuffled()
        orderPos = 0
    }

    func nextPreset() { switchTo(nextInOrder(), now: now) }

    func selectPreset(_ index: Int) { switchTo(index, now: now) }

    /// Development aid: renders each preset for a few seconds of synthetic music
    /// (kick drum + melody) offscreen and writes a PNG per preset.
    func writeSnapshots(to dir: URL, width: Int = 960, height: Int = 540) {
        var phase: Double = 0
        var t0: Double = 0
        fetchSamples = { buf, n in
            for i in 0..<n {
                let tt = t0 + Double(i) / 48000
                let beatPos = tt.truncatingRemainder(dividingBy: 0.5)
                let kick = sin(2 * .pi * 55 * beatPos) * exp(-beatPos * 14) * 0.9
                let note = [220.0, 277.2, 329.6, 440.0][Int(tt * 2) % 4]
                phase += 2 * .pi * note / 48000
                let mel = sin(phase) * 0.25 + sin(phase * 3.01) * 0.08
                buf[i * 2] = Float(kick + mel)
                buf[i * 2 + 1] = Float(kick + mel * 0.6 + sin(phase * 1.5) * 0.1)
            }
            return n
        }
        let d = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: width, height: height, mipmapped: false)
        d.usage = [.renderTarget]
        d.storageMode = .managed
        guard let target = device.makeTexture(descriptor: d) else { return }
        locked = true
        for (index, preset) in VisPreset.all.enumerated() {
            selectPreset(index)
            previousIndex = nil
            for f in 0..<240 {                      // 4 s at 60 fps
                manualTime = presetStart + Double(f) / 60 + 0.001
                t0 = manualTime!
                guard let cmd = queue.makeCommandBuffer() else { return }
                render(cmd, target: target, size: CGSize(width: width, height: height))
                if f == 239, let blit = cmd.makeBlitCommandEncoder() { blit.synchronize(resource: target); blit.endEncoding() }
                cmd.commit()
                cmd.waitUntilCompleted()
            }
            var bytes = [UInt8](repeating: 0, count: width * height * 4)
            target.getBytes(&bytes, bytesPerRow: width * 4, from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
            let cs = CGColorSpaceCreateDeviceRGB()
            let info = CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
            if let ctx = CGContext(data: &bytes, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4, space: cs, bitmapInfo: info.rawValue),
               let img = ctx.makeImage() {
                let rep = NSBitmapImageRep(cgImage: img)
                let name = String(format: "%02d %@.png", index, preset.name)
                try? rep.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent(name))
            }
            presetStart = manualTime!
        }
    }

    func previousPreset() {
        orderPos = (orderPos - 2 + order.count) % order.count
        switchTo(nextInOrder(), now: now)
    }

    /// Fixed-step clock for offline rendering (snapshots); nil = real time.
    var manualTime: Double?
    private var now: Double { manualTime ?? (CACurrentMediaTime() - start) }

    private func nextInOrder() -> Int {
        orderPos += 1
        if orderPos >= order.count {
            let last = presetIndex
            reshuffle()
            if order.first == last { order.swapAt(0, order.count - 1) }
        }
        return order[orderPos % order.count]
    }

    private func switchTo(_ index: Int, now: Double) {
        guard index != presetIndex else { return }
        previousIndex = presetIndex
        presetIndex = index
        blendStart = now
        presetStart = now
        presetBeats = 0
        onPresetChange?(VisPreset.all[index].name)
    }

    // MARK: MTKViewDelegate

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {}

    func draw(in view: MTKView) {
        guard let drawable = view.currentDrawable, let cmd = queue.makeCommandBuffer() else { return }
        render(cmd, target: drawable.texture, size: view.drawableSize)
        cmd.present(drawable)
        cmd.commit()
    }

    /// Renders one frame into `target` (any pixel format).
    func render(_ cmd: MTLCommandBuffer, target: MTLTexture, size: CGSize) {
        let t = now
        let dt = manualTime != nil ? Float(1.0 / 60) : Float(min(0.1, CACurrentMediaTime() - lastFrame))
        lastFrame = CACurrentMediaTime()
        analyzer.update(dt: dt, time: t, fetch: fetchSamples)
        fadeIn = min(1, fadeIn + dt * 0.8)

        // auto-advance: on a strong beat after 18 s, or regardless after 40 s
        if analyzer.beat { presetBeats += 1 }
        let age = t - presetStart
        if !locked, previousIndex == nil, (age > 18 && analyzer.beat && analyzer.bass > 1.7) || age > 40 {
            switchTo(nextInOrder(), now: t)
        }

        let params = currentParams(time: t)
        ensureTextures(for: size)
        guard feedback.count == 2, let blurA, let blurB else { return }
        let prev = feedback[current], cur = feedback[1 - current]
        let w = Float(cur.width), h = Float(cur.height), aspect = w / h

        // 1+2: warp the previous frame, then draw waves/shapes/borders on top
        let rp = MTLRenderPassDescriptor()
        rp.colorAttachments[0].texture = cur
        rp.colorAttachments[0].loadAction = .dontCare
        rp.colorAttachments[0].storeAction = .store
        if let enc = cmd.makeRenderCommandEncoder(descriptor: rp) {
            var wu = WarpUniforms(time: Float(t), decay: params.decay, zoom: params.zoom, zoomExp: params.zoomExp,
                                  rot: params.rot, warp: params.warp, warpSpeed: params.warpSpeed, warpScale: params.warpScale,
                                  cx: params.cx, cy: params.cy, dx: params.dx, dy: params.dy,
                                  sx: params.sx, sy: params.sy, zoomRad: params.zoomRad, rotRad: params.rotRad,
                                  aspect: aspect, wrap: params.wrap ? 1 : 0)
            enc.setRenderPipelineState(warpPipe)
            enc.setFragmentTexture(prev, index: 0)
            enc.setFragmentBytes(&wu, length: MemoryLayout<WarpUniforms>.stride, index: 0)
            enc.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)

            for additive in [false, true] {
                geometry.removeAll(keepingCapacity: true)
                buildGeometry(params, width: w, height: h, time: Float(t), additive: additive)
                guard !geometry.isEmpty else { continue }
                enc.setRenderPipelineState(additive ? geomAddPipe : geomAlphaPipe)
                let bytes = geometry.count * MemoryLayout<GVert>.stride
                if bytes <= 4096 {
                    enc.setVertexBytes(geometry, length: bytes, index: 0)
                } else if let buf = device.makeBuffer(bytes: geometry, length: bytes, options: .storageModeShared) {
                    enc.setVertexBuffer(buf, offset: 0, index: 0)
                }
                enc.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: geometry.count)
            }
            enc.endEncoding()
        }

        // 3: half-res blur for glow
        fullscreenPass(cmd, pipe: copyPipe, target: blurA, textures: [cur])
        var dirH = SIMD2<Float>(1.5 / Float(blurA.width), 0)
        fullscreenPass(cmd, pipe: blurPipe, target: blurB, textures: [blurA], bytes: &dirH, length: 8)
        var dirV = SIMD2<Float>(0, 1.5 / Float(blurA.height))
        fullscreenPass(cmd, pipe: blurPipe, target: blurA, textures: [blurB], bytes: &dirV, length: 8)

        // 4: composite to screen
        let fmt = target.pixelFormat
        if compPipe == nil || compFormat != fmt {
            compPipe = makeCompPipe(fmt)
            compFormat = fmt
        }
        if let compPipe {
            var cu = CompUniforms(time: Float(t), gamma: params.gamma, echoZoom: params.echoZoom, echoAlpha: params.echoAlpha,
                                  echoOrient: Float(params.echoOrient), hue: params.hue, glow: params.glow, kaleido: Float(params.kaleido),
                                  chroma: params.chroma, solarize: params.solarize ? 1 : 0, invert: params.invert ? 1 : 0,
                                  brighten: params.brighten ? 1 : 0, darken: params.darken ? 1 : 0,
                                  darkenCenter: params.darkenCenter, aspect: aspect, contrast: params.contrast,
                                  vignette: params.vignette, saturation: params.saturation, fade: fadeIn)
            fullscreenPass(cmd, pipe: compPipe, target: target, textures: [cur, blurA],
                           bytes: &cu, length: MemoryLayout<CompUniforms>.stride)
        }
        current = 1 - current
        framesRendered += 1
    }

    // MARK: params

    private func vars(for index: Int, time: Double, start: Double, beats: Int) -> FrameVars {
        FrameVars(time: Float(time), progress: Float(time - start),
                  bass: analyzer.bass, mid: analyzer.mid, treb: analyzer.treb,
                  bassAtt: analyzer.bassAtt, midAtt: analyzer.midAtt, trebAtt: analyzer.trebAtt,
                  volume: analyzer.volume, beat: analyzer.beat, beats: beats)
    }

    private func evaluate(_ index: Int, time: Double) -> VisParams {
        let preset = VisPreset.all[index]
        var p = preset.base
        preset.perFrame(&p, vars(for: index, time: time, start: presetStart, beats: presetBeats))
        return p
    }

    private func currentParams(time: Double) -> VisParams {
        let p = evaluate(presetIndex, time: time)
        guard let prevIndex = previousIndex else { return p }
        let k = (time - blendStart) / blendDuration
        if k >= 1 { previousIndex = nil; return p }
        let s = Float(k * k * (3 - 2 * k)) // smoothstep
        return VisParams.lerp(evaluate(prevIndex, time: time), p, s)
    }

    // MARK: textures / passes

    private func ensureTextures(for size: CGSize) {
        // cap the feedback resolution: the look is soft anyway and 5K full screen is costly
        let scale = min(1, 2048 / max(size.width, size.height))
        let w = max(64, Int(size.width * scale)), h = max(64, Int(size.height * scale))
        if let f = feedback.first, f.width == w, f.height == h { return }
        func tex(_ w: Int, _ h: Int) -> MTLTexture? {
            let d = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba16Float, width: w, height: h, mipmapped: false)
            d.usage = [.renderTarget, .shaderRead]
            d.storageMode = .private
            return device.makeTexture(descriptor: d)
        }
        let old = feedback.isEmpty ? nil : feedback[current]
        feedback = [tex(w, h), tex(w, h)].compactMap { $0 }
        blurA = tex(w / 2, h / 2)
        blurB = tex(w / 2, h / 2)
        current = 0
        // clear (or carry over the old image when resizing, e.g. entering full screen)
        if let cmd = queue.makeCommandBuffer() {
            for t in feedback {
                if let old {
                    fullscreenPass(cmd, pipe: copyPipe, target: t, textures: [old])
                } else {
                    let rp = MTLRenderPassDescriptor()
                    rp.colorAttachments[0].texture = t
                    rp.colorAttachments[0].loadAction = .clear
                    rp.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 1)
                    rp.colorAttachments[0].storeAction = .store
                    cmd.makeRenderCommandEncoder(descriptor: rp)?.endEncoding()
                }
            }
            cmd.commit()
        }
    }

    private func fullscreenPass(_ cmd: MTLCommandBuffer, pipe: MTLRenderPipelineState, target: MTLTexture,
                                textures: [MTLTexture], bytes: UnsafeRawPointer? = nil, length: Int = 0) {
        let rp = MTLRenderPassDescriptor()
        rp.colorAttachments[0].texture = target
        rp.colorAttachments[0].loadAction = .dontCare
        rp.colorAttachments[0].storeAction = .store
        guard let enc = cmd.makeRenderCommandEncoder(descriptor: rp) else { return }
        enc.setRenderPipelineState(pipe)
        for (i, t) in textures.enumerated() { enc.setFragmentTexture(t, index: i) }
        if let bytes { enc.setFragmentBytes(bytes, length: length, index: 0) }
        enc.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
        enc.endEncoding()
    }

    private func makeCompPipe(_ fmt: MTLPixelFormat) -> MTLRenderPipelineState? {
        guard let lib = try? device.makeLibrary(source: visualizerShaderSource, options: nil) else { return nil }
        let d = MTLRenderPipelineDescriptor()
        d.vertexFunction = lib.makeFunction(name: "fs_vertex")
        d.fragmentFunction = lib.makeFunction(name: "comp_fs")
        d.colorAttachments[0].pixelFormat = fmt
        return try? device.makeRenderPipelineState(descriptor: d)
    }

    // MARK: geometry (coordinates: 0…1, y up)

    private func ndc(_ p: SIMD2<Float>) -> SIMD4<Float> { SIMD4(p.x * 2 - 1, p.y * 2 - 1, 0, 1) }

    private func tri(_ a: SIMD2<Float>, _ b: SIMD2<Float>, _ c: SIMD2<Float>, _ ca: SIMD4<Float>, _ cb: SIMD4<Float>, _ cc: SIMD4<Float>) {
        geometry.append(GVert(pos: ndc(a), color: ca))
        geometry.append(GVert(pos: ndc(b), color: cb))
        geometry.append(GVert(pos: ndc(c), color: cc))
    }

    private func quad(_ a: SIMD2<Float>, _ b: SIMD2<Float>, _ c: SIMD2<Float>, _ d: SIMD2<Float>, _ col: SIMD4<Float>) {
        tri(a, b, c, col, col, col)
        tri(a, c, d, col, col, col)
    }

    /// Thick polyline; `thick` in pixels at 1080p.
    private func line(_ pts: [SIMD2<Float>], color: SIMD4<Float>, thick: Float, width: Float, height: Float) {
        guard pts.count > 1 else { return }
        let px = thick * height / 1080 * 0.5
        let sx = 1 / width, sy = 1 / height
        for i in 0..<(pts.count - 1) {
            let a = pts[i], b = pts[i + 1]
            var d = SIMD2((b.x - a.x) * width, (b.y - a.y) * height)
            let len = simd_length(d)
            if len < 1e-4 { continue }
            d /= len
            let n = SIMD2(-d.y * px * sx, d.x * px * sy)
            quad(a + n, b + n, b - n, a - n, color)
        }
    }

    private func buildGeometry(_ p: VisParams, width w: Float, height h: Float, time t: Float, additive: Bool) {
        let aspect = w / h
        // wave
        if p.waveAdditive == additive {
            var alpha = p.waveA * (0.55 + 0.45 * min(1.5, analyzer.volume * 2))
            if !analyzer.hasSignal { alpha *= 0.3 }
            let col = SIMD4<Float>(p.waveR, p.waveG, p.waveB, alpha)
            let L = analyzer.left, R = analyzer.right
            let sc = p.waveScale
            switch p.waveMode {
            case 0: // circle
                let n = 256
                var pts: [SIMD2<Float>] = []
                for i in 0...n {
                    let k = i % n
                    // blend the seam so the loop closes smoothly
                    let edge = Float(k) / Float(n)
                    let s = L[k * 2] * (1 - edge) + L[(n - k) % n * 2] * edge
                    let r = (0.22 + 0.25 * s) * sc
                    let a = Float(i) / Float(n) * 2 * .pi + t * 0.2
                    pts.append(SIMD2(p.waveX + r * cos(a) / aspect, p.waveY + r * sin(a)))
                }
                line(pts, color: col, thick: p.waveThick, width: w, height: h)
            case 1: // xy scope, rotated 45°
                let pts = (0..<512).map { i -> SIMD2<Float> in
                    let u = (L[i] - R[i]) * 0.7, v = (L[i] + R[i]) * 0.7
                    return SIMD2(p.waveX + u * 0.45 * sc / aspect, p.waveY + v * 0.45 * sc)
                }
                line(pts, color: col, thick: p.waveThick, width: w, height: h)
            case 2: // horizontal line
                let amp: Float = 0.25 * sc
                let pts = (0..<512).map { (i: Int) -> SIMD2<Float> in SIMD2<Float>(Float(i) / 511, p.waveY + L[i] * amp) }
                line(pts, color: col, thick: p.waveThick, width: w, height: h)
            case 3: // mirrored spectrum bars, log-spaced
                let bands = 64
                for b in 0..<bands {
                    let lo = Int(pow(2, Float(b) / Float(bands) * 8.5)), hi = max(lo + 1, Int(pow(2, Float(b + 1) / Float(bands) * 8.5)))
                    var m: Float = 0
                    for i in lo..<min(hi, 512) { m = max(m, analyzer.spectrum[i]) }
                    let x0 = Float(b) / Float(bands), x1 = x0 + 0.8 / Float(bands)
                    let hgt = m * 0.35 * sc
                    for side: Float in [-1, 1] where side > 0 || p.waveY > 0.3 {
                        let y0 = p.waveY, y1 = p.waveY + hgt * side
                        quad(SIMD2(x0, y0), SIMD2(x1, y0), SIMD2(x1, y1), SIMD2(x0, y1), col)
                    }
                }
            case 4: // two lines, L above, R below
                let amp: Float = 0.18 * sc
                let yTop: Float = p.waveY + 0.15, yBottom: Float = p.waveY - 0.15
                let a = (0..<512).map { (i: Int) -> SIMD2<Float> in SIMD2<Float>(Float(i) / 511, yTop + L[i] * amp) }
                let b = (0..<512).map { (i: Int) -> SIMD2<Float> in SIMD2<Float>(Float(i) / 511, yBottom + R[i] * amp) }
                line(a, color: col, thick: p.waveThick, width: w, height: h)
                line(b, color: col, thick: p.waveThick, width: w, height: h)
            default: // dot spiral
                let ds = 2.5 * p.waveThick / 1080
                for i in stride(from: 0, to: 512, by: 2) {
                    let f = Float(i) / 512
                    let a = f * 6 * .pi + t * 0.5
                    let r = (0.05 + 0.4 * f + 0.06 * L[i]) * sc
                    let c = SIMD2(p.waveX + r * cos(a) / aspect, p.waveY + r * sin(a))
                    quad(c + SIMD2(-ds / aspect, -ds), c + SIMD2(ds / aspect, -ds), c + SIMD2(ds / aspect, ds), c + SIMD2(-ds / aspect, ds), col)
                }
            }
        }
        // shapes
        for s in p.shapes where s.enabled && s.additive == additive {
            let n = max(3, Int(s.sides.rounded()))
            let c = SIMD2(s.x, s.y)
            let ci = SIMD4(s.r, s.g, s.b, s.a), co = SIMD4(s.r2, s.g2, s.b2, s.a2)
            var ring: [SIMD2<Float>] = []
            for i in 0...n {
                let a = s.ang + Float(i) / Float(n) * 2 * .pi + .pi / Float(n)
                ring.append(c + SIMD2(s.rad * cos(a) / aspect, s.rad * sin(a)))
            }
            for i in 0..<n { tri(c, ring[i], ring[i + 1], ci, co, co) }
            if s.borderA > 0.001 {
                line(ring, color: SIMD4(min(1, s.r + 0.3), min(1, s.g + 0.3), min(1, s.b + 0.3), s.borderA), thick: 2, width: w, height: h)
            }
        }
        // borders (alpha-blended pass only)
        if !additive {
            func frame(_ inset: Float, _ size: Float, _ col: SIMD4<Float>) {
                guard size > 0.0001, col.w > 0.001 else { return }
                let a = inset, b = inset + size
                quad(SIMD2(a, a), SIMD2(1 - a, a), SIMD2(1 - a, b), SIMD2(a, b), col)
                quad(SIMD2(a, 1 - b), SIMD2(1 - a, 1 - b), SIMD2(1 - a, 1 - a), SIMD2(a, 1 - a), col)
                quad(SIMD2(a, b), SIMD2(b, b), SIMD2(b, 1 - b), SIMD2(a, 1 - b), col)
                quad(SIMD2(1 - b, b), SIMD2(1 - a, b), SIMD2(1 - a, 1 - b), SIMD2(1 - b, 1 - b), col)
            }
            frame(0, p.obSize, SIMD4(p.obR, p.obG, p.obB, p.obA))
            frame(p.obSize, p.ibSize, SIMD4(p.ibR, p.ibG, p.ibB, p.ibA))
        }
    }
}
