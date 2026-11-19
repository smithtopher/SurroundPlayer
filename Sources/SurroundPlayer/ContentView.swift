import SwiftUI
import UniformTypeIdentifiers

struct ContentView: View {
    @EnvironmentObject var model: PlayerModel
    @State private var showPlaylist = true
    @State private var dropTargeted = false
    @State private var controlsVisible = true
    @State private var hideControls: DispatchWorkItem?

    var body: some View {
        Group {
            if model.isFullScreen {
                // Just the picture, out of the split view (which insets for the title bar):
                // it runs under the top bar, which fades with the controls.
                stageColumn
                    .ignoresSafeArea()
            } else {
                HSplitView {
                    stageColumn
                        .frame(minWidth: 520)
                    if showPlaylist {
                        PlaylistView()
                            .frame(minWidth: 220, idealWidth: 280, maxWidth: 420)
                    }
                }
            }
        }
        .background(WindowAccessor { model.attach(window: $0) })
        .toolbar {
            ToolbarItemGroup {
                Button { model.showOpenPanel() } label: { Label("Open", systemImage: "plus") }
                    .help("Add files or folders")
                DevicePicker()
                Button { model.visualizerEnabled.toggle() } label: {
                    Label("Visualiser", systemImage: model.visualizerEnabled ? "sparkles.tv.fill" : "sparkles.tv")
                }
                .help(model.visualizerEnabled ? "Hide the visualiser (V)" : "Show the visualiser, also in place of video (V)")
                Button { model.toggleFullScreen() } label: {
                    Label("Full Screen", systemImage: "arrow.up.left.and.arrow.down.right")
                }
                .help("Full screen player (F)")
                Button { showPlaylist.toggle() } label: { Label("Playlist", systemImage: "sidebar.right") }
                    .help("Show or hide the playlist")
            }
        }
        .onChange(of: model.isFullScreen) { _, full in
            if full { pokeControls() } else { hideControls?.cancel(); controlsVisible = true }
        }
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted) { providers in
            loadDroppedURLs(providers) { urls in model.add(urls: urls, playFirst: !model.isPlaying) }
            return true
        }
        .overlay {
            if dropTargeted {
                RoundedRectangle(cornerRadius: 12).stroke(Color.accentColor, lineWidth: 3).padding(6)
            }
        }
    }

    private var stageColumn: some View {
        VStack(spacing: 0) {
            stage
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .overlay(alignment: .bottom) {
                    if model.isFullScreen {
                        ControlsBar()
                            .clipShape(RoundedRectangle(cornerRadius: 14))
                            .frame(maxWidth: 900)
                            .padding(24)
                            .opacity(controlsVisible ? 1 : 0)
                            .onHover { inside in if inside { pokeControls(keep: true) } }
                    }
                }
                .onContinuousHover { phase in
                    if case .active = phase, model.isFullScreen { pokeControls() }
                }
            if !model.isFullScreen { ControlsBar() }
        }
    }

    /// Full screen: show the controls and the top bar on mouse movement, hide them (and
    /// the cursor) after 3 s, unless the mouse is resting on the top bar.
    private func pokeControls(keep: Bool = false) {
        hideControls?.cancel()
        if !controlsVisible {
            withAnimation(.easeOut(duration: 0.2)) { controlsVisible = true }
            model.setTopBarVisible(true)
        }
        guard !keep else { return }
        let w = DispatchWorkItem {
            guard model.isFullScreen else { return }
            if model.mouseIsOverTopBar { pokeControls(); return }
            withAnimation(.easeIn(duration: 0.5)) { controlsVisible = false }
            model.setTopBarVisible(false)
            NSCursor.setHiddenUntilMouseMoves(true)
        }
        hideControls = w
        DispatchQueue.main.asyncAfter(deadline: .now() + 3, execute: w)
    }

    @ViewBuilder private var stage: some View {
        ZStack {
            Color.black
            if model.showsVisualizer, let r = model.visualizer {
                VisualizerStage(renderer: r)        // overrides the video track when enabled
            } else if model.hasVideo {
                VideoView(sink: model.sink)
            } else {
                NowPlayingArt()
                    .onTapGesture(count: 2) { model.toggleFullScreen() }
            }
            if let err = model.errorMessage {
                VStack {
                    Spacer()
                    Text(err)
                        .padding(10)
                        .background(.red.opacity(0.85), in: RoundedRectangle(cornerRadius: 8))
                        .foregroundStyle(.white)
                        .padding()
                }
            }
        }
    }
}

func loadDroppedURLs(_ providers: [NSItemProvider], completion: @escaping @MainActor ([URL]) -> Void) {
    let group = DispatchGroup()
    let lock = NSLock()
    var urls: [URL] = []
    for p in providers {
        group.enter()
        _ = p.loadObject(ofClass: URL.self) { url, _ in
            if let url { lock.lock(); urls.append(url); lock.unlock() }
            group.leave()
        }
    }
    group.notify(queue: .main) {
        let sorted = urls.sorted { $0.path.localizedStandardCompare($1.path) == .orderedAscending }
        MainActor.assumeIsolated { completion(sorted) }
    }
}

struct NowPlayingArt: View {
    @EnvironmentObject var model: PlayerModel
    var body: some View {
        VStack(spacing: 16) {
            Group {
                if let art = model.artwork {
                    Image(nsImage: art).resizable().aspectRatio(contentMode: .fit)
                } else {
                    ZStack {
                        RoundedRectangle(cornerRadius: 16).fill(Color.white.opacity(0.06))
                        Image(systemName: model.title.isEmpty ? "hifispeaker.2" : "music.note")
                            .font(.system(size: 64, weight: .light))
                            .foregroundStyle(.white.opacity(0.35))
                    }
                    .aspectRatio(1, contentMode: .fit)
                }
            }
            .frame(maxWidth: 320, maxHeight: 320)
            .shadow(radius: 20)
            if model.title.isEmpty {
                Text("Drop music or video files here")
                    .foregroundStyle(.white.opacity(0.5))
            } else {
                VStack(spacing: 4) {
                    Text(model.title).font(.title2.weight(.semibold)).foregroundStyle(.white)
                    if !model.artist.isEmpty || !model.album.isEmpty {
                        Text([model.artist, model.album].filter { !$0.isEmpty }.joined(separator: " — "))
                            .foregroundStyle(.white.opacity(0.7))
                    }
                }
                .multilineTextAlignment(.center)
            }
        }
        .padding(32)
    }
}

struct ControlsBar: View {
    @EnvironmentObject var model: PlayerModel
    @State private var scrubValue: Double = 0

    var body: some View {
        VStack(spacing: 8) {
            HStack(spacing: 10) {
                Text(timeString(model.isScrubbing ? scrubValue : model.position))
                    .monospacedDigit().font(.caption).foregroundStyle(.secondary).frame(width: 56, alignment: .trailing)
                Slider(value: Binding(get: { model.isScrubbing ? scrubValue : model.position },
                                      set: { scrubValue = $0 }),
                       in: 0...max(model.duration, 1)) { editing in
                    if editing {
                        scrubValue = model.position
                        model.isScrubbing = true
                    } else {
                        model.seek(to: scrubValue)
                        model.isScrubbing = false
                    }
                }
                .controlSize(.small)
                .disabled(model.duration <= 0)
                Text(timeString(model.duration))
                    .monospacedDigit().font(.caption).foregroundStyle(.secondary).frame(width: 56, alignment: .leading)
            }
            HStack(spacing: 14) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(model.title.isEmpty ? "Not playing" : model.title).font(.headline).lineLimit(1)
                    if !model.artist.isEmpty { Text(model.artist).font(.subheadline).foregroundStyle(.secondary).lineLimit(1) }
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                Button { model.previous() } label: { Image(systemName: "backward.fill") }
                Button { model.togglePlay() } label: {
                    Image(systemName: model.isPlaying ? "pause.fill" : "play.fill").font(.title2).frame(width: 32)
                }
                Button { model.next() } label: { Image(systemName: "forward.fill") }

                HStack(spacing: 8) {
                    if model.audioTracks.count > 1 {
                        Menu {
                            ForEach(Array(model.audioTracks.enumerated()), id: \.offset) { i, name in
                                Button { model.selectAudioTrack(i) } label: {
                                    if i == model.currentAudioTrack { Label(name, systemImage: "checkmark") } else { Text(name) }
                                }
                            }
                        } label: {
                            HStack(spacing: 6) {
                                Image(systemName: "waveform")
                                Text(model.currentAudioTrackLabel).font(.caption).lineLimit(1).truncationMode(.tail)
                                    .frame(maxWidth: 220, alignment: .leading)
                            }
                        }
                            .menuStyle(.borderlessButton)
                            .fixedSize()
                            .help("Audio track (A: next track)")
                    }
                    OutputBadge()
                }
                .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .buttonStyle(.borderless)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(.bar)
    }
}

struct OutputBadge: View {
    @EnvironmentObject var model: PlayerModel
    @State private var showDetails = false

    var body: some View {
        if model.outputMode >= 0 {
            Button { showDetails.toggle() } label: {
                Text(model.isBitstreaming ? (model.bitstreamCodecName.isEmpty ? "BITSTREAM" : model.bitstreamCodecName.uppercased()) : model.pcmBadge)
                    .font(.system(size: 10, weight: .bold, design: .rounded))
                    .padding(.horizontal, 6).padding(.vertical, 3)
                    .foregroundStyle(model.isBitstreaming ? Color.white : Color.primary)
                    .background(model.isBitstreaming ? Color.accentColor : Color.secondary.opacity(0.2),
                                in: RoundedRectangle(cornerRadius: 4))
            }
            .buttonStyle(.plain)
            .help(model.outputDescription)
            .popover(isPresented: $showDetails, arrowEdge: .top) {
                VStack(alignment: .leading, spacing: 8) {
                    detail("Source", model.sourceAudio)
                    detail("Output", model.outputDescription)
                    if model.hasVideo { detail("Video", model.videoDescription) }
                }
                .padding()
                .frame(width: 380)
            }
        }
    }

    private func detail(_ k: String, _ v: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(k).font(.caption).foregroundStyle(.secondary)
            Text(v).font(.callout).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
        }
    }
}

struct DevicePicker: View {
    @EnvironmentObject var model: PlayerModel
    var body: some View {
        Menu {
            Button { model.deviceUID = "" } label: {
                if model.deviceUID.isEmpty { Label("System Default", systemImage: "checkmark") } else { Text("System Default") }
            }
            Divider()
            ForEach(model.devices) { d in
                Button { model.deviceUID = d.uid } label: {
                    let name = d.name + (d.isHDMI ? "  (HDMI)" : "") + (d.canBitstream ? "  • DD" : "")
                    if model.deviceUID == d.uid { Label(name, systemImage: "checkmark") } else { Text(name) }
                }
            }
            Divider()
            Button("Refresh Devices") { model.refreshDevices() }
        } label: {
            Label(model.selectedDevice?.name ?? "Output", systemImage: model.selectedDevice?.isHDMI == true ? "tv" : "hifispeaker")
        }
        .help("Audio output device")
    }
}

struct PlaylistView: View {
    @EnvironmentObject var model: PlayerModel
    @State private var query = ""
    @FocusState private var searchFocused: Bool

    private var terms: [String] {
        query.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
            .split(whereSeparator: \.isWhitespace).map(String.init)
    }

    /// (index in the full playlist, track) for rows that match the search.
    private var rows: [(index: Int, track: Track)] {
        let t = terms
        return model.playlist.enumerated()
            .filter { t.isEmpty || $0.element.matches(t) }
            .map { (index: $0.offset, track: $0.element) }
    }

    var body: some View {
        let rows = self.rows
        let filtering = !terms.isEmpty
        VStack(spacing: 0) {
            searchField
            Divider()
            List(selection: $model.selection) {
                ForEach(rows, id: \.track.id) { row in
                    PlaylistRow(track: row.track, isCurrent: model.currentIndex == row.index, isPlaying: model.isPlaying)
                        .tag(row.track.id)
                        .contentShape(Rectangle())
                        .onTapGesture(count: 2) { model.play(index: row.index) }
                }
                .onMove(perform: filtering ? nil : { from, to in model.move(from: from, to: to) })
                .onInsert(of: [.fileURL]) { offset, providers in
                    // drop files/folders between rows; with a filter active, append
                    let at = filtering ? nil : offset
                    loadDroppedURLs(providers) { urls in model.add(urls: urls, playFirst: false, at: at) }
                }
            }
            .overlay {
                if model.playlist.isEmpty {
                    VStack(spacing: 8) {
                        Image(systemName: "folder.badge.plus").font(.system(size: 30)).foregroundStyle(.tertiary)
                        Text("Drop files or folders here").foregroundStyle(.secondary)
                    }
                } else if filtering && rows.isEmpty {
                    Text("No matches").foregroundStyle(.secondary)
                }
            }
            .contextMenu(forSelectionType: Track.ID.self) { ids in
                Button("Play") { play(ids) }
                Button("Show in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting(model.playlist.filter { ids.contains($0.id) }.map(\.url))
                }
                Divider()
                Button("Remove") { model.remove(ids: ids) }
            } primaryAction: { ids in play(ids) }
            .onDeleteCommand { model.remove(ids: model.selection) }
            Divider()
            HStack {
                if model.isScanning {
                    ProgressView().controlSize(.small)
                    Text("Scanning…").font(.caption).foregroundStyle(.secondary)
                } else {
                    Text(filtering ? "\(rows.count) of \(model.playlist.count) items" : "\(model.playlist.count) items")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Clear") { model.clearPlaylist() }.buttonStyle(.borderless).font(.caption)
                    .disabled(model.playlist.isEmpty)
            }
            .padding(.horizontal, 10).padding(.vertical, 6)
        }
        .background {
            // ⌘F focuses the search field
            Button("") { searchFocused = true }.keyboardShortcut("f").hidden()
        }
    }

    private var searchField: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
            TextField("Filter tracks", text: $query)
                .textFieldStyle(.plain)
                .focused($searchFocused)
                .onSubmit { if let first = rows.first { model.play(index: first.index) } }
                .onExitCommand { query = ""; searchFocused = false }
            if !query.isEmpty {
                Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain).foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
    }

    private func play(_ ids: Set<Track.ID>) {
        if let id = ids.first, let i = model.playlist.firstIndex(where: { $0.id == id }) { model.play(index: i) }
    }
}

struct PlaylistRow: View {
    let track: Track
    let isCurrent: Bool
    let isPlaying: Bool

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: isCurrent ? (isPlaying ? "speaker.wave.2.fill" : "speaker.fill") : "")
                .frame(width: 16)
                .foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: 1) {
                Text(track.name).lineLimit(1)
                Text(track.folder).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer()
            Text(track.url.pathExtension.uppercased()).font(.caption2).foregroundStyle(.secondary)
        }
        .help(track.url.path)
    }
}

func timeString(_ t: Double) -> String {
    guard t.isFinite, t > 0 else { return "0:00" }
    let s = Int(t.rounded(.down))
    return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, (s / 60) % 60, s % 60) : String(format: "%d:%02d", s / 60, s % 60)
}
