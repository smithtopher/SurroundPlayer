import AppKit
import SwiftUI

@main
struct SurroundPlayerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @ObservedObject private var model = PlayerModel.shared

    // The main window is created by AppDelegate (see makeMainWindow): a SwiftUI
    // `Window` scene isn't created when the app is launched to open files.
    var body: some Scene {
        Settings {
            SettingsView()
                .environmentObject(model)
        }
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("Open…") { model.showOpenPanel() }
                    .keyboardShortcut("o")
            }
            CommandMenu("Playback") {
                // Single keys (Space, arrows, F, V, [, ], L) are handled by the player
                // window itself so they never interfere with typing in the search field.
                Button(model.isPlaying ? "Pause  (Space)" : "Play  (Space)") { model.togglePlay() }
                Button("Next  (⌘→)") { model.next() }
                Button("Previous  (⌘←)") { model.previous() }
                Divider()
                Button("Forward 10 s  (→)") { model.skip(10) }
                Button("Back 10 s  (←)") { model.skip(-10) }
                Button("Forward 1 min  (↑)") { model.skip(60) }
                Button("Back 1 min  (↓)") { model.skip(-60) }
                Divider()
                Button("Stop") { model.stop() }
                    .keyboardShortcut(".", modifiers: .command)
                Divider()
                Button(model.isFullScreen ? "Exit Full Screen Player  (F)" : "Full Screen Player  (F)") { model.toggleFullScreen() }
            }
            CommandMenu("Visualiser") {
                Toggle("Show Visualiser for Music  (V)", isOn: $model.visualizerEnabled)
                Divider()
                Button("Next Preset  (])") { model.visualizer?.nextPreset() }
                    .disabled(!model.showsVisualizer)
                Button("Previous Preset  ([)") { model.visualizer?.previousPreset() }
                    .disabled(!model.showsVisualizer)
                Toggle("Lock Preset  (L)", isOn: $model.visLocked)
            }
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var signalSources: [DispatchSourceSignal] = []
    private var mainWindow: NSWindow?

    // Before any open-file events are delivered.
    func applicationWillFinishLaunching(_ notification: Notification) {
        if CommandLine.arguments.contains("--vis-snapshot") { return }
        MainActor.assumeIsolated { makeMainWindow() }
    }

    @MainActor private func makeMainWindow() {
        let root = ContentView()
            .environmentObject(PlayerModel.shared)
            .frame(minWidth: 820, minHeight: 520)
        let hosting = NSHostingView(rootView: root)
        hosting.sceneBridgingOptions = [.toolbars, .title]
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 680),
                         styleMask: [.titled, .closable, .miniaturizable, .resizable],
                         backing: .buffered, defer: false)
        w.title = "Surround Player"
        w.contentView = hosting
        w.toolbarStyle = .unified
        w.isReleasedWhenClosed = false
        w.collectionBehavior.insert(.fullScreenPrimary)
        w.tabbingMode = .disallowed
        if !w.setFrameUsingName("main") { w.center() }
        w.setFrameAutosaveName("main")
        w.makeKeyAndOrderFront(nil)
        mainWindow = w
        PlayerModel.shared.attach(window: w)
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { mainWindow?.makeKeyAndOrderFront(nil) }
        return true
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // development: `SurroundPlayer --vis-snapshot <dir>` renders every preset to PNG and quits
        if let i = CommandLine.arguments.firstIndex(of: "--vis-snapshot"), i + 1 < CommandLine.arguments.count {
            let dir = URL(fileURLWithPath: CommandLine.arguments[i + 1])
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            VisualizerRenderer()?.writeSnapshots(to: dir)
            exit(0)
        }
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
        if ProcessInfo.processInfo.environment["SURROUND_TEST_FULLSCREEN"] != nil {   // testing aid
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { MainActor.assumeIsolated { PlayerModel.shared.toggleFullScreen() } }
        }
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
