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
                         ? "\(d.name) is a digital output: Dolby Digital bitstreaming is available."
                         : "\(d.name) is not a digital output, so PCM is always used (bitstream would play as noise).")
                        .font(.caption).foregroundStyle(.secondary)
                }
            } header: { Text("Audio Device") }

            Section {
                Toggle("Dolby Digital (AC3) bitstream output", isOn: $model.ac3Enabled)
                Picker("AC3 bitrate", selection: $model.ac3Bitrate) {
                    Text("384 kb/s").tag(384)
                    Text("448 kb/s (DVD)").tag(448)
                    Text("640 kb/s (Blu-ray, best)").tag(640)
                }
                .disabled(!model.ac3Enabled)
                Toggle("Pass native Dolby Digital tracks through untouched", isOn: $model.ac3Passthrough)
                    .disabled(!model.ac3Enabled)
                Toggle("Also encode stereo sources as Dolby Digital", isOn: $model.stereoAsAC3)
                    .disabled(!model.ac3Enabled)
                Text("""
                Multichannel audio (E-AC3, DTS, TrueHD, AAC, FLAC, …) is decoded, downmixed to 5.1 when needed and \
                re-encoded to AC3, then sent as an IEC 61937 bitstream with exclusive access to the device — the same \
                approach Kodi uses with WASAPI exclusive mode. The TV only has to forward it over optical to the amp. \
                Stereo sources play as PCM unless the option above is on; turn it on if your amp drops out when \
                switching between PCM and Dolby Digital.
                """)
                .font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            } header: { Text("Surround") }

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
