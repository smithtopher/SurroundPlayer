import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var model: PlayerModel

    var body: some View {
        Form {
            Section {
                Picker("Output device", selection: $model.deviceUID) {
                    Text("System Default").tag("")
                    ForEach(model.devices) { d in
                        Text(d.name + (d.isHDMI ? " (HDMI)" : "") + " — \(d.channels) ch").tag(d.uid)
                    }
                }
                if let d = model.selectedDevice {
                    Text(d.canBitstream
                         ? "\(d.name) is a digital output: Dolby Digital / DTS bitstreaming is available."
                         : "\(d.name) is not a digital output, so PCM is always used (bitstream would play as noise).")
                        .font(.caption).foregroundStyle(.secondary)
                }
            } header: { Text("Audio Device") }

            Section {
                Picker("Surround output", selection: Binding(get: { model.surroundOutput }, set: { model.surroundOutput = $0 })) {
                    Text("Dolby Digital bitstream (AC3, transcoded)").tag(0)
                    Text("DTS bitstream (transcoded)").tag(1)
                    Text("Multichannel PCM (no transcoding, HDMI to an AV receiver)").tag(2)
                    Text("Off (stereo PCM)").tag(3)
                }
                let mode = model.surroundOutput
                if mode == 0 {
                    Picker("Dolby Digital bitrate", selection: $model.ac3Bitrate) {
                        Text("384 kb/s").tag(384)
                        Text("448 kb/s (DVD)").tag(448)
                        Text("640 kb/s (Blu-ray, best)").tag(640)
                    }
                } else if mode == 1 {
                    Picker("DTS bitrate", selection: $model.dtsBitrate) {
                        Text("1509 kb/s (full rate, best)").tag(1509)
                        Text("754 kb/s (half rate)").tag(754)
                    }
                }
                if mode != 3 {
                    Toggle(mode == 0 ? "Pass native Dolby Digital tracks through untouched"
                                     : "Pass native Dolby Digital and DTS tracks through untouched",
                           isOn: $model.ac3Passthrough)
                }
                if mode == 0 || mode == 1 {
                    Toggle("Also encode stereo sources", isOn: $model.stereoAsAC3)
                        .disabled(model.upmix.enabled)
                        .help(model.upmix.enabled ? "Stereo is upmixed to 5.1 (Stereo Upmix below)" : "")
                    note("""
                    Multichannel audio (E-AC3, DTS-HD, TrueHD, AAC, FLAC, …) is decoded, downmixed to 5.1 when needed and \
                    re-encoded to the chosen format, then sent as an IEC 61937 bitstream with exclusive access to the device — \
                    the same approach Kodi uses with WASAPI exclusive mode. The TV only has to forward it over optical to the amp. \
                    Stereo sources play as PCM unless the option above is on; turn it on if your amp drops out when \
                    switching between PCM and surround.
                    """)
                }
                if mode == 1 {
                    note("""
                    DTS: in this mode native DTS tracks (and the DTS core of DTS-HD) also pass through untouched. \
                    The DTS encoder is FFmpeg's, which is marked experimental and is less refined than its AC3 \
                    encoder. Some TVs don't forward DTS over optical; if the amp goes silent, set the TV's digital \
                    output to Pass-through/Bitstream, or use Dolby Digital.
                    """)
                }
                if mode == 2 {
                    note("""
                    Multichannel PCM: surround is decoded and sent as uncompressed PCM in its own channel layout and \
                    sample rate (up to 7.1, 24-bit/192 kHz), with exclusive access, bit-for-bit, nothing re-encoded. Needs \
                    HDMI straight into an AV receiver (or a TV that passes it on over HDMI eARC): optical/S/PDIF can't \
                    carry it, so a TV feeding an amp over optical downmixes it to stereo. If the device refuses the \
                    layout, the player falls back to stereo PCM.
                    """)
                }
            } header: { Text("Surround") }

            upmixSection

            Section {
                Toggle("Show visualiser for music", isOn: $model.visualizerEnabled)
                Text("A MilkDrop-style feedback visualiser that reacts to the audio actually leaving the speakers. When on, it also replaces the picture of files with a video track. Keys: V toggle, ] / [ next / previous preset, L lock preset, F full screen, A next audio track.")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } header: { Text("Visualiser") }

            Section {
                HStack {
                    Slider(value: $model.avOffsetMs, in: -500...1000, step: 10) { Text("Audio latency") }
                    Text("\(Int(model.avOffsetMs)) ms").monospacedDigit().frame(width: 64, alignment: .trailing)
                }
                Text("If sound arrives after the picture (the amp's decoding delay), raise this to delay the video by the same amount.")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } header: { Text("Sync") }

            Section {
                Text("Surround Player \(Self.version)").font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .frame(width: 520)
        .padding(.vertical, 8)
    }

    // MARK: stereo upmix

    @ViewBuilder private var upmixSection: some View {
        Section {
            Picker("Upmix stereo to 5.1", selection: Binding(get: { model.upmix.mode }, set: { m in
                var u = model.upmix
                u.mode = m
                if m != .custom { u.apply(m) }   // a preset sets the sliders
                model.upmix = u
            })) {
                ForEach(UpmixSettings.Mode.allCases) { Text($0.title).tag($0) }
            }
            note(model.upmix.mode.blurb + (model.upmixAvailable ? ""
                 : " Needs a surround output above (Dolby Digital, DTS or Multichannel PCM)."))
            if model.upmix.enabled {
                Group {
                    Toggle("Also for videos with stereo sound", isOn: up(\.includeVideo, custom: false))
                    slider("Surround level", dbOff(\.surroundDb), -20...6, step: 0.5) { $0 <= -20 ? "Off" : String(format: "%+.1f dB", $0) }
                    slider("Centre focus", up(\.center), 0...1, step: 0.05) {
                        $0 < 0.05 ? "Phantom" : $0 > 0.95 ? "Locked" : String(format: "%.0f%%", $0 * 100)
                    }
                    slider("Front width", up(\.width), 0...1, step: 0.05) {
                        abs($0 - 0.5) < 0.01 ? "As recorded" : String(format: "%.0f%%", $0 * 100)
                    }
                    slider("Subwoofer (LFE)", dbOff(\.lfeDb), -20...6, step: 0.5) { $0 <= -20 ? "Off" : String(format: "%+.1f dB", $0) }
                    DisclosureGroup("Advanced") {
                        Picker("Bass crossover", selection: up(\.crossoverHz)) {
                            ForEach([60, 80, 100, 120, 150, 200, 250], id: \.self) { Text("\($0) Hz").tag($0) }
                        }
                        Picker("Bass to subwoofer", selection: up(\.lfeMove)) {
                            Text("Copy (main speakers keep it)").tag(false)
                            Text("Move (small main speakers)").tag(true)
                        }
                        slider("Bass boost", up(\.bassDb), 0...12, step: 0.5) { $0 < 0.25 ? "Off" : String(format: "+%.1f dB", $0) }
                        slider("Rear delay", up(\.rearDelayMs), 0...30, step: 1) { $0 < 0.5 ? "Off" : String(format: "%.0f ms", $0) }
                        Picker("Rear high-cut", selection: up(\.rearHighcutHz)) {
                            Text("Off").tag(0)
                            ForEach([4000, 6000, 7000, 8000, 10000, 12000, 16000], id: \.self) { Text("\($0 / 1000) kHz").tag($0) }
                        }
                        note("""
                        Crossover: bass below it goes to the subwoofer channel (changing it causes a short dropout). \
                        Most AV receivers already send the speakers' bass to the subwoofer, so a strong LFE level can \
                        double the bass. Rear delay keeps sounds anchored at the front; the high-cut makes the rears \
                        read as room sound rather than as speakers you can point at.
                        """)
                    }
                }
                .disabled(!model.upmixAvailable)
                note("Changes apply while playing. U turns upmix on and off. The output badge shows UPMIX while it is active.")
            }
        } header: { Text("Stereo Upmix") }
    }

    /// A setting's binding; moving it turns a preset into Custom.
    private func up<T>(_ kp: WritableKeyPath<UpmixSettings, T>, custom: Bool = true) -> Binding<T> {
        Binding(get: { model.upmix[keyPath: kp] }, set: { v in
            var u = model.upmix
            u[keyPath: kp] = v
            if custom, u.mode != .off { u.mode = .custom }
            model.upmix = u
        })
    }

    /// dB slider whose left end (-20) means off (-40 in the engine).
    private func dbOff(_ kp: WritableKeyPath<UpmixSettings, Double>) -> Binding<Double> {
        let b = up(kp)
        return Binding(get: { max(-20, b.wrappedValue) }, set: { b.wrappedValue = $0 <= -20 ? -40 : $0 })
    }

    private func slider(_ title: String, _ value: Binding<Double>, _ range: ClosedRange<Double>, step: Double,
                        _ label: @escaping (Double) -> String) -> some View {
        HStack {
            Slider(value: value, in: range, step: step) { Text(title) }
            Text(label(value.wrappedValue)).monospacedDigit().frame(width: 84, alignment: .trailing)
        }
    }

    private func note(_ text: String) -> some View {
        Text(text)
            .font(.caption).foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }

    static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "(development build)"
    }
}
