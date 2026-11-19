// .cue sheet parsing: one big audio file (or several) split into virtual tracks.
// Same rules as the Windows app's CueSheet.cs. No UI or engine dependencies.
import Foundation

struct CueTrack: Hashable, Sendable, Codable {
    let audioURL: URL       // resolved, existing file
    let number: Int
    let title: String
    let performer: String
    let album: String
    let start: Double       // seconds into audioURL (INDEX 01)
    let end: Double?        // seconds; nil = to the end of the file
    var cueURL: URL?        // the sheet it came from (playlist files write the sheet)
}

enum CueSheet {
    /// Parses `cueURL` and returns its audio tracks whose files exist. An unreadable or
    /// empty sheet gives an empty list.
    static func load(_ cueURL: URL, mediaExtensions: Set<String>) -> [CueTrack] {
        guard let text = readText(cueURL) else { return [] }
        return parse(text, cueURL: cueURL, mediaExtensions: mediaExtensions)
    }

    private struct Raw {
        var file = ""
        var number = 0
        var audio = true
        var title = "", performer = ""
        var index1: Double?
    }

    static func parse(_ text: String, cueURL: URL, mediaExtensions: Set<String>) -> [CueTrack] {
        let dir = cueURL.deletingLastPathComponent()
        var album = "", albumPerformer = "", file = ""
        var raws: [Raw] = []

        for rawLine in text.split(omittingEmptySubsequences: true, whereSeparator: { $0 == "\n" || $0 == "\r" || $0 == "\r\n" }) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty { continue }
            let sp = line.firstIndex(where: { $0 == " " || $0 == "\t" })
            let key = String(sp.map { line[..<$0] } ?? Substring(line)).uppercased()
            let rest = sp.map { String(line[line.index(after: $0)...]).trimmingCharacters(in: .whitespaces) } ?? ""
            switch key {
            case "FILE":
                file = fileName(rest)
            case "TRACK":
                let parts = rest.split(whereSeparator: { $0 == " " || $0 == "\t" })
                var r = Raw(file: file)
                if let n = parts.first.flatMap({ Int($0) }) { r.number = n }
                r.audio = parts.count < 2 || parts[1].uppercased() == "AUDIO"
                raws.append(r)
            case "TITLE":
                if raws.isEmpty { album = unquote(rest) } else { raws[raws.count - 1].title = unquote(rest) }
            case "PERFORMER":
                if raws.isEmpty { albumPerformer = unquote(rest) } else { raws[raws.count - 1].performer = unquote(rest) }
            case "INDEX":
                guard !raws.isEmpty else { break }
                let parts = rest.split(whereSeparator: { $0 == " " || $0 == "\t" })
                if parts.count == 2, Int(parts[0]) == 1, let t = time(String(parts[1])) {
                    raws[raws.count - 1].index1 = t
                    raws[raws.count - 1].file = file   // a FILE line between TRACK and INDEX 01 belongs to this track
                }
            default:
                break
            }
        }

        let fileCount = Set(raws.map { $0.file.lowercased() }).count
        var resolved: [String: URL?] = [:]
        func resolve(_ name: String) -> URL? {
            if let hit = resolved[name.lowercased()] { return hit }
            let r = resolveFile(dir: dir, name: name, cueURL: fileCount == 1 ? cueURL : nil, exts: mediaExtensions)
            resolved[name.lowercased()] = r
            return r
        }

        var result: [CueTrack] = []
        let playable = raws.filter { $0.index1 != nil && !$0.file.isEmpty }
        for (i, r) in playable.enumerated() {
            // the track ends where the next one in the same file starts (data tracks included,
            // so a data track never gets played as audio)
            var end: Double?
            if i + 1 < playable.count, playable[i + 1].file.lowercased() == r.file.lowercased() { end = playable[i + 1].index1 }
            guard r.audio, let start = r.index1 else { continue }
            if let end, end <= start { continue }
            guard let url = resolve(r.file) else { continue }
            result.append(CueTrack(audioURL: url, number: r.number,
                                   title: r.title.isEmpty ? String(format: "Track %02d", r.number) : r.title,
                                   performer: r.performer.isEmpty ? albumPerformer : r.performer,
                                   album: album, start: start, end: end, cueURL: cueURL))
        }
        return result
    }

    /// The FILE line's name: quoted, or everything before the trailing file type.
    private static func fileName(_ rest: String) -> String {
        if rest.hasPrefix("\"") {
            let body = rest.dropFirst()
            return String(body.firstIndex(of: "\"").map { body[..<$0] } ?? body)
        }
        if let sp = rest.lastIndex(of: " "), sp > rest.startIndex {
            return rest[..<sp].trimmingCharacters(in: .whitespaces)
        }
        return rest
    }

    private static func unquote(_ s: String) -> String {
        let s = s.trimmingCharacters(in: .whitespaces)
        guard s.count >= 2, s.hasPrefix("\"") else { return s }
        let body = s.dropFirst()
        return String(body.lastIndex(of: "\"").map { body[..<$0] } ?? body).trimmingCharacters(in: .whitespaces)
    }

    /// mm:ss:ff, 75 frames per second (minutes can exceed 99).
    static func time(_ s: String) -> Double? {
        let p = s.split(separator: ":")
        guard p.count == 3, let m = Int(p[0]), let sec = Int(p[1]), let f = Int(p[2]) else { return nil }
        return Double(m * 60 + sec) + Double(f) / 75.0
    }

    /// The named file, else the same name with another media extension (rippers often write
    /// "x.wav" and the file was later converted to FLAC), else, for a single-file sheet,
    /// an audio file named like the sheet itself.
    private static func resolveFile(dir: URL, name: String, cueURL: URL?, exts: Set<String>) -> URL? {
        let rel = name.replacingOccurrences(of: "\\", with: "/")   // sheets written on Windows
        let fm = FileManager.default
        let direct = dir.appendingPathComponent(rel)
        if fm.fileExists(atPath: direct.path) { return direct }
        let stems = [direct.deletingPathExtension(), cueURL?.deletingPathExtension()].compactMap { $0 }
        for stem in stems {
            for e in exts.sorted() {
                let c = stem.appendingPathExtension(e)
                if fm.fileExists(atPath: c.path) { return c }
            }
        }
        return nil
    }

    /// UTF-16 with a BOM, UTF-8 (with or without BOM) if it decodes cleanly, else
    /// Windows-1252, which older (Windows) rippers wrote.
    /// A text file in whatever encoding rippers and subtitle tools wrote (see decode).
    static func readText(_ url: URL) -> String? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return decode(data)
    }

    static func decode(_ d: Data) -> String? {
        let b = [UInt8](d.prefix(3))
        if b.count >= 2, b[0] == 0xFF, b[1] == 0xFE { return String(data: d.dropFirst(2), encoding: .utf16LittleEndian) }
        if b.count >= 2, b[0] == 0xFE, b[1] == 0xFF { return String(data: d.dropFirst(2), encoding: .utf16BigEndian) }
        let body = b.count == 3 && b == [0xEF, 0xBB, 0xBF] ? d.dropFirst(3) : d
        return String(data: body, encoding: .utf8)
            ?? String(data: body, encoding: .windowsCP1252)
            ?? String(data: body, encoding: .isoLatin1)
    }
}
