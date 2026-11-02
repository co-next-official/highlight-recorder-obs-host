// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Co-Next. Part of Highlight Recorder (obs-host). See LICENSE.
// libobs（OBS Studio の obs.dll）の C API のうち、使うものだけを宣言する。
// 構造体・列挙の並びは OBS 32.2.2 の libobs/obs.h・obs-properties.h・media-io/video-io.h に合わせている。

using System.Runtime.InteropServices;
using System.Text;

namespace ObsHost;

[StructLayout(LayoutKind.Sequential)]
struct ObsVideoInfo
{
    public IntPtr GraphicsModule; // const char*
    public uint FpsNum, FpsDen;
    public uint BaseWidth, BaseHeight;
    public uint OutputWidth, OutputHeight;
    public int OutputFormat; // enum video_format
    public uint Adapter;
    [MarshalAs(UnmanagedType.U1)] public bool GpuConversion;
    public int Colorspace; // enum video_colorspace
    public int Range; // enum video_range_type
    public int ScaleType; // enum obs_scale_type
}

[StructLayout(LayoutKind.Sequential)]
struct ObsAudioInfo
{
    public uint SamplesPerSec;
    public int Speakers; // enum speaker_layout
}

[StructLayout(LayoutKind.Sequential)]
struct Vec2
{
    public float X, Y;
}

static class Obs
{
    const string Lib = "obs.dll";
    const UnmanagedType Utf8 = UnmanagedType.LPUTF8Str;

    public const int VIDEO_FORMAT_NV12 = 2;
    public const int VIDEO_CS_709 = 2;
    public const int VIDEO_RANGE_PARTIAL = 1;
    public const int OBS_SCALE_BICUBIC = 2;
    public const int SPEAKERS_STEREO = 2;
    public const int OBS_VIDEO_SUCCESS = 0;
    public const int OBS_BOUNDS_SCALE_INNER = 2;
    public const uint OBS_ALIGN_CENTER = 0;

    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool obs_startup([MarshalAs(Utf8)] string locale, [MarshalAs(Utf8)] string? moduleConfigPath, IntPtr store);
    [DllImport(Lib)] public static extern void obs_shutdown();
    [DllImport(Lib)] public static extern void obs_add_data_path([MarshalAs(Utf8)] string path);
    [DllImport(Lib)] public static extern int obs_open_module(out IntPtr module, [MarshalAs(Utf8)] string path, [MarshalAs(Utf8)] string dataPath);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool obs_init_module(IntPtr module);
    [DllImport(Lib)] public static extern void obs_post_load_modules();
    [DllImport(Lib)] public static extern int obs_reset_video(ref ObsVideoInfo ovi);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool obs_reset_audio(ref ObsAudioInfo oai);
    [DllImport(Lib)] public static extern void obs_set_video_levels(float sdrWhite, float hdrNominalPeak);
    [DllImport(Lib)] public static extern uint obs_get_lagged_frames();
    [DllImport(Lib)] public static extern uint obs_get_total_frames();

    [DllImport(Lib)] public static extern IntPtr obs_data_create();
    [DllImport(Lib)] public static extern void obs_data_release(IntPtr data);
    [DllImport(Lib)] public static extern void obs_data_set_string(IntPtr data, [MarshalAs(Utf8)] string name, [MarshalAs(Utf8)] string val);
    [DllImport(Lib)] public static extern void obs_data_set_int(IntPtr data, [MarshalAs(Utf8)] string name, long val);
    [DllImport(Lib)] public static extern void obs_data_set_bool(IntPtr data, [MarshalAs(Utf8)] string name, [MarshalAs(UnmanagedType.U1)] bool val);

    [DllImport(Lib)] public static extern IntPtr obs_source_create([MarshalAs(Utf8)] string id, [MarshalAs(Utf8)] string name, IntPtr settings, IntPtr hotkeyData);
    [DllImport(Lib)] public static extern void obs_source_release(IntPtr source);
    [DllImport(Lib)] public static extern void obs_source_update(IntPtr source, IntPtr settings);
    [DllImport(Lib)] public static extern uint obs_source_get_width(IntPtr source);
    [DllImport(Lib)] public static extern uint obs_source_get_height(IntPtr source);
    [DllImport(Lib)] public static extern void obs_source_set_volume(IntPtr source, float volume);
    [DllImport(Lib)] public static extern void obs_source_set_muted(IntPtr source, [MarshalAs(UnmanagedType.U1)] bool muted);
    [DllImport(Lib)] public static extern void obs_source_set_audio_mixers(IntPtr source, uint mixers);
    [DllImport(Lib)] public static extern void obs_source_filter_add(IntPtr source, IntPtr filter);
    [DllImport(Lib)] public static extern void obs_set_output_source(uint channel, IntPtr source);

    [DllImport(Lib)] public static extern IntPtr obs_scene_create([MarshalAs(Utf8)] string name);
    [DllImport(Lib)] public static extern void obs_scene_release(IntPtr scene);
    [DllImport(Lib)] public static extern IntPtr obs_scene_get_source(IntPtr scene);
    [DllImport(Lib)] public static extern IntPtr obs_scene_add(IntPtr scene, IntPtr source);
    [DllImport(Lib)] public static extern void obs_sceneitem_set_bounds_type(IntPtr item, int type);
    [DllImport(Lib)] public static extern void obs_sceneitem_set_bounds_alignment(IntPtr item, uint alignment);
    [DllImport(Lib)] public static extern void obs_sceneitem_set_bounds(IntPtr item, ref Vec2 bounds);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool obs_sceneitem_set_visible(IntPtr item, [MarshalAs(UnmanagedType.U1)] bool visible);

    [DllImport(Lib)] public static extern IntPtr obs_get_source_properties([MarshalAs(Utf8)] string id);
    [DllImport(Lib)] public static extern void obs_properties_destroy(IntPtr props);
    [DllImport(Lib)] public static extern IntPtr obs_properties_get(IntPtr props, [MarshalAs(Utf8)] string name);
    [DllImport(Lib)] public static extern nuint obs_property_list_item_count(IntPtr prop);
    [DllImport(Lib)] public static extern IntPtr obs_property_list_item_name(IntPtr prop, nuint idx);
    [DllImport(Lib)] public static extern IntPtr obs_property_list_item_string(IntPtr prop, nuint idx);

    [DllImport(Lib)] public static extern IntPtr obs_output_create([MarshalAs(Utf8)] string id, [MarshalAs(Utf8)] string name, IntPtr settings, IntPtr hotkeyData);
    [DllImport(Lib)] public static extern void obs_output_release(IntPtr output);
    [DllImport(Lib)] public static extern void obs_output_set_mixers(IntPtr output, nuint mixers);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool obs_output_start(IntPtr output);
    [DllImport(Lib)] public static extern void obs_output_stop(IntPtr output);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool obs_output_active(IntPtr output);
    [DllImport(Lib)] public static extern IntPtr obs_output_get_last_error(IntPtr output);
    [DllImport(Lib)] public static extern IntPtr obs_output_get_signal_handler(IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void SignalCallback(IntPtr data, IntPtr calldata);
    [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern void signal_handler_connect(IntPtr handler, string signal, SignalCallback callback, IntPtr data);
    [DllImport(Lib)] public static extern int obs_output_get_frames_dropped(IntPtr output);

    // ログ: void (*log_handler_t)(int lvl, const char *msg, va_list args, void *p)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogHandler(int level, IntPtr msg, IntPtr args, IntPtr param);
    [DllImport(Lib)] public static extern void base_set_log_handler(LogHandler handler, IntPtr param);

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int _vsnprintf(byte[] buffer, nuint count, IntPtr format, IntPtr args);

    public static string FormatLog(IntPtr msg, IntPtr args)
    {
        var buf = new byte[4096];
        int n = _vsnprintf(buf, (nuint)(buf.Length - 1), msg, args);
        if (n < 0 || n >= buf.Length) n = Array.IndexOf(buf, (byte)0) is var z and >= 0 ? z : buf.Length - 1;
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    public static string? Str(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    public static IntPtr Data(Action<IntPtr> fill)
    {
        var d = obs_data_create();
        fill(d);
        return d;
    }

    // 設定を作ってソースを作る（設定はすぐ手放す）
    public static IntPtr CreateSource(string id, string name, Action<IntPtr>? fill = null)
    {
        var settings = fill == null ? IntPtr.Zero : Data(fill);
        var source = obs_source_create(id, name, settings, IntPtr.Zero);
        if (settings != IntPtr.Zero) obs_data_release(settings);
        return source;
    }

    // ソースの設定の選択肢（一覧）を { 表示名, 値 } で返す
    public static List<(string Name, string Value)> ListItems(string sourceId, string property)
    {
        var items = new List<(string, string)>();
        var props = obs_get_source_properties(sourceId);
        if (props == IntPtr.Zero) return items;
        var prop = obs_properties_get(props, property);
        if (prop != IntPtr.Zero)
        {
            nuint n = obs_property_list_item_count(prop);
            for (nuint i = 0; i < n; i++)
                items.Add((Str(obs_property_list_item_name(prop, i)) ?? "", Str(obs_property_list_item_string(prop, i)) ?? ""));
        }
        obs_properties_destroy(props);
        return items;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string path);
}
