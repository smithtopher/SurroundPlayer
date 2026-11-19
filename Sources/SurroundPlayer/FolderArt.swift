import Foundation

/// Artwork for a file without embedded art: a .jpg/.jpeg/.png in its folder.
/// Same rules as the Windows app (PlayerModel.FindFolderArt).
enum FolderArt {
    static let extensions: Set<String> = ["jpg", "jpeg", "png"]
    static let names = ["cover", "folder", "front", "album", "albumart", "albumartlarge"]

    /// An image named like the media file, then the usual names (cover, folder, front, ...),
    /// then names containing cover/folder/front ("Cover (front).jpg"), then the first image
    /// alphabetically.
    static func find(for media: URL) -> URL? {
        let dir = media.deletingLastPathComponent()
        guard let items = try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil,
                                                                      options: [.skipsHiddenFiles]) else { return nil }
        let images = items.filter { extensions.contains($0.pathExtension.lowercased()) }
            .sorted { $0.lastPathComponent.lowercased() < $1.lastPathComponent.lowercased() }
        guard !images.isEmpty else { return nil }
        func name(_ u: URL) -> String { u.deletingPathExtension().lastPathComponent.lowercased() }
        let stem = media.deletingPathExtension().lastPathComponent.lowercased()
        return images.first { name($0) == stem }
            ?? names.lazy.compactMap { n in images.first { name($0) == n } }.first
            ?? images.first { u in names.prefix(3).contains { name(u).contains($0) } }
            ?? images[0]
    }
}
