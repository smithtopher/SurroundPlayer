// swift-tools-version:5.9
import Foundation
import PackageDescription

// FFmpeg location, in order of preference:
//   1. $FFMPEG_PREFIX (scripts/build_app.sh sets this per architecture)
//   2. vendor/ffmpeg/<host arch>, built static by scripts/build_ffmpeg.sh (self-contained app)
//   3. Homebrew (`brew install ffmpeg`; app then only runs where Homebrew FFmpeg is installed)
let packageRoot = URL(fileURLWithPath: #filePath).deletingLastPathComponent().path
#if arch(arm64)
let hostArch = "arm64"
#else
let hostArch = "x86_64"
#endif
let vendored = "\(packageRoot)/vendor/ffmpeg/\(hostArch)"
let ffmpegPrefix = ProcessInfo.processInfo.environment["FFMPEG_PREFIX"]
    ?? (FileManager.default.fileExists(atPath: "\(vendored)/lib/libavformat.a") ? vendored : "/opt/homebrew/opt/ffmpeg")

let package = Package(
    name: "SurroundPlayer",
    platforms: [.macOS(.v14)],
    targets: [
        .target(
            name: "PlayerCore",
            path: "Sources/PlayerCore",
            cSettings: [
                .unsafeFlags(["-I\(ffmpegPrefix)/include", "-O2", "-Wall", "-Wno-deprecated-declarations"])
            ],
            linkerSettings: [
                .unsafeFlags(["-L\(ffmpegPrefix)/lib"]),
                .linkedLibrary("avformat"), .linkedLibrary("avcodec"), .linkedLibrary("avutil"),
                .linkedLibrary("swresample"), .linkedLibrary("swscale"),
                // needed by a static FFmpeg
                .linkedLibrary("z"), .linkedLibrary("bz2"),
                .linkedFramework("CoreAudio"), .linkedFramework("CoreVideo"),
                .linkedFramework("CoreFoundation"), .linkedFramework("VideoToolbox"),
                .linkedFramework("CoreMedia"), .linkedFramework("CoreServices"),
            ]
        ),
        .executableTarget(
            name: "SurroundPlayer",
            dependencies: ["PlayerCore"],
            path: "Sources/SurroundPlayer"
        ),
        // Command-line harness: device listing, offline IEC 61937 dumps, headless playback.
        .executableTarget(
            name: "pctest",
            dependencies: ["PlayerCore"],
            path: "Sources/pctest"
        ),
    ]
)
