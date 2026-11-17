import AppKit
import MetalKit
import SwiftUI

final class VisualizerMTKView: MTKView {
    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { window?.toggleFullScreen(nil) } else { super.mouseDown(with: event) }
    }
}

struct VisualizerView: NSViewRepresentable {
    let renderer: VisualizerRenderer

    func makeNSView(context: Context) -> VisualizerMTKView {
        let v = VisualizerMTKView(frame: .zero, device: renderer.device)
        v.colorPixelFormat = .bgra8Unorm
        v.framebufferOnly = true
        v.preferredFramesPerSecond = 60
        v.clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 1)
        v.delegate = renderer
        return v
    }

    func updateNSView(_ nsView: VisualizerMTKView, context: Context) {}
}

/// Visualiser with MilkDrop-style overlays: the track title fades in on track change
/// and the preset name on preset change.
struct VisualizerStage: View {
    @EnvironmentObject var model: PlayerModel
    let renderer: VisualizerRenderer
    @State private var showTitle = false
    @State private var showPreset = false
    @State private var titleHide: DispatchWorkItem?
    @State private var presetHide: DispatchWorkItem?

    var body: some View {
        ZStack {
            VisualizerView(renderer: renderer)
            VStack {
                HStack {
                    Spacer()
                    if showPreset || model.visLocked {
                        HStack(spacing: 6) {
                            if model.visLocked { Image(systemName: "lock.fill") }
                            Text(model.visPresetName)
                        }
                        .font(.callout.weight(.medium))
                        .padding(.horizontal, 10).padding(.vertical, 5)
                        .background(.black.opacity(0.4), in: Capsule())
                        .transition(.opacity)
                    }
                }
                Spacer()
                HStack {
                    if showTitle {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(model.title).font(.system(size: 30, weight: .semibold))
                            if !model.artist.isEmpty || !model.album.isEmpty {
                                Text([model.artist, model.album].filter { !$0.isEmpty }.joined(separator: " — "))
                                    .font(.title3).opacity(0.8)
                            }
                        }
                        .shadow(color: .black.opacity(0.8), radius: 8)
                        .transition(.opacity)
                    }
                    Spacer()
                }
            }
            .foregroundStyle(.white)
            .padding(model.isFullScreen ? 48 : 20)
            .padding(.bottom, model.isFullScreen ? 60 : 0)
            .allowsHitTesting(false)
        }
        .onAppear {
            model.setVisualizerActive(true)
            flashTitle()
        }
        .onDisappear { model.setVisualizerActive(false) }
        .onChange(of: model.title) { flashTitle() }
        .onChange(of: model.visPresetChanges) { flashPreset() }
    }

    private func flashTitle() {
        guard !model.title.isEmpty else { return }
        titleHide?.cancel()
        withAnimation(.easeIn(duration: 0.6)) { showTitle = true }
        let w = DispatchWorkItem { withAnimation(.easeOut(duration: 1.5)) { showTitle = false } }
        titleHide = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 5, execute: w)
    }

    private func flashPreset() {
        presetHide?.cancel()
        withAnimation(.easeIn(duration: 0.3)) { showPreset = true }
        let w = DispatchWorkItem { withAnimation(.easeOut(duration: 1)) { showPreset = false } }
        presetHide = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.5, execute: w)
    }
}

/// Hands the hosting NSWindow to the model (full-screen state, key handling).
struct WindowAccessor: NSViewRepresentable {
    let onWindow: (NSWindow) -> Void
    func makeNSView(context: Context) -> NSView {
        let v = NSView()
        DispatchQueue.main.async { if let w = v.window { onWindow(w) } }
        return v
    }
    func updateNSView(_ nsView: NSView, context: Context) {}
}
