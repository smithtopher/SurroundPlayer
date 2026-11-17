import AVFoundation
import AppKit
import CoreMedia
import SwiftUI

/// Receives decoded frames from the engine's presenter thread (already timed
/// against the audio clock) and shows them immediately.
final class VideoSink: @unchecked Sendable {
    let layer = AVSampleBufferDisplayLayer()
    private var format: CMVideoFormatDescription?

    init() {
        layer.videoGravity = .resizeAspect
        layer.backgroundColor = NSColor.black.cgColor
    }

    func display(_ pb: CVPixelBuffer) {
        if format == nil || !CMVideoFormatDescriptionMatchesImageBuffer(format!, imageBuffer: pb) {
            format = nil
            CMVideoFormatDescriptionCreateForImageBuffer(allocator: nil, imageBuffer: pb, formatDescriptionOut: &format)
        }
        guard let format else { return }
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: .zero, decodeTimeStamp: .invalid)
        var sb: CMSampleBuffer?
        guard CMSampleBufferCreateReadyWithImageBuffer(allocator: nil, imageBuffer: pb, formatDescription: format,
                                                       sampleTiming: &timing, sampleBufferOut: &sb) == noErr,
              let sb else { return }
        if let atts = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: true), CFArrayGetCount(atts) > 0 {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(atts, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(dict, Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                 Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        }
        let renderer = layer.sampleBufferRenderer
        if renderer.status == .failed { renderer.flush() }
        renderer.enqueue(sb)
    }

    func clear() {
        layer.sampleBufferRenderer.flush(removingDisplayedImage: true, completionHandler: nil)
        format = nil
    }
}

final class VideoHostView: NSView {
    let sink: VideoSink
    init(sink: VideoSink) {
        self.sink = sink
        super.init(frame: .zero)
        wantsLayer = true
        layer = CALayer()
        layer?.backgroundColor = NSColor.black.cgColor
        sink.layer.frame = bounds
        sink.layer.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        layer?.addSublayer(sink.layer)
    }
    required init?(coder: NSCoder) { fatalError() }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        sink.layer.frame = bounds
        CATransaction.commit()
    }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { window?.toggleFullScreen(nil) } else { super.mouseDown(with: event) }
    }
}

struct VideoView: NSViewRepresentable {
    let sink: VideoSink
    func makeNSView(context: Context) -> VideoHostView { VideoHostView(sink: sink) }
    func updateNSView(_ nsView: VideoHostView, context: Context) {}
}
