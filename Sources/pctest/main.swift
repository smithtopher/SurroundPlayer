// pctest: command-line harness for PlayerCore.
//   pctest devices
//   pctest dump <media> <out.raw> [--pcm] [--dts [--dts-bitrate 1509|754]] [--stereo-ac3] [--bitrate N] [--no-passthrough]
//   pctest play <media> [--device UID] [--pcm] [--seconds N] [--seek S]
import Foundation
import PlayerCore

setvbuf(stdout, nil, _IONBF, 0)
let args = CommandLine.arguments
func flag(_ n: String) -> Bool { args.contains(n) }
func value(_ n: String) -> String? {
    guard let i = args.firstIndex(of: n), i + 1 < args.count else { return nil }
    return args[i + 1]
}
func str<T>(_ t: T) -> String {
    withUnsafeBytes(of: t) { raw in String(cString: raw.bindMemory(to: CChar.self).baseAddress!) }
}

guard args.count >= 2 else {
    print("usage: pctest devices | dump <media> <out> | play <media> [--device UID]")
    exit(1)
}

if args[1] == "devices" {
    var devs = [PCDevice](repeating: PCDevice(), count: 64)
    let n = Int(pc_list_output_devices(&devs, 64))
    for d in devs.prefix(n) {
        print("\(str(d.name))  uid=\(str(d.uid))  ch=\(d.output_channels) hdmi=\(d.is_hdmi) ac3fmt=\(d.has_compressed_formats) default=\(d.is_default)")
    }
    exit(0)
}

guard args.count >= 3 else { print("missing media path"); exit(1) }
let media = args[2]

final class Ctx { var eof = false; var frames = 0 }
let ctx = Ctx()
let player = pc_player_create()!
let ctxPtr = Unmanaged.passUnretained(ctx).toOpaque()
pc_player_set_callbacks(player, { c, _, _ in
    Unmanaged<Ctx>.fromOpaque(c!).takeUnretainedValue().frames += 1
}, { c, ev, _ in
    if ev == Int32(PC_EVENT_EOF.rawValue) { Unmanaged<Ctx>.fromOpaque(c!).takeUnretainedValue().eof = true }
}, ctxPtr)

var cfg = PCConfig()
cfg.bitstream_enabled = flag("--pcm") ? 0 : 1
cfg.bitstream_codec = Int32(flag("--dts") ? PC_CODEC_DTS.rawValue : PC_CODEC_AC3.rawValue)
cfg.dts_bitrate_kbps = Int32(value("--dts-bitrate") ?? "1509") ?? 1509
cfg.ac3_bitrate_kbps = Int32(value("--bitrate") ?? "640") ?? 640
cfg.stereo_bitstream = flag("--stereo-ac3") ? 1 : 0
cfg.passthrough = flag("--no-passthrough") ? 0 : 1
cfg.audio_track = Int32(value("--track") ?? "-1") ?? -1
cfg.start_position = Double(value("--start") ?? "0") ?? 0

if flag("--vis") { pc_player_set_vis_enabled(player, 1) }
let dumpPath = args[1] == "dump" ? (args.count > 3 ? args[3] : "out.raw") : nil
let uid = value("--device")
var err = [CChar](repeating: 0, count: 512)

let rc: Int32 = (uid ?? "").withCString { uidp in
    (dumpPath ?? "").withCString { dp in
        cfg.device_uid = uid == nil ? nil : uidp
        cfg.dump_path = dumpPath == nil ? nil : dp
        return pc_player_open(player, media, &cfg, 0, &err, 512)
    }
}
if rc != 0 { print("open failed: \(String(cString: err))"); exit(2) }

var info = PCInfo()
pc_player_get_info(player, &info)
print("source: \(str(info.source_audio))")
print("output: \(str(info.output))")
print("video:  \(str(info.video))  duration: \(info.duration)")
for i in 0..<pc_player_audio_track_count(player) {
    var b = [CChar](repeating: 0, count: 256)
    pc_player_audio_track_name(player, i, &b, 256)
    print("  track \(String(cString: b))\(i == pc_player_current_audio_track(player) ? "  *" : "")")
}

let limit = Double(value("--seconds") ?? "1e9") ?? 1e9
let seekAt = value("--seek").flatMap(Double.init)
let start = Date()
var sought = false
var visBuf = [Float](repeating: 0, count: 4096)
while !ctx.eof && Date().timeIntervalSince(start) < limit {
    usleep(flag("--vis") ? 40_000 : 250_000)
    if flag("--vis") {
        let n = Int(pc_player_vis_samples(player, &visBuf, 2048))
        var rms: Float = 0, zc = 0
        for i in 0..<n { rms += visBuf[i * 2] * visBuf[i * 2]; if i > 0 && (visBuf[i * 2] < 0) != (visBuf[i * 2 - 2] < 0) { zc += 1 } }
        print(String(format: "vis frames=%d rmsL=%.4f zcHz=%.0f pos=%.2f", n, n > 0 ? sqrt(rms / Float(n)) : 0, Double(zc) / 2 / (Double(max(n, 1)) / 48000), pc_player_position(player)))
    }
    if let s = seekAt, !sought, Date().timeIntervalSince(start) > 2 { pc_player_seek(player, s); sought = true; print("seek -> \(s)") }
    if dumpPath == nil { print(String(format: "pos %.3f  frames %d", pc_player_position(player), ctx.frames)) }
    if flag("--debug") { pc_player_debug(player) }
    if let pa = value("--pause-at").flatMap(Double.init), abs(Date().timeIntervalSince(start) - pa) < 0.13 { pc_player_set_paused(player, 1); print("pause") }
    if let pa = value("--pause-at").flatMap(Double.init), abs(Date().timeIntervalSince(start) - pa - 1.5) < 0.13 { pc_player_set_paused(player, 0); print("resume") }
}
pc_player_get_info(player, &info); print("video now: \(str(info.video))")
print(ctx.eof ? "EOF" : "stopped", "frames shown: \(ctx.frames)")
if let next = value("--then") {
    ctx.eof = false
    let t0 = Date()
    let rc2 = (uid ?? "").withCString { uidp -> Int32 in
        cfg.device_uid = uid == nil ? nil : uidp
        cfg.dump_path = nil
        cfg.start_position = 0
        return pc_player_open(player, next, &cfg, 0, &err, 512)
    }
    print(String(format: "reopen took %.0f ms rc=%d", Date().timeIntervalSince(t0) * 1000, rc2))
    pc_player_get_info(player, &info)
    print("output: \(str(info.output))")
    while !ctx.eof { usleep(250_000) }
    print("EOF 2")
}
pc_player_close(player)
pc_player_destroy(player)
