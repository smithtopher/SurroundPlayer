// Subtitles: the engine's tracks (embedded streams + files next to the video + files the user
// loads), the remembered language, and polling what to show. Same rules as the Windows app
// (PlayerModel.Subtitles.cs).
import AppKit
import CryptoKit
import PlayerCore

/// What the subtitle overlay shows now.
struct SubtitleFrame: Equatable {
    enum Kind { case none, text, bitmap }
    var kind: Kind = .none
    var text = ""
    var image: CGImage?
    var x = 0, y = 0, w = 0, h = 0, refW = 0, refH = 0   // bitmap placement on a refW x refH canvas
    static let none = SubtitleFrame()
}

extension PlayerModel {
    nonisolated static let subtitleExtensions: Set<String> = ["srt", "ass", "ssa", "vtt"]

    /// Before the engine reopens: the same file (audio track change, device reopen) keeps the
    /// subtitle choice and the files the user loaded; another file starts fresh.
    func prepareSubtitlesForOpen(_ track: Track) {
        let same = subFilePath == track.url.path
        keepSubtitle = same ? currentSubtitleTrack : nil
        if !same { userSubFiles.removeAll() }
        subFilePath = track.url.path
    }

    /// After open: add sidecar files and the user's files, list the tracks, select one.
    func loadSubtitles(_ track: Track) {
        if hasVideo {
            for f in Self.findSidecarSubtitles(track.url) { _ = addSubtitleFileToEngine(f) }
            for f in userSubFiles { _ = addSubtitleFileToEngine(f) }
        }
        refreshSubtitleTracks()
        let pick = keepSubtitle.flatMap { $0 < subtitleTracks.count ? $0 : nil } ?? preferredSubtitleTrack()
        pc_player_set_subtitle_track(player, Int32(pick))
        currentSubtitleTrack = Int(pc_player_current_subtitle_track(player))
        subSerial = -2   // fetch whatever the new selection shows
    }

    func refreshSubtitleTracks() {
        var names: [String] = [], langs: [String] = [], flags: [Int32] = []
        let count = Int(pc_player_subtitle_track_count(player))
        var buf = [CChar](repeating: 0, count: 256)
        for i in 0..<count {
            pc_player_subtitle_track_name(player, Int32(i), &buf, 256)
            names.append(String(cString: buf))
            pc_player_subtitle_track_language(player, Int32(i), &buf, 256)
            langs.append(Self.normalizeLanguage(String(cString: buf)))
            var f = pc_player_subtitle_track_flags(player, Int32(i))
            if names[i].lowercased().contains(".forced.") { f |= Int32(PC_SUBFLAG_FORCED) }
            flags.append(f)
        }
        // external files: language from the name, e.g. "Movie.en.srt" or "Movie.eng.forced.srt"
        for i in 0..<count where langs[i].isEmpty {
            guard let r = names[i].range(of: ": ") else { continue }
            let parts = (String(names[i][r.upperBound...]) as NSString).deletingPathExtension.split(separator: ".")
            var j = parts.count - 1
            while j >= 1, langs[i].isEmpty { langs[i] = Self.normalizeLanguage(String(parts[j])); j -= 1 }
        }
        subtitleLangs = langs
        subtitleFlags = flags
        subtitleTracks = names
    }

    /// `subtitleLanguage`: "" (never chosen) follows the file: the track it marks as default,
    /// like mpv/VLC. "off": off. "*" or a language: a track in that language, else one of
    /// unknown language. A forced track (foreign-language parts only) is shown when nothing
    /// else is.
    private func preferredSubtitleTrack() -> Int {
        let pref = subtitleLanguage
        let all = Array(0..<subtitleTracks.count)
        func forced(_ i: Int) -> Bool { subtitleFlags[i] & Int32(PC_SUBFLAG_FORCED) != 0 }
        func isDefault(_ i: Int) -> Bool { subtitleFlags[i] & Int32(PC_SUBFLAG_DEFAULT) != 0 }
        let firstForced = all.first(where: forced) ?? -1
        if pref == "off" { return firstForced }
        if pref.isEmpty { return all.first { isDefault($0) && !forced($0) } ?? firstForced }
        if pref == "*" { return all.first { !forced($0) } ?? all.first ?? -1 }
        let lang = all.filter { subtitleLangs[$0] == pref }
        if !lang.isEmpty { return lang.first { !forced($0) } ?? lang[0] }
        return all.first { subtitleLangs[$0].isEmpty && !forced($0) } ?? -1
    }

    /// Short label for the CC button: "Off", the language ("EN"), or the track number.
    var subtitleShortLabel: String {
        guard subtitleTracks.indices.contains(currentSubtitleTrack) else { return "Off" }
        let l = subtitleLangs[currentSubtitleTrack]
        return l.isEmpty ? "#\(currentSubtitleTrack + 1)" : l.uppercased()
    }

    /// The user's choice (-1 = off); also remembered for the next files.
    func selectSubtitleTrack(_ index: Int) {
        guard isOpen else { return }
        pc_player_set_subtitle_track(player, Int32(index))
        currentSubtitleTrack = Int(pc_player_current_subtitle_track(player))
        subtitleLanguage = currentSubtitleTrack < 0 ? "off"
            : subtitleLangs[currentSubtitleTrack].isEmpty ? "*" : subtitleLangs[currentSubtitleTrack]
    }

    /// S key: off -> track 1 -> track 2 ... -> off.
    func cycleSubtitleTrack() {
        guard !subtitleTracks.isEmpty else { return }
        let next = currentSubtitleTrack + 1
        selectSubtitleTrack(next >= subtitleTracks.count ? -1 : next)
    }

    func showSubtitleOpenPanel() {
        guard isOpen, hasVideo else { return }
        let panel = NSOpenPanel()
        panel.allowedContentTypes = Self.subtitleExtensions.compactMap { .init(filenameExtension: $0) }
        panel.allowsOtherFileTypes = true
        if let i = currentIndex, playlist.indices.contains(i) { panel.directoryURL = playlist[i].url.deletingLastPathComponent() }
        if panel.runModal() == .OK, let url = panel.url { loadSubtitleFile(url) }
    }

    /// Adds a subtitle file for the current video and shows it.
    func loadSubtitleFile(_ url: URL) {
        guard isOpen else { return }
        let index = addSubtitleFileToEngine(url)
        guard index >= 0 else { errorMessage = "Could not read subtitles from \(url.lastPathComponent)"; return }
        if !userSubFiles.contains(url) { userSubFiles.append(url) }
        refreshSubtitleTracks()
        selectSubtitleTrack(index)
    }

    private func addSubtitleFileToEngine(_ url: URL) -> Int {
        Int(pc_player_add_subtitle_file(player, Self.utf8Copy(url).path, url.lastPathComponent))
    }

    /// "Movie.srt", "Movie.en.srt", "Movie.English.forced.ass"... next to "Movie.mkv".
    nonisolated static func findSidecarSubtitles(_ media: URL) -> [URL] {
        let dir = media.deletingLastPathComponent()
        let stem = media.deletingPathExtension().lastPathComponent.lowercased()
        guard let items = try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil,
                                                                      options: [.skipsHiddenFiles]) else { return [] }
        return items.filter { f in
            guard subtitleExtensions.contains(f.pathExtension.lowercased()) else { return false }
            let n = f.deletingPathExtension().lastPathComponent.lowercased()
            return n == stem || n.hasPrefix(stem + ".")
        }
        .sorted { $0.lastPathComponent.lowercased() < $1.lastPathComponent.lowercased() }
    }

    /// FFmpeg reads subtitle text as UTF-8. Files in another encoding (UTF-16, or the
    /// Windows-1252 older tools wrote) are converted to a UTF-8 copy in the temp folder.
    nonisolated static func utf8Copy(_ url: URL) -> URL {
        guard let data = try? Data(contentsOf: url) else { return url }
        let b = [UInt8](data.prefix(2))
        let utf16 = b.count == 2 && (b == [0xFF, 0xFE] || b == [0xFE, 0xFF])
        if !utf16, String(data: data, encoding: .utf8) != nil { return url }
        guard let text = CueSheet.decode(data) else { return url }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("SurroundPlayer/subtitles")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let hash = Insecure.SHA1.hash(data: Data(url.path.lowercased().utf8)).map { String(format: "%02x", $0) }.joined().prefix(12)
        let copy = dir.appendingPathComponent("\(hash).\(url.pathExtension)")
        do { try text.write(to: copy, atomically: true, encoding: .utf8); return copy } catch { return url }
    }

    // ISO 639-2/B codes that differ from the /T ones Foundation knows
    private nonisolated static let bCodes: [String: String] = [
        "fre": "fr", "ger": "de", "dut": "nl", "chi": "zh", "cze": "cs", "gre": "el", "per": "fa", "rum": "ro",
        "slo": "sk", "alb": "sq", "arm": "hy", "baq": "eu", "bur": "my", "geo": "ka", "ice": "is", "mac": "mk",
        "mao": "mi", "may": "ms", "tib": "bo", "wel": "cy",
    ]
    /// two-letter code, three-letter code and English name (lowercased) -> two-letter code
    private nonisolated static let languageIndex: [String: String] = {
        var idx: [String: String] = [:]
        let en = Locale(identifier: "en")
        for code in Locale.LanguageCode.isoLanguageCodes {
            guard let two = code.identifier(.alpha2) else { continue }
            idx[two] = idx[two] ?? two
            if let three = code.identifier(.alpha3) { idx[three] = idx[three] ?? two }
            if let name = en.localizedString(forLanguageCode: two)?.lowercased() { idx[name] = idx[name] ?? two }
        }
        return idx
    }()

    /// "eng" / "en" / "English" -> "en"; "" if not recognised.
    nonisolated static func normalizeLanguage(_ code: String) -> String {
        let c = code.trimmingCharacters(in: .whitespaces).lowercased()
        if c.isEmpty || c == "und" { return "" }
        return bCodes[c] ?? languageIndex[c] ?? ""
    }

    /// Polls the engine (every 40 ms) and publishes what the overlay should show when it changes.
    func pollSubtitle() {
        let d = sink.displaySize
        if d != videoDisplaySize { videoDisplaySize = d }
        guard isOpen else {
            if subtitleFrame != .none { subtitleFrame = .none }
            subSerial = -1
            return
        }
        var st = PCSubtitleState()
        pc_player_subtitle_state(player, &st)
        guard st.serial != subSerial else { return }
        var frame = SubtitleFrame.none
        if st.kind == Int32(PC_SUB_TEXT) {
            var buf = [CChar](repeating: 0, count: Int(st.text_len) + 1)
            guard pc_player_subtitle_text(player, &buf, Int32(buf.count)) == st.serial else { return }   // changed: next poll
            frame.kind = .text
            frame.text = String(cString: buf)
        } else if st.kind == Int32(PC_SUB_BITMAP), st.w > 0, st.h > 0 {
            var px = [UInt8](repeating: 0, count: Int(st.w) * Int(st.h) * 4)
            guard pc_player_subtitle_bitmap(player, &px, Int32(px.count)) == st.serial else { return }
            frame.kind = .bitmap
            frame.image = Self.makeImage(bgra: px, w: Int(st.w), h: Int(st.h))
            (frame.x, frame.y, frame.w, frame.h) = (Int(st.x), Int(st.y), Int(st.w), Int(st.h))
            frame.refW = st.ref_w > 0 ? Int(st.ref_w) : videoWidth
            frame.refH = st.ref_h > 0 ? Int(st.ref_h) : videoHeight
        }
        subSerial = st.serial
        subtitleFrame = frame
    }

    /// Premultiplied BGRA rows -> CGImage.
    nonisolated static func makeImage(bgra: [UInt8], w: Int, h: Int) -> CGImage? {
        guard let provider = CGDataProvider(data: Data(bgra) as CFData) else { return nil }
        let info = CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        return CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: w * 4,
                       space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: info, provider: provider,
                       decode: nil, shouldInterpolate: true, intent: .defaultIntent)
    }
}
