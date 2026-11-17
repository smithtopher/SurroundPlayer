// swift-tools-version:5.9
import PackageDescription

// FFmpeg comes from Homebrew (`brew install ffmpeg`).
let ffmpegPrefix = "/opt/homebrew/opt/ffmpeg"

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
                .linkedFramework("CoreAudio"), .linkedFramework("CoreVideo"),
                .linkedFramework("CoreFoundation"), .linkedFramework("VideoToolbox"),
                .linkedFramework("CoreMedia"),
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
