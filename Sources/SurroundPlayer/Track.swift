import Foundation

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
