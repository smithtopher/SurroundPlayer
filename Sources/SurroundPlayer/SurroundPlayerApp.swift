import AppKit
import SwiftUI

@main
struct SurroundPlayerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @StateObject private var model = PlayerModel.shared

    var body: some Scene {
        Window("Surround Player", id: "main") {
            ContentView()
                .environmentObject(model)
                .frame(minWidth: 820, minHeight: 520)
        }
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("Open…") { model.showOpenPanel() }
                    .keyboardShortcut("o")
            }
            CommandMenu("Playback") {
                Button(model.isPlaying ? "Pause" : "Play") { model.togglePlay() }
                    .keyboardShortcut(.space, modifiers: [])
                Button("Next") { model.next() }
                    .keyboardShortcut(.rightArrow, modifiers: .command)
                Button("Previous") { model.previous() }
                    .keyboardShortcut(.leftArrow, modifiers: .command)
                Divider()
                Button("Forward 10 s") { model.skip(10) }
                    .keyboardShortcut(.rightArrow, modifiers: [])
                Button("Back 10 s") { model.skip(-10) }
                    .keyboardShortcut(.leftArrow, modifiers: [])
                Button("Forward 1 min") { model.skip(60) }
                    .keyboardShortcut(.upArrow, modifiers: [])
                Button("Back 1 min") { model.skip(-60) }
                    .keyboardShortcut(.downArrow, modifiers: [])
                Divider()
                Button("Stop") { model.stop() }
                    .keyboardShortcut(".", modifiers: .command)
            }
        }
        Settings {
            SettingsView()
                .environmentObject(model)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var signalSources: [DispatchSourceSignal] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Ctrl-C / kill from a terminal: restore the audio device before exiting.
        for sig in [SIGINT, SIGTERM] {
            signal(sig, SIG_IGN)
            let src = DispatchSource.makeSignalSource(signal: sig, queue: .main)
            src.setEventHandler {
                MainActor.assumeIsolated { PlayerModel.shared.shutdown() }
                exit(0)
            }
            src.resume()
            signalSources.append(src)
        }
        NSApp.setActivationPolicy(.regular)
        NSApp.activate(ignoringOtherApps: true)
        // `SurroundPlayer file1 file2 …` from a terminal
        let files = CommandLine.arguments.dropFirst().filter { !$0.hasPrefix("-") && FileManager.default.fileExists(atPath: $0) }
        if !files.isEmpty {
            MainActor.assumeIsolated { PlayerModel.shared.add(urls: files.map { URL(fileURLWithPath: $0) }, playFirst: true) }
        }
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        Task { @MainActor in PlayerModel.shared.add(urls: urls, playFirst: true) }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    func applicationWillTerminate(_ notification: Notification) {
        // Releases hog mode and restores the device's original format.
        MainActor.assumeIsolated { PlayerModel.shared.shutdown() }
    }
}
