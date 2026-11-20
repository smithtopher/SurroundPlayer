// P/Invoke bindings for playercore.dll (Sources/PlayerCore/include/playercore.h).
// Struct layouts must match the C header exactly.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SurroundPlayer;

internal static unsafe class Native
{
    private const string Dll = "playercore.dll";

    public const int PC_MODE_PCM = 0, PC_MODE_TRANSCODE = 1, PC_MODE_PASSTHROUGH = 2;
    public const int PC_CODEC_AC3 = 0, PC_CODEC_DTS = 1;
    public const int PC_EVENT_EOF = 1, PC_EVENT_ERROR = 2, PC_EVENT_NEXT = 3;
    public const int PC_FRAME_D3D11 = 1, PC_FRAME_BGRA = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct PCConfig
    {
        public IntPtr device_uid;       // UTF-8, NULL = default (the engine copies it)
        public int bitstream_enabled;
        public int bitstream_codec;
        public int ac3_bitrate_kbps;
        public int dts_bitrate_kbps;
        public int stereo_bitstream;
        public int passthrough;
        public int audio_track;
        public double start_position;
        public double av_offset;
        public IntPtr dump_path;
        public int multichannel_pcm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PCInfo
    {
        public fixed byte title[256];
        public fixed byte artist[256];
        public fixed byte album[256];
        public fixed byte source_audio[256];
        public fixed byte output[512];
        public fixed byte video[256];
        public double duration;
        public int has_video;
        public int mode;
        public fixed byte bitstream_codec[32];
        public int video_width, video_height;
        public int source_rate, source_bits, source_bitrate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PCDevice
    {
        public fixed byte uid[256];
        public fixed byte name[256];
        public int output_channels;
        public int is_hdmi;
        public int is_digital;
        public int has_compressed_formats;
        public int is_default;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PCFrameInfo
    {
        public int kind;
        public int width, height;
        public int sar_num, sar_den;
        public int color_space, color_primaries, color_trc, color_range;
        public IntPtr texture;
        public int array_index;
        public int dxgi_format;
        public int shader_resource;
        public IntPtr bgra;
        public int stride;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VideoCallback(IntPtr ctx, IntPtr frame, double pts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallback(IntPtr ctx, int ev, IntPtr message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DeviceChangeCallback(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr pc_player_create();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_destroy(IntPtr p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pc_player_set_callbacks(IntPtr p, VideoCallback? vcb, EventCallback? ecb, IntPtr ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pc_player_open(IntPtr p, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref PCConfig cfg,
                                            int paused, byte* err, int errlen);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_close(IntPtr p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_set_paused(IntPtr p, int paused);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_seek(IntPtr p, double seconds);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern double pc_player_position(IntPtr p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_set_av_offset(IntPtr p, double seconds);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_get_info(IntPtr p, out PCInfo info);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_audio_track_count(IntPtr p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_audio_track_name(IntPtr p, int index, byte* buf, int len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_current_audio_track(IntPtr p);

    // stereo upmix (mirrors PCUpmix in playercore.h)
    [StructLayout(LayoutKind.Sequential)]
    public struct PCUpmix
    {
        public int enabled, include_video;
        public float surround_db, center, width, lfe_db;
        public int crossover_hz, lfe_move;
        public float bass_db, rear_delay_ms;
        public int rear_highcut_hz;
    }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_set_upmix(IntPtr p, ref PCUpmix u);

    // gapless playback
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_set_next(IntPtr p,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? path);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_next_pending(IntPtr p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_upmix_active(IntPtr p);

    // subtitles
    public const int PC_SUB_NONE = 0, PC_SUB_TEXT = 1, PC_SUB_BITMAP = 2;
    [StructLayout(LayoutKind.Sequential)]
    public struct PCSubtitleState
    {
        public int serial, kind, text_len;
        public int x, y, w, h;
        public int ref_w, ref_h;
    }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_subtitle_track_count(IntPtr p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_subtitle_track_name(IntPtr p, int index, byte* buf, int len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_subtitle_track_language(IntPtr p, int index, byte* buf, int len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_current_subtitle_track(IntPtr p);
    public const int PC_SUBFLAG_DEFAULT = 1, PC_SUBFLAG_FORCED = 2, PC_SUBFLAG_EXTERNAL = 4;
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_subtitle_track_flags(IntPtr p, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_set_subtitle_track(IntPtr p, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_add_subtitle_file(IntPtr p,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string? name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_subtitle_state(IntPtr p, out PCSubtitleState st);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_subtitle_text(IntPtr p, byte* buf, int len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_subtitle_bitmap(IntPtr p, byte* bgra, int len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr pc_player_cover_art(IntPtr p, out int size);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_list_output_devices([Out] PCDevice[] devs, int max);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_set_device_change_callback(DeviceChangeCallback? cb, IntPtr ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_output_lost(IntPtr p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_player_set_vis_enabled(IntPtr p, int enabled);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_player_vis_samples(IntPtr p, float* lr, int frames);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_set_d3d11_device(IntPtr device);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int pc_frame_info(IntPtr frame, out PCFrameInfo info);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_frame_retain(IntPtr frame);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void pc_frame_release(IntPtr frame);

    // Win32 helpers
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int StrCmpLogicalW(string a, string b);
    [DllImport("user32.dll")] public static extern int ShowCursor(bool show);

    public static string Str(byte* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }
}
