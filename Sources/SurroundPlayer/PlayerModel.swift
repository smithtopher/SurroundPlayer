import AppKit
import CoreAudio
import PlayerCore
import SwiftUI
import UniformTypeIdentifiers

struct Track: Identifiable, Hashable {
    let id = UUID()
    let url: URL
    var name: String { url.deletingPathExtension().lastPathComponent }
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

    static let mediaExtensions: Set<String> = [
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
    @Published var ac3Passthrough: Bool = defaults.object(forKey: "ac3Passthrough") as? Bool ?? true {
        didSet { Self.defaults.set(ac3Passthrough, forKey: "ac3Passthrough"); settingsChanged() }
    }
    @Published var stereoAsAC3: Bool = defaults.object(forKey: "stereoAsAC3") as? Bool ?? false {
        didSet { Self.defaults.set(stereoAsAC3, forKey: "stereoAsAC3"); settingsChanged() }
    }
    @Published var avOffsetMs: Double = defaults.double(forKey: "avOffsetMs") {
        didSet { Self.defaults.set(avOffsetMs, forKey: "avOffsetMs"); pc_player_set_av_offset(player, avOffsetMs / 1000) }
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
        pc_player_set_callbacks(player, { ctx, pb, _ in
            guard let ctx, let pb else { return }
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

    func add(urls: [URL], playFirst: Bool) {
        var files: [URL] = []
        for url in urls {
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue {
                let en = FileManager.default.enumerator(at: url, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles])
                let found = (en?.allObjects as? [URL] ?? []).filter { Self.mediaExtensions.contains($0.pathExtension.lowercased()) }
                files += found.sorted { $0.path.localizedStandardCompare($1.path) == .orderedAscending }
            } else {
                files.append(url)
            }
        }
        guard !files.isEmpty else { return }
        let firstNew = playlist.count
        playlist += files.map { Track(url: $0) }
        if playFirst { play(index: firstNew) }
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
        cfg.ac3_enabled = ac3Enabled ? 1 : 0
        cfg.ac3_bitrate_kbps = Int32(ac3Bitrate)
        cfg.ac3_passthrough = ac3Passthrough ? 1 : 0
        cfg.stereo_as_ac3 = stereoAsAC3 ? 1 : 0
        cfg.audio_track = Int32(audioTrack)
        cfg.start_position = start
        cfg.av_offset = avOffsetMs / 1000
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

    func seek(to seconds: Double) {
        guard isOpen else { return }
        pc_player_seek(player, seconds)
        position = seconds
    }

    func skip(_ delta: Double) { seek(to: max(0, position + delta)) }

    func selectAudioTrack(_ index: Int) {
        guard isOpen, let i = currentIndex, index != currentAudioTrack else { return }
        play(index: i, start: position, paused: !isPlaying, audioTrack: index)
    }

    /// Output settings changed: reopen the current item at the same position.
    private func settingsChanged() {
        guard isOpen, let i = currentIndex else { return }
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
        guard isOpen, !isScrubbing else { return }
        position = pc_player_position(player)
        if Self.debug {
            debugTicks += 1
            if debugTicks % 5 == 0 {
                FileHandle.standardError.write("[app] \(title) pos=\(String(format: "%.2f", position))/\(String(format: "%.1f", duration)) playing=\(isPlaying) mode=\(outputMode) | \(outputDescription) | video: \(videoDescription)\n".data(using: .utf8)!)
            }
        }
    }

    private func clearInfo() {
        title = ""; artist = ""; album = ""; sourceAudio = ""; outputDescription = ""; videoDescription = ""
        outputMode = -1; hasVideo = false; artwork = nil; audioTracks = []; currentAudioTrack = -1; duration = 0
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
        hasVideo = info.has_video != 0
        duration = info.duration
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
            artwork = nil
        }
        // the hardware/software decode note is only known once frames arrive
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
            guard let self, self.isOpen else { return }
            var i2 = PCInfo()
            pc_player_get_info(self.player, &i2)
            self.videoDescription = cString(i2.video)
        }
    }

    private func updateActivity() {
        let wantAwake = isPlaying && hasVideo
        if wantAwake, activity == nil {
            activity = ProcessInfo.processInfo.beginActivity(options: [.idleDisplaySleepDisabled, .userInitiated],
                                                             reason: "Video playback")
        } else if !wantAwake, let a = activity {
            ProcessInfo.processInfo.endActivity(a)
            activity = nil
        }
    }

    var isBitstreaming: Bool { outputMode == Int32(PC_MODE_AC3_TRANSCODE.rawValue) || outputMode == Int32(PC_MODE_AC3_PASSTHROUGH.rawValue) }
}
