// Playlists: .m3u/.m3u8 files the user saves or opens, and the session file that restores
// the playlist at the next launch. Same rules as the Windows app's PlaylistFile.cs.
import Foundation

enum PlaylistFile {
    static func isPlaylist(_ url: URL) -> Bool { ["m3u", "m3u8"].contains(url.pathExtension.lowercased()) }

    /// The entries of an .m3u/.m3u8 (relative ones resolved against the playlist's folder).
    /// Comments, URLs and nested playlists are skipped.
    static func readM3U(_ url: URL) -> [URL] {
        guard let text = CueSheet.readText(url) else { return [] }
        let dir = url.deletingLastPathComponent()
        var result: [URL] = []
        for raw in text.split(whereSeparator: { $0 == "\n" || $0 == "\r" || $0 == "\r\n" }) {
            var line = raw.trimmingCharacters(in: .whitespaces)
            line = line.trimmingCharacters(in: CharacterSet(charactersIn: "\u{FEFF}"))
            if line.isEmpty || line.hasPrefix("#") { continue }
            let entry: URL
            if line.lowercased().hasPrefix("file://") {
                guard let u = URL(string: line), u.isFileURL else { continue }
                entry = u
            } else if line.contains("://") {
                continue
            } else {
                let path = line.replacingOccurrences(of: "\\", with: "/")   // written on Windows
                entry = path.hasPrefix("/") ? URL(fileURLWithPath: path) : dir.appendingPathComponent(path)
            }
            let full = entry.standardizedFileURL
            if !isPlaylist(full) { result.append(full) }
        }
        return result
    }

    /// Writes an extended M3U in UTF-8, paths relative to the playlist's folder (so a library
    /// can be moved with its playlists). Cue sheet tracks are written as their sheet, once per
    /// run of tracks from it, which other players also read.
    static func writeM3U(_ url: URL, tracks: [Track]) throws {
        let dir = url.deletingLastPathComponent().standardizedFileURL
        var out = "#EXTM3U\n"
        var lastSheet: String?
        for t in tracks {
            let target: URL, title: String
            if let cue = t.cue, let sheet = cue.cueURL {
                if sheet.path == lastSheet { continue }
                lastSheet = sheet.path
                target = sheet
                title = cue.album.isEmpty ? sheet.deletingPathExtension().lastPathComponent : cue.album
            } else {
                lastSheet = nil
                target = t.url
                title = t.name
            }
            out += "#EXTINF:-1,\(title.replacingOccurrences(of: "\n", with: " "))\n\(relativePath(target.standardizedFileURL, from: dir))\n"
        }
        try out.write(to: url, atomically: true, encoding: .utf8)
    }

    /// `target` relative to the folder `dir` ("../Other/x.flac"); absolute if they share nothing.
    static func relativePath(_ target: URL, from dir: URL) -> String {
        let t = target.pathComponents, d = dir.pathComponents
        var i = 0
        while i < t.count, i < d.count, t[i] == d[i] { i += 1 }
        if i <= 1 { return target.path }   // only "/" in common
        return (Array(repeating: "..", count: d.count - i) + t[i...]).joined(separator: "/")
    }

    // MARK: session (restored at launch)

    struct SessionTrack: Codable { let path: String; let cue: CueTrack? }
    struct Session: Codable { let version: Int; let tracks: [SessionTrack]; let current: Int; let position: Double }

    static var sessionURL: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("SurroundPlayer/playlist.json")
    }

    static func saveSession(tracks: [Track], current: Int?, position: Double) {
        let s = Session(version: 1, tracks: tracks.map { SessionTrack(path: $0.url.path, cue: $0.cue) },
                        current: current ?? -1, position: position)
        guard let data = try? JSONEncoder().encode(s) else { return }
        try? FileManager.default.createDirectory(at: sessionURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: sessionURL, options: .atomic)
    }

    static func loadSession() -> Session? {
        guard let data = try? Data(contentsOf: sessionURL) else { return nil }
        return try? JSONDecoder().decode(Session.self, from: data)
    }
}
