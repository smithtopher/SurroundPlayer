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
                Toggle("Bitstream surround (Dolby Digital / DTS)", isOn: $model.ac3Enabled)
                Picker("Transcode surround to", selection: $model.bitstreamCodec) {
                    Text("Dolby Digital (AC3)").tag(0)
                    Text("DTS").tag(1)
                }
                .disabled(!model.ac3Enabled)
                if model.bitstreamCodec == 0 {
                    Picker("Dolby Digital bitrate", selection: $model.ac3Bitrate) {
                        Text("384 kb/s").tag(384)
                        Text("448 kb/s (DVD)").tag(448)
                        Text("640 kb/s (Blu-ray, best)").tag(640)
                    }
                    .disabled(!model.ac3Enabled)
                } else {
                    Picker("DTS bitrate", selection: $model.dtsBitrate) {
                        Text("1509 kb/s (full rate, best)").tag(1509)
                        Text("754 kb/s (half rate)").tag(754)
                    }
                    .disabled(!model.ac3Enabled)
                }
                Toggle(model.bitstreamCodec == 0 ? "Pass native Dolby Digital tracks through untouched"
                                                 : "Pass native Dolby Digital and DTS tracks through untouched",
                       isOn: $model.ac3Passthrough)
                    .disabled(!model.ac3Enabled)
                Toggle("Also encode stereo sources", isOn: $model.stereoAsAC3)
                    .disabled(!model.ac3Enabled)
                Text("""
                Multichannel audio (E-AC3, DTS-HD, TrueHD, AAC, FLAC, …) is decoded, downmixed to 5.1 when needed and \
                re-encoded to the chosen format, then sent as an IEC 61937 bitstream with exclusive access to the device — \
                the same approach Kodi uses with WASAPI exclusive mode. The TV only has to forward it over optical to the amp. \
                Stereo sources play as PCM unless the option above is on; turn it on if your amp drops out when \
                switching between PCM and surround.
                """)
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                if model.bitstreamCodec == 1 {
                    Text("""
                    DTS: in this mode native DTS tracks (and the DTS core of DTS-HD) also pass through untouched. \
                    The DTS encoder is FFmpeg's, which is marked experimental and is less refined than its AC3 \
                    encoder. Some TVs don't forward DTS over optical; if the amp goes silent, set the TV's digital \
                    output to Pass-through/Bitstream, or use Dolby Digital.
                    """)
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                }
            } header: { Text("Surround") }

            Section {
                Toggle("Show visualiser for music", isOn: $model.visualizerEnabled)
                Text("A MilkDrop-style feedback visualiser that reacts to the audio actually leaving the speakers. When on, it also replaces the picture of files with a video track. Keys: V toggle, ] / [ next / previous preset, L lock preset, F full screen.")
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
        }
        .formStyle(.grouped)
        .frame(width: 520)
        .padding(.vertical, 8)
    }
}
