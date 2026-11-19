import AppKit
import CoreAudio
import CoreVideo
import PlayerCore
import SwiftUI
import UniformTypeIdentifiers

struct Track: Identifiable, Hashable {
    let id = UUID()
    let url: URL
    /// Cue sheet tracks: a span of `url` from `start` to `end` (nil = end of file).
    let cue: CueTrack?
    var start: Double { cue?.start ?? 0 }
    var end: Double? { cue?.end }
    var name: String {
        if let cue { return String(format: "%02d. %@", cue.number, cue.title) }
        return url.deletingPathExtension().lastPathComponent
    }
    /// Parent folder, e.g. the album folder in an Artist/Album/Track library.
    var folder: String { url.deletingLastPathComponent().lastPathComponent }
    /// Lowercased, diacritic-folded text the sidebar search matches against.
    let searchText: String

    init(url: URL, cue: CueTrack? = nil) {
        self.url = url
        self.cue = cue
        var parts = url.deletingPathExtension().pathComponents.suffix(4).joined(separator: " ")
        if let cue { parts += " \(cue.title) \(cue.performer) \(cue.album)" }
        searchText = parts.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
    }

    func matches(_ terms: [String]) -> Bool { terms.allSatisfy { searchText.contains($0) } }

    /// `next` continues this item in the same file (consecutive cue tracks): gapless, no reopen.
    func continues(into next: Track) -> Bool {
        guard let end, next.cue != nil, next.url.standardizedFileURL.path == url.standardizedFileURL.path else { return false }
        return abs(next.start - end) < 0.001
    }
}

struct AudioDevice: Identifiable, Hashable {
    let uid: String
    let name: String
    let channels: Int
    let isHDMI: Bool
    let canBitstream: Bool
    let isDefault: Bool
    var id: String { uid }
}

func cString<T>(_ tuple: T) -> String {
    withUnsafeBytes(of: tuple) { raw in String(cString: raw.bindMemory(to: CChar.self).baseAddress!) }
}

/// Bridges C callbacks (called on engine threads) back to Swift.
private final class EngineBridge {
    let sink: VideoSink
    weak var model: PlayerModel?
    var generation = 0
    init(sink: VideoSink) { self.sink = sink }
}

@MainActor
final class PlayerModel: ObservableObject {
    static let shared = PlayerModel()

    nonisolated static let mediaExtensions: Set<String> = [
        "mp3", "m4a", "aac", "flac", "wav", "aif", "aiff", "alac", "ogg", "opus", "wma", "ac3", "eac3", "dts", "mka", "ape", "wv", "dsf",
        "mp4", "m4v", "mkv", "mov", "avi", "webm", "ts", "m2ts", "mts", "wmv", "mpg", "mpeg", "vob", "flv",
    ]

    // playlist / state
    @Published var playlist: [Track] = []
    @Published var currentIndex: Int?
    @Published var selection: Set<Track.ID> = []
    @Published var isPlaying = false
    @Published var position: Double = 0
    @Published var duration: Double = 0
    @Published var isScrubbing = false

    // current item info
    @Published var title = ""
    @Published var artist = ""
    @Published var album = ""
    @Published var sourceAudio = ""
    @Published var outputDescription = ""
    @Published var videoDescription = ""
    @Published var outputMode: Int32 = -1
    @Published var hasVideo = false
    @Published var artwork: NSImage?
    @Published var audioTracks: [String] = []
    @Published var currentAudioTrack = -1
    @Published var errorMessage: String?

    @Published var devices: [AudioDevice] = []

    // settings (persisted)
    private static let defaults = UserDefaults.standard
    @Published var deviceUID: String = defaults.string(forKey: "deviceUID") ?? "" {
        didSet {
            Self.defaults.set(deviceUID, forKey: "deviceUID")
            if let d = devices.first(where: { $0.uid == deviceUID }) { Self.defaults.set(d.name, forKey: "deviceName") }
            if oldValue != deviceUID { settingsChanged() }
        }
    }
    @Published var ac3Enabled: Bool = defaults.object(forKey: "ac3Enabled") as? Bool ?? true {
        didSet { Self.defaults.set(ac3Enabled, forKey: "ac3Enabled"); settingsChanged() }
    }
    @Published var ac3Bitrate: Int = defaults.object(forKey: "ac3Bitrate") as? Int ?? 640 {
        didSet { Self.defaults.set(ac3Bitrate, forKey: "ac3Bitrate"); settingsChanged() }
    }
    /// 0 = Dolby Digital (AC3), 1 = DTS
    @Published var bitstreamCodec: Int = defaults.object(forKey: "bitstreamCodec") as? Int ?? 0 {
        didSet { Self.defaults.set(bitstreamCodec, forKey: "bitstreamCodec"); settingsChanged() }
    }
    @Published var dtsBitrate: Int = defaults.object(forKey: "dtsBitrate") as? Int ?? 1509 {
        didSet { Self.defaults.set(dtsBitrate, forKey: "dtsBitrate"); settingsChanged() }
    }
    @Published var bitstreamCodecName = ""
    @Published var ac3Passthrough: Bool = defaults.object(forKey: "ac3Passthrough") as? Bool ?? true {
        didSet { Self.defaults.set(ac3Passthrough, forKey: "ac3Passthrough"); settingsChanged() }
    }
    @Published var stereoAsAC3: Bool = defaults.object(forKey: "stereoAsAC3") as? Bool ?? false {
        didSet { Self.defaults.set(stereoAsAC3, forKey: "stereoAsAC3"); settingsChanged() }
    }
    /// Don't transcode: multichannel sources go out as decoded PCM in their own layout and
    /// rate (exclusive, bit-exact; HDMI into an AV receiver). Native AC3/DTS still pass through.
    @Published var multichannelPcm: Bool = defaults.object(forKey: "multichannelPcm") as? Bool ?? false {
        didSet { Self.defaults.set(multichannelPcm, forKey: "multichannelPcm"); settingsChanged() }
    }

    /// Settings' "Surround output": 0 Dolby Digital, 1 DTS, 2 multichannel PCM, 3 off (stereo PCM).
    /// Same mapping onto the stored settings as the Windows app.
    var surroundOutput: Int {
        get { multichannelPcm ? 2 : !ac3Enabled ? 3 : bitstreamCodec == 1 ? 1 : 0 }
        set {
            batchingSettings = true
            switch newValue {
            case 0, 1: multichannelPcm = false; ac3Enabled = true; bitstreamCodec = newValue
            case 2: multichannelPcm = true
            default: multichannelPcm = false; ac3Enabled = false
            }
            batchingSettings = false
            settingsChanged()       // one reopen for the lot
        }
    }
    private var batchingSettings = false
    @Published var avOffsetMs: Double = defaults.double(forKey: "avOffsetMs") {
        didSet { Self.defaults.set(avOffsetMs, forKey: "avOffsetMs"); pc_player_set_av_offset(player, avOffsetMs / 1000) }
    }

    /// Off by default. When on it takes over the stage for every file, including ones
    /// with a video track (the video keeps decoding in sync but isn't drawn).
    @Published var visualizerEnabled: Bool = defaults.object(forKey: "visualizerEnabled") as? Bool ?? false {
        didSet {
            Self.defaults.set(visualizerEnabled, forKey: "visualizerEnabled")
            syncVideoSink()
            updateActivity()
        }
    }
    @Published var visPresetName = ""
    @Published var visPresetChanges = 0
    @Published var visLocked = false { didSet { visualizer?.locked = visLocked } }
    @Published var isFullScreen = false
    private(set) weak var window: NSWindow?

    /// Created lazily (needs Metal); owned here so the image/preset survive view changes.
    lazy var visualizer: VisualizerRenderer? = {
        guard let r = VisualizerRenderer() else { return nil }
        let pl = player
        r.fetchSamples = { buf, n in Int(pc_player_vis_samples(pl, buf, Int32(n))) }
        r.onPresetChange = { [weak self] name in
            self?.visPresetName = name
            self?.visPresetChanges += 1
        }
        visPresetName = r.presetName
        return r
    }()

    var showsVisualizer: Bool { visualizerEnabled && currentIndex != nil && visualizer != nil }

    /// Stop pushing video frames to the (hidden) video layer while the visualiser is up.
    private func syncVideoSink() {
        let videoVisible = !(visualizerEnabled && visualizer != nil)
        if videoVisible != sink.isEnabled {
            sink.isEnabled = videoVisible
            if videoVisible { sink.clear() }   // don't flash the frame from before the switch
        }
    }

    func setVisualizerActive(_ active: Bool) { pc_player_set_vis_enabled(player, active ? 1 : 0) }

    // MARK: window / full screen / keys

    private var keyMonitor: Any?
    private var hadFullSizeContent = false   // windowed style, restored on leaving full screen

    func attach(window: NSWindow) {
        guard self.window !== window else { return }
        self.window = window
        isFullScreen = window.styleMask.contains(.fullScreen)
        // full screen adds .fullSizeContentView; put back exactly what the window had
        hadFullSizeContent = window.styleMask.contains(.fullSizeContentView)
        let nc = NotificationCenter.default
        // full screen = just the picture: no playlist; the picture runs under the toolbar,
        // which fades out with the controls (ContentView.pokeControls)
        nc.addObserver(forName: NSWindow.willEnterFullScreenNotification, object: window, queue: .main) { [weak window] _ in
            MainActor.assumeIsolated {
                PlayerModel.shared.isFullScreen = true
                window?.styleMask.insert(.fullSizeContentView)
            }
        }
        nc.addObserver(forName: NSWindow.willExitFullScreenNotification, object: window, queue: .main) { [weak window] _ in
            MainActor.assumeIsolated {
                PlayerModel.shared.setTopBarVisible(true)
                PlayerModel.shared.isFullScreen = false
                if !PlayerModel.shared.hadFullSizeContent { window?.styleMask.remove(.fullSizeContentView) }
            }
        }
        if keyMonitor == nil {
            keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
                MainActor.assumeIsolated { PlayerModel.shared.handleKey(event) } ? nil : event
            }
        }
    }

    func toggleFullScreen() { (window ?? NSApp.keyWindow)?.toggleFullScreen(nil) }

    /// In full screen AppKit puts the title bar + toolbar in a window of its own
    /// (NSToolbarFullScreenWindow) that is otherwise always shown. Fade that window.
    private var fullScreenTopBar: NSWindow? {
        guard let w = window, w.styleMask.contains(.fullScreen),
              let bar = w.standardWindowButton(.closeButton)?.window, bar !== w else { return nil }
        return bar
    }

    func setTopBarVisible(_ visible: Bool) {
        guard let bar = fullScreenTopBar else { return }
        bar.ignoresMouseEvents = !visible   // hidden: clicks and hovers reach the picture underneath
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = visible ? 0.2 : 0.5
            bar.animator().alphaValue = visible ? 1 : 0
        }
    }

    var mouseIsOverTopBar: Bool {
        guard let bar = fullScreenTopBar, bar.alphaValue > 0 else { return false }
        return bar.frame.contains(NSEvent.mouseLocation)
    }

    /// Single-key controls for the player window. Skipped while typing in a text field,
    /// and for key combos with ⌘/⌃/⌥ (those belong to the menus).
    private func handleKey(_ event: NSEvent) -> Bool {
        guard event.window === window, !(window?.firstResponder is NSText) else { return false }
        let mods = event.modifierFlags.intersection([.command, .control, .option, .shift])
        if mods == .command && event.keyCode == 123 { previous(); return true }   // ⌘←
        if mods == .command && event.keyCode == 124 { next(); return true }       // ⌘→
        if !mods.intersection([.command, .control, .option]).isEmpty { return false }
        switch event.keyCode {
        case 49: togglePlay(); return true                          // space
        case 123: skip(-10); return true                            // ←
        case 124: skip(10); return true                             // →
        case 126: skip(60); return true                             // ↑
        case 125: skip(-60); return true                            // ↓
        case 53:                                                    // esc
            if isFullScreen { toggleFullScreen(); return true }
            return false
        default: break
        }
        switch event.charactersIgnoringModifiers?.lowercased() {
        case "f": toggleFullScreen()
        case "v": visualizerEnabled.toggle()
        case "]", "n": visualizer?.nextPreset()
        case "[", "p": visualizer?.previousPreset()
        case "l": visLocked.toggle()
        case "a": cycleAudioTrack()
        default: return false
        }
        return true
    }

    let sink = VideoSink()
    private let player: OpaquePointer
    private let bridge: EngineBridge
    private var timer: Timer?
    private var activity: NSObjectProtocol?
    private var isOpen = false
    private var deviceListener: AudioObjectPropertyListenerBlock?

    private init() {
        player = pc_player_create()
        bridge = EngineBridge(sink: sink)
        bridge.model = self
        let ctx = Unmanaged.passUnretained(bridge).toOpaque()
        pc_player_set_callbacks(player, { ctx, frame, _ in
            guard let ctx, let frame else { return }
            // macOS video back-end: native frames are CVPixelBuffers (borrowed for this call)
            let pb = Unmanaged<CVPixelBuffer>.fromOpaque(frame).takeUnretainedValue()
            Unmanaged<EngineBridge>.fromOpaque(ctx).takeUnretainedValue().sink.display(pb)
        }, { ctx, event, _ in
            guard let ctx else { return }
            let bridge = Unmanaged<EngineBridge>.fromOpaque(ctx).takeUnretainedValue()
            let gen = bridge.generation
            if event == Int32(PC_EVENT_EOF.rawValue) {
                DispatchQueue.main.async { bridge.model?.trackFinished(generation: gen) }
            }
        }, ctx)
        refreshDevices()
        watchDevices()
        syncVideoSink()
        timer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
    }

    // MARK: devices

    func refreshDevices() {
        var raw = [PCDevice](repeating: PCDevice(), count: 64)
        let n = Int(pc_list_output_devices(&raw, 64))
        devices = raw.prefix(n).map {
            AudioDevice(uid: cString($0.uid), name: cString($0.name), channels: Int($0.output_channels),
                        isHDMI: $0.is_hdmi != 0, canBitstream: $0.is_digital != 0, isDefault: $0.is_default != 0)
        }
        handleDeviceChange()
    }

    private var openDeviceUID: String?
    private var openDeviceName: String?

    /// The device we are playing on disappeared (TV off, HDMI re-enumerated after an input/mode change).
    private func handleDeviceChange() {
        guard isOpen, let uid = openDeviceUID, !uid.isEmpty, !devices.contains(where: { $0.uid == uid }) else { return }
        guard let i = currentIndex else { return }
        let pos = position
        let wasPlaying = isPlaying
        if let name = openDeviceName, devices.contains(where: { $0.name == name }) {
            play(index: i, start: pos, paused: !wasPlaying, audioTrack: currentAudioTrack)
        } else {
            stop()
            currentIndex = i
            errorMessage = "Audio device \(openDeviceName ?? "") disconnected"
        }
    }

    private func watchDevices() {
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices,
                                              mScope: kAudioObjectPropertyScopeGlobal,
                                              mElement: kAudioObjectPropertyElementMain)
        let block: AudioObjectPropertyListenerBlock = { _, _ in
            Task { @MainActor in PlayerModel.shared.refreshDevices() }
        }
        deviceListener = block
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &addr, DispatchQueue.main, block)
    }

    var selectedDevice: AudioDevice? {
        deviceUID.isEmpty ? devices.first(where: { $0.isDefault }) : devices.first(where: { $0.uid == effectiveDeviceUID })
    }

    /// HDMI sinks can change UID when the TV re-sends its EDID (input / mode change),
    /// so fall back to the remembered device name.
    var effectiveDeviceUID: String {
        if deviceUID.isEmpty || devices.contains(where: { $0.uid == deviceUID }) { return deviceUID }
        if let name = Self.defaults.string(forKey: "deviceName"), let d = devices.first(where: { $0.name == name }) { return d.uid }
        return ""
    }

    // MARK: playlist

    func showOpenPanel() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = true
        panel.canChooseFiles = true
        if panel.runModal() == .OK { add(urls: panel.urls, playFirst: currentIndex == nil || !isPlaying) }
    }

    @Published var isScanning = false

    /// Adds files and folders (folders are scanned recursively, off the main thread).
    /// `at` inserts at a playlist position (e.g. a drop between rows); nil appends.
    func add(urls: [URL], playFirst: Bool, at insertIndex: Int? = nil) {
        noteRecent(urls)
        isScanning = true
        Task.detached(priority: .userInitiated) {
            let tracks = Self.expand(urls)
            await MainActor.run {
                self.isScanning = false
                guard !tracks.isEmpty else { return }
                let at = min(max(insertIndex ?? self.playlist.count, 0), self.playlist.count)
                let currentID = self.currentIndex.map { self.playlist[$0].id }
                self.playlist.insert(contentsOf: tracks, at: at)
                self.currentIndex = currentID.flatMap { id in self.playlist.firstIndex { $0.id == id } }
                if playFirst { self.play(index: at) }
            }
        }
    }

    // MARK: recently opened (File > Open Recent)

    static let maxRecents = 10
    /// What the user opened (files or folders, via Open…, drops, Finder), newest first.
    @Published private(set) var recentURLs: [URL] =
        (defaults.stringArray(forKey: "recentFiles") ?? []).map { URL(fileURLWithPath: $0) }

    private func noteRecent(_ urls: [URL]) {
        var list = recentURLs
        for url in urls.reversed() where url.isFileURL {   // keep the opened order: first url on top
            let u = url.standardizedFileURL
            list.removeAll { $0.path == u.path }
            list.insert(u, at: 0)
        }
        setRecents(Array(list.prefix(Self.maxRecents)))
    }

    private func setRecents(_ list: [URL]) {
        recentURLs = list
        Self.defaults.set(list.map(\.path), forKey: "recentFiles")
    }

    func clearRecents() { setRecents([]) }

    /// Plays a recent item: a file already in the playlist plays in place; anything else
    /// (a file not in the playlist, a folder, a cue sheet) is added and its first item played.
    func openRecent(_ url: URL) {
        guard FileManager.default.fileExists(atPath: url.path) else {
            setRecents(recentURLs.filter { $0.path != url.path })
            errorMessage = "\(url.lastPathComponent) no longer exists"
            return
        }
        if let i = playlist.firstIndex(where: { $0.cue == nil && $0.url.standardizedFileURL.path == url.path }) {
            noteRecent([url])
            play(index: i)
        } else {
            add(urls: [url], playFirst: true)
        }
    }

    /// Menu title: the name, plus the parent folder when two recents share a name.
    func recentTitle(_ url: URL) -> String {
        let name = url.lastPathComponent
        let clash = recentURLs.filter { $0.lastPathComponent == name }.count > 1
        return clash ? "\(name) — \(url.deletingLastPathComponent().lastPathComponent)" : name
    }

    nonisolated static func isCue(_ url: URL) -> Bool { url.pathExtension.lowercased() == "cue" }

    /// Files and folders -> playlist items. A .cue sheet becomes its tracks, and the audio
    /// files it covers aren't listed again on their own.
    nonisolated static func expand(_ urls: [URL]) -> [Track] {
        var files: [URL] = []
        for url in urls {
            var isDir: ObjCBool = false
            guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir) else { continue }
            if isDir.boolValue {
                let en = FileManager.default.enumerator(at: url, includingPropertiesForKeys: nil,
                                                        options: [.skipsHiddenFiles, .skipsPackageDescendants])
                var found: [URL] = []
                while let f = en?.nextObject() as? URL {
                    if isCue(f) || mediaExtensions.contains(f.pathExtension.lowercased()) { found.append(f) }
                }
                files += found.sorted { $0.path.localizedStandardCompare($1.path) == .orderedAscending }
            } else {
                files.append(url)
            }
        }
        var sheets: [String: [CueTrack]] = [:]
        var covered = Set<String>()
        for f in files where isCue(f) && sheets[f.standardizedFileURL.path] == nil {
            let cue = CueSheet.load(f, mediaExtensions: mediaExtensions)
            sheets[f.standardizedFileURL.path] = cue
            for t in cue { covered.insert(t.audioURL.standardizedFileURL.path) }
        }
        var tracks: [Track] = []
        var emitted = Set<String>()
        for f in files {
            let path = f.standardizedFileURL.path
            guard emitted.insert(path).inserted else { continue }
            if isCue(f) { tracks += (sheets[path] ?? []).map { Track(url: $0.audioURL, cue: $0) } }
            else if !covered.contains(path) { tracks.append(Track(url: f)) }
        }
        return tracks
    }

    func remove(ids: Set<Track.ID>) {
        let currentID = currentIndex.map { playlist[$0].id }
        if let currentID, ids.contains(currentID) { stop() }
        playlist.removeAll { ids.contains($0.id) }
        currentIndex = currentID.flatMap { id in playlist.firstIndex { $0.id == id } }
    }

    func move(from: IndexSet, to: Int) {
        let currentID = currentIndex.map { playlist[$0].id }
        playlist.move(fromOffsets: from, toOffset: to)
        currentIndex = currentID.flatMap { id in playlist.firstIndex { $0.id == id } }
    }

    func clearPlaylist() {
        stop()
        playlist.removeAll()
    }

    // MARK: transport

    func play(index: Int, start: Double = 0, paused: Bool = false, audioTrack: Int = -1) {
        guard playlist.indices.contains(index) else { return }
        let track = playlist[index]
        bridge.generation += 1
        if currentIndex != index { sink.clear() }
        errorMessage = nil

        var cfg = PCConfig()
        // multichannel PCM transcodes nothing: bitstream is then only native passthrough,
        // of AC3 and DTS alike
        cfg.bitstream_enabled = (multichannelPcm ? ac3Passthrough : ac3Enabled) ? 1 : 0
        cfg.bitstream_codec = Int32(bitstreamCodec == 1 || multichannelPcm ? PC_CODEC_DTS.rawValue : PC_CODEC_AC3.rawValue)
        cfg.multichannel_pcm = multichannelPcm ? 1 : 0
        cfg.dts_bitrate_kbps = Int32(dtsBitrate)
        cfg.ac3_bitrate_kbps = Int32(ac3Bitrate)
        cfg.passthrough = ac3Passthrough ? 1 : 0
        cfg.stereo_bitstream = stereoAsAC3 ? 1 : 0
        cfg.audio_track = Int32(audioTrack)
        cfg.start_position = track.start + max(0, start)   // cue tracks: offset into the file
        cfg.av_offset = avOffsetMs / 1000
        if Self.debug { FileHandle.standardError.write("[app] open \(track.url.lastPathComponent) at \(String(format: "%.2f", cfg.start_position)) s\n".data(using: .utf8)!) }
        var err = [CChar](repeating: 0, count: 512)
        let uidString = effectiveDeviceUID
        let rc = uidString.withCString { uid -> Int32 in
            cfg.device_uid = uidString.isEmpty ? nil : uid
            return pc_player_open(player, track.url.path, &cfg, paused ? 1 : 0, &err, 512)
        }
        currentIndex = index
        selection = [track.id]
        if rc != 0 {
            isOpen = false
            isPlaying = false
            errorMessage = "\(track.name): \(String(cString: err))"
            clearInfo()
            return
        }
        isOpen = true
        isPlaying = !paused
        let resolved = uidString.isEmpty ? devices.first(where: { $0.isDefault }) : devices.first(where: { $0.uid == uidString })
        openDeviceUID = resolved?.uid
        openDeviceName = resolved?.name
        loadInfo(track: track)
        updateActivity()
        tick()
    }

    func togglePlay() {
        guard isOpen else {
            if let i = currentIndex ?? (playlist.isEmpty ? nil : 0) { play(index: i) }
            return
        }
        isPlaying.toggle()
        pc_player_set_paused(player, isPlaying ? 0 : 1)
        updateActivity()
    }

    func stop() {
        bridge.generation += 1
        pc_player_close(player)
        isOpen = false
        isPlaying = false
        position = 0
        sink.clear()
        clearInfo()
        updateActivity()
    }

    func next() {
        guard let i = currentIndex else { return }
        if i + 1 < playlist.count { play(index: i + 1) } else { stop() }
    }

    func previous() {
        guard let i = currentIndex else { return }
        if position > 3 || i == 0 { seek(to: 0) } else { play(index: i - 1) }
    }

    /// `seconds` is relative to the current item (a cue track's start).
    func seek(to seconds: Double) {
        guard isOpen, let i = currentIndex, playlist.indices.contains(i) else { return }
        var s = max(0, seconds)
        if duration > 0 { s = min(s, duration) }
        pc_player_seek(player, playlist[i].start + s)
        position = s
    }

    func skip(_ delta: Double) { seek(to: max(0, position + delta)) }

    func selectAudioTrack(_ index: Int) {
        guard isOpen, let i = currentIndex, index != currentAudioTrack else { return }
        play(index: i, start: position, paused: !isPlaying, audioTrack: index)
    }

    /// A key: next audio track (wraps).
    func cycleAudioTrack() {
        if audioTracks.count > 1 { selectAudioTrack((currentAudioTrack + 1) % audioTracks.count) }
    }

    /// Current audio track without its "N: " prefix, for the controls bar.
    var currentAudioTrackLabel: String {
        guard audioTracks.indices.contains(currentAudioTrack) else { return "" }
        let s = audioTracks[currentAudioTrack]
        if let r = s.range(of: ": "), s.distance(from: s.startIndex, to: r.lowerBound) < 4 { return String(s[r.upperBound...]) }
        return s
    }

    /// Badge text when not bitstreaming: "PCM", or "PCM 5.1" for untranscoded multichannel.
    var pcmBadge: String {
        guard outputDescription.contains("multichannel") else { return "PCM" }
        let parts = outputDescription.split(separator: " ")
        return parts.count > 1 ? "PCM " + (parts[1].split(separator: "(").first.map(String.init) ?? "") : "PCM"
    }

    /// Output settings changed: reopen the current item at the same position.
    private func settingsChanged() {
        guard !batchingSettings, isOpen, let i = currentIndex else { return }
        play(index: i, start: position, paused: !isPlaying, audioTrack: currentAudioTrack)
    }

    func shutdown() {
        pc_player_close(player)
    }

    fileprivate func trackFinished(generation: Int) {
        guard generation == bridge.generation else { return }
        next()
    }

    // MARK: info

    private static let debug = ProcessInfo.processInfo.environment["SURROUND_DEBUG"] != nil
    private var debugTicks = 0

    private func tick() {
        guard isOpen, let i = currentIndex, playlist.indices.contains(i) else { return }
        let abs = pc_player_position(player)
        // cue track boundary: the engine keeps playing the file, so the model moves on itself
        if let end = playlist[i].end, abs >= end - 0.02 {
            if i + 1 < playlist.count, playlist[i].continues(into: playlist[i + 1]) {
                currentIndex = i + 1           // same file, next track: no reopen, no gap
                selection = [playlist[i + 1].id]
                applyTrackInfo(playlist[i + 1])
            } else {
                next()
                return
            }
        }
        guard !isScrubbing, let c = currentIndex else { return }
        position = max(0, abs - playlist[c].start)
        if Self.debug {
            debugTicks += 1
            if debugTicks % 5 == 0 {
                FileHandle.standardError.write("[app] items=\(playlist.count) \(title) pos=\(String(format: "%.2f", position))/\(String(format: "%.1f", duration)) playing=\(isPlaying) mode=\(outputMode) | \(outputDescription) | video: \(videoDescription) | art=\(artwork != nil) fullscreen=\(isFullScreen) \(showsVisualizer ? visualizer!.debugStatus : "vis off")\n".data(using: .utf8)!)
            }
        }
    }

    private func clearInfo() {
        title = ""; artist = ""; album = ""; sourceAudio = ""; outputDescription = ""; videoDescription = ""
        outputMode = -1; bitstreamCodecName = ""; hasVideo = false; artwork = nil; audioTracks = []; currentAudioTrack = -1; duration = 0
    }

    private func loadInfo(track: Track) {
        var info = PCInfo()
        pc_player_get_info(player, &info)
        title = cString(info.title).isEmpty ? track.name : cString(info.title)
        artist = cString(info.artist)
        album = cString(info.album)
        sourceAudio = cString(info.source_audio)
        outputDescription = cString(info.output)
        videoDescription = cString(info.video)
        outputMode = info.mode
        bitstreamCodecName = cString(info.bitstream_codec)
        hasVideo = info.has_video != 0
        fileDuration = info.duration
        applyTrackInfo(track)
        audioTracks = (0..<pc_player_audio_track_count(player)).map { i in
            var buf = [CChar](repeating: 0, count: 256)
            pc_player_audio_track_name(player, i, &buf, 256)
            return String(cString: buf)
        }
        currentAudioTrack = Int(pc_player_current_audio_track(player))
        var size: Int32 = 0
        if let art = pc_player_cover_art(player, &size), size > 0 {
            artwork = NSImage(data: Data(bytes: art, count: Int(size)))
        } else {
            artwork = folderArt(for: track.url)
        }
        // the hardware/software decode note is only known once frames arrive
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
            guard let self, self.isOpen else { return }
            var i2 = PCInfo()
            pc_player_get_info(self.player, &i2)
            self.videoDescription = cString(i2.video)
        }
    }

    private var fileDuration: Double = 0     // engine duration of the open file

    /// Title/artist/album and duration for `track`: cue metadata over the file's tags.
    private func applyTrackInfo(_ track: Track) {
        if let cue = track.cue {
            title = cue.title
            if !cue.performer.isEmpty { artist = cue.performer }
            if !cue.album.isEmpty { album = cue.album }
        }
        duration = max(0, (track.end ?? fileDuration) - track.start)
    }

    // MARK: folder artwork

    private var folderArtPath: String?
    private var folderArtImage: NSImage?

    /// Artwork for a file without embedded art: a .jpg/.jpeg/.png in the same folder (same
    /// rules as the Windows app). The last image read is cached, so an album reads it once.
    private func folderArt(for media: URL) -> NSImage? {
        guard let pick = FolderArt.find(for: media) else { return nil }
        if pick.path == folderArtPath { return folderArtImage }
        let size = (try? pick.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        folderArtPath = pick.path
        folderArtImage = size <= 32 << 20 ? NSImage(contentsOf: pick) : nil
        return folderArtImage
    }

    private func updateActivity() {
        let wantAwake = isPlaying && (hasVideo || showsVisualizer)
        if wantAwake, activity == nil {
            activity = ProcessInfo.processInfo.beginActivity(options: [.idleDisplaySleepDisabled, .userInitiated],
                                                             reason: "Video playback")
        } else if !wantAwake, let a = activity {
            ProcessInfo.processInfo.endActivity(a)
            activity = nil
        }
    }

    var isBitstreaming: Bool { outputMode == Int32(PC_MODE_TRANSCODE.rawValue) || outputMode == Int32(PC_MODE_PASSTHROUGH.rawValue) }
}
