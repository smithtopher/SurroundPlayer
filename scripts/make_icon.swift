// Turns icon.png (rounded square on an opaque background) into a macOS-grid icon:
// detects the square's edges, clips the corners transparent, places it at 824/1024.
import AppKit

let args = CommandLine.arguments
guard args.count == 3, let img = NSImage(contentsOfFile: args[1]),
      let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
    fatalError("usage: make_icon <in.png> <out-1024.png>")
}
let w = cg.width, h = cg.height
let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                    space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
ctx.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
let px = ctx.data!.bindMemory(to: UInt8.self, capacity: w * h * 4)
func lum(_ x: Int, _ y: Int) -> Int { let i = (y * w + x) * 4; return Int(px[i]) + Int(px[i + 1]) + Int(px[i + 2]) }
let bg = lum(2, 2), thr = bg + 24
// scan across the middle in each direction for the first non-background pixel
let mid = h / 2, midx = w / 2
let left = (0..<w).first { lum($0, mid) > thr } ?? 0
let right = (0..<w).reversed().first { lum($0, mid) > thr } ?? w - 1
let top = (0..<h).first { lum(midx, $0) > thr } ?? 0      // row 0 = top in this bitmap
let bottom = (0..<h).reversed().first { lum(midx, $0) > thr } ?? h - 1
print("detected body x \(left)...\(right), y \(top)...\(bottom) (background \(bg / 3))")

let crop = cg.cropping(to: CGRect(x: left, y: top, width: right - left + 1, height: bottom - top + 1))!
let S = 1024, body = CGRect(x: 100, y: 100, width: 824, height: 824)
let out = CGContext(data: nil, width: S, height: S, bitsPerComponent: 8, bytesPerRow: S * 4,
                    space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
out.interpolationQuality = .high
// subtle drop shadow like system icons
out.saveGState()
out.setShadow(offset: CGSize(width: 0, height: -10), blur: 28, color: NSColor.black.withAlphaComponent(0.35).cgColor)
out.addPath(CGPath(roundedRect: body, cornerWidth: 185, cornerHeight: 185, transform: nil))
out.setFillColor(NSColor.black.cgColor)
out.fillPath()
out.restoreGState()
out.addPath(CGPath(roundedRect: body.insetBy(dx: 0.5, dy: 0.5), cornerWidth: 184, cornerHeight: 184, transform: nil))
out.clip()
out.draw(crop, in: body)
let rep = NSBitmapImageRep(cgImage: out.makeImage()!)
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: args[2]))
