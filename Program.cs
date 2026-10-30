// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Co-Next. Part of Highlight Recorder (obs-host). See LICENSE.
// highlight-recorder の録画プロセス。インストール済みの OBS Studio の libobs で画面と音を取り込み、
// NVENC などでエンコードした MPEG-TS を FFmpeg に渡して 2 秒ごとの .ts（seg_%04d.ts / segments.csv）に区切る。
// アプリ（src/main/ring-recorder.js）はこの区切りを直前録画のバッファとして使う。
//
// 映像: シーンの下にモニターのキャプチャ、上にゲームキャプチャ（OBS 公式の署名付きフック）を重ねる。
//       ゲームのフックがつながっている間はモニター側を隠す（ゲームの画面だけになる）。
// 音:   PC の音は "game"（ゲームキャプチャの音声。ゲームがないときは PC 全体）か "all"（PC 全体）。
//       マイクは名前で選び、音量・ノイズ抑制をかける。separate なら トラック 1 = 全部、2 = PC、3 = マイク。
//
// 使い方: obs-host <設定の JSON>（Config を参照）
// 標準入力: 1 行 1 つの JSON 命令。{"cmd":"target","exe":"Discovery.exe"}（null でゲームなし）。閉じたら止めて終わる
// 標準出力: 1 行 1 つの JSON。{"event":"started"} / {"event":"hooked","exe":...} / {"event":"unhooked"} /
//           {"event":"mic","name":...} / {"event":"lagging","lagged":n,"total":n} / {"event":"error","message":...}
// 標準エラー: OBS のログ（警告以上と、ゲームキャプチャの状態）

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ObsHost;

var config = JsonSerializer.Deserialize<Config>(args[0], new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var stdoutLock = new object();
void Emit(object e)
{
    lock (stdoutLock) { Console.Out.WriteLine(JsonSerializer.Serialize(e)); Console.Out.Flush(); }
}

var bin = Path.Combine(config.ObsDir, "bin", "64bit");
Obs.SetDllDirectory(bin);
// win-capture はフック（graphics-hook64.dll）を "..\..\data\obs-plugins\win-capture" から探す（OBS 本体の作業フォルダが前提）
Environment.CurrentDirectory = bin;
NativeLibrary.SetDllImportResolver(typeof(Obs).Assembly, (name, _, _) =>
    name == "obs.dll" ? NativeLibrary.Load(Path.Combine(bin, "obs.dll")) : IntPtr.Zero);

Obs.LogHandler logHandler = (level, msg, va, _) =>
{
    // 200 = 警告、300 = 情報。情報はゲームキャプチャの状態だけ流す
    if (level > 300) return;
    var text = Obs.FormatLog(msg, va);
    if (level <= 200 || text.StartsWith("[game-capture")) Console.Error.WriteLine(text);
};
Obs.base_set_log_handler(logHandler, IntPtr.Zero);

string Fwd(string p) => p.Replace('\\', '/');

// プラグインが書く設定（win-capture の互換リストなど）は一時フォルダに置く
var moduleConfig = Fwd(Path.Combine(Path.GetTempPath(), "highlight-recorder-obs"));
if (!Obs.obs_startup("en-US", moduleConfig, IntPtr.Zero)) return Fail("obs_startup failed");
Obs.obs_add_data_path(Fwd(Path.Combine(config.ObsDir, "data", "libobs")) + "/");

var gm = Marshal.StringToHGlobalAnsi("libobs-d3d11");
var ovi = new ObsVideoInfo
{
    GraphicsModule = gm, FpsNum = (uint)config.Fps, FpsDen = 1,
    BaseWidth = (uint)config.BaseWidth, BaseHeight = (uint)config.BaseHeight,
    OutputWidth = (uint)config.OutputWidth, OutputHeight = (uint)config.OutputHeight,
    OutputFormat = Obs.VIDEO_FORMAT_NV12, GpuConversion = true,
    Colorspace = Obs.VIDEO_CS_709, Range = Obs.VIDEO_RANGE_PARTIAL, ScaleType = Obs.OBS_SCALE_BICUBIC,
};
if (Obs.obs_reset_video(ref ovi) != Obs.OBS_VIDEO_SUCCESS) return Fail("could not start OBS video (obs_reset_video)");
var oai = new ObsAudioInfo { SamplesPerSec = 48000, Speakers = Obs.SPEAKERS_STEREO };
if (!Obs.obs_reset_audio(ref oai)) return Fail("could not start OBS audio (obs_reset_audio)");
// HDR のゲーム・モニターを SDR にするときの白の明るさ（Windows の「SDR コンテンツの明るさ」に合わせる）
Obs.obs_set_video_levels(config.SdrWhiteNits, 1000f);

// OBS の画面を前提にしたプラグインは読み込まない（frontend の関数を呼んで落ちる）
foreach (var module in new[] { "win-capture", "win-wasapi", "obs-ffmpeg", "obs-filters" })
{
    var dll = Fwd(Path.Combine(config.ObsDir, "obs-plugins", "64bit", module + ".dll"));
    var data = Fwd(Path.Combine(config.ObsDir, "data", "obs-plugins", module)) + "/";
    int r = Obs.obs_open_module(out var m, dll, data);
    if (r != 0 || !Obs.obs_init_module(m)) return Fail($"could not load OBS module {module} ({r})");
}
Obs.obs_post_load_modules();

// ---- 映像 ----
var scene = Obs.obs_scene_create("scene");
var canvas = new Vec2 { X = config.BaseWidth, Y = config.BaseHeight };
IntPtr AddFitted(IntPtr source)
{
    var item = Obs.obs_scene_add(scene, source);
    Obs.obs_sceneitem_set_bounds_type(item, Obs.OBS_BOUNDS_SCALE_INNER);
    Obs.obs_sceneitem_set_bounds_alignment(item, Obs.OBS_ALIGN_CENTER);
    Obs.obs_sceneitem_set_bounds(item, ref canvas);
    return item;
}

// モニターは位置で選ぶ（OBS の一覧の表示名は「名前: 幅x高さ @ x,y」）
var monitorId = Obs.ListItems("monitor_capture", "monitor_id")
    .FirstOrDefault(i => i.Name.Contains($"@ {config.Monitor.X},{config.Monitor.Y}")).Value;
var monitor = Obs.CreateSource("monitor_capture", "monitor", d =>
{
    if (monitorId != null) Obs.obs_data_set_string(d, "monitor_id", monitorId);
    Obs.obs_data_set_bool(d, "capture_cursor", true);
});
var monitorItem = AddFitted(monitor);

bool gameAudio = config.Audio.Pc == "game";
var game = Obs.CreateSource("game_capture", "game", d =>
{
    Obs.obs_data_set_string(d, "capture_mode", "window");
    Obs.obs_data_set_int(d, "priority", 2); // WINDOW_PRIORITY_EXE
    Obs.obs_data_set_bool(d, "capture_cursor", false);
    Obs.obs_data_set_bool(d, "capture_audio", gameAudio);
});
AddFitted(game);
Obs.obs_set_output_source(0, Obs.obs_scene_get_source(scene));

// ---- 音 ----
bool separate = config.Audio.Separate && config.Audio.Pc != null && config.Audio.Mic != null;
uint pcMixers = separate ? 0b011u : 1u;
uint micMixers = separate ? 0b101u : 1u;
IntPtr desktop = IntPtr.Zero, mic = IntPtr.Zero;
uint channel = 1;
if (config.Audio.Pc != null)
{
    desktop = Obs.CreateSource("wasapi_output_capture", "desktop", d => Obs.obs_data_set_string(d, "device_id", "default"));
    Obs.obs_source_set_audio_mixers(desktop, pcMixers);
    Obs.obs_set_output_source(channel++, desktop);
    Obs.obs_source_set_audio_mixers(game, pcMixers);
}
else
{
    Obs.obs_source_set_audio_mixers(game, 0);
}
if (config.Audio.Mic != null)
{
    var wanted = config.Audio.Mic.Name;
    string? deviceId = wanted == "default" ? "default" : null;
    string? matched = deviceId;
    if (deviceId == null)
    {
        // ブラウザ側の名前は末尾に "(046d:0825)" のような USB の番号が付くことがあるので、前方一致も見る
        foreach (var (name, value) in Obs.ListItems("wasapi_input_capture", "device_id"))
        {
            if (value == "default" || name.Length == 0) continue;
            if (wanted == name || wanted.StartsWith(name + " (") || name.StartsWith(wanted)) { deviceId = value; matched = name; break; }
        }
    }
    Emit(new { @event = "mic", name = matched });
    if (deviceId != null)
    {
        mic = Obs.CreateSource("wasapi_input_capture", "mic", d => Obs.obs_data_set_string(d, "device_id", deviceId));
        Obs.obs_source_set_volume(mic, (float)config.Audio.Mic.Volume);
        Obs.obs_source_set_audio_mixers(mic, micMixers);
        if (config.Audio.Mic.NoiseSuppression)
        {
            var filter = Obs.CreateSource("noise_suppress_filter", "noise", d => Obs.obs_data_set_string(d, "method", "rnnoise"));
            Obs.obs_source_filter_add(mic, filter);
            Obs.obs_source_release(filter);
        }
        Obs.obs_set_output_source(channel++, mic);
    }
}

// ---- 出力: MPEG-TS を TCP で FFmpeg に渡し、2 秒ごとに区切らせる ----
// （OBS の FFmpeg 出力は segment 形式だとストリームを作らない）
int port = FreePort();
var segmenter = new Process
{
    StartInfo = new ProcessStartInfo(config.Ffmpeg)
    {
        UseShellExecute = false,
        RedirectStandardError = true,
        CreateNoWindow = true,
    },
};
foreach (var a in new[]
{
    "-hide_banner", "-loglevel", "error", "-y", "-f", "mpegts", "-i", $"tcp://127.0.0.1:{port}?listen=1",
    "-map", "0", "-c", "copy",
    "-f", "segment", "-segment_time", config.SegmentSec.ToString(System.Globalization.CultureInfo.InvariantCulture),
    "-segment_wrap", config.Wrap.ToString(),
    "-segment_list", Path.Combine(config.OutDir, "segments.csv"), "-segment_list_type", "csv",
    Path.Combine(config.OutDir, "seg_%04d.ts"),
}) segmenter.StartInfo.ArgumentList.Add(a);
segmenter.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine($"[segmenter] {e.Data}"); };
segmenter.Start();
segmenter.BeginErrorReadLine();
Thread.Sleep(300); // FFmpeg が待ち受けを始めるまで

int gop = (int)Math.Round(config.Fps * config.SegmentSec);
var outSettings = Obs.Data(d =>
{
    Obs.obs_data_set_string(d, "url", $"tcp://127.0.0.1:{port}");
    Obs.obs_data_set_string(d, "format_name", "mpegts");
    Obs.obs_data_set_string(d, "video_encoder", config.Encoder);
    Obs.obs_data_set_int(d, "video_encoder_id", 27); // AV_CODEC_ID_H264
    Obs.obs_data_set_string(d, "video_settings", config.EncoderSettings);
    Obs.obs_data_set_int(d, "gop_size", gop);
    Obs.obs_data_set_string(d, "audio_encoder", "aac");
    Obs.obs_data_set_int(d, "audio_encoder_id", 86018); // AV_CODEC_ID_AAC
    Obs.obs_data_set_int(d, "audio_bitrate", 160);
});
var output = Obs.obs_output_create("ffmpeg_output", "ring", outSettings, IntPtr.Zero);
Obs.obs_data_release(outSettings);
Obs.obs_output_set_mixers(output, separate ? 0b111u : 1u);
if (!Obs.obs_output_start(output))
{
    var err = Obs.Str(Obs.obs_output_get_last_error(output));
    segmenter.Kill();
    return Fail($"could not start OBS output: {err}");
}
Emit(new { @event = "started" });

// ---- 命令（標準入力）と見張り ----
string? target = config.Target;
string? windowSpec = null;
bool hooked = false;
bool stopping = false;
var commandThread = new Thread(() =>
{
    string? line;
    while ((line = Console.In.ReadLine()) != null)
    {
        try
        {
            var cmd = JsonNode.Parse(line);
            if ((string?)cmd?["cmd"] == "target") lock (stdoutLock) target = (string?)cmd["exe"];
        }
        catch (Exception e) { Console.Error.WriteLine($"bad command: {e.Message}"); }
    }
    stopping = true; // 標準入力が閉じた（アプリが止めた・落ちた）
}) { IsBackground = true };
commandThread.Start();

uint lastLagged = Obs.obs_get_lagged_frames();
var lagCheck = Stopwatch.StartNew();
var sinceStart = Stopwatch.StartNew();
bool wasActive = false;
int tick = 0;
int exitCode = 0;
while (!stopping)
{
    Thread.Sleep(250);
    // 出力は別スレッドでつながるので、動き出すまで少し待つ
    bool active = Obs.obs_output_active(output);
    wasActive |= active;
    if (segmenter.HasExited || (!active && (wasActive || sinceStart.Elapsed.TotalSeconds > 10)))
    {
        Emit(new { @event = "error", message = segmenter.HasExited ? "segmenter exited" : "OBS output stopped" });
        exitCode = 2;
        break;
    }

    string? want;
    lock (stdoutLock) want = target;
    // ゲームのウィンドウを 2 秒ごとに探し直す（起動の途中で作り直すゲームがある）
    if (tick++ % 8 == 0)
    {
        var spec = want == null ? null : GameWindow.Find(want);
        if (spec != windowSpec && (spec != null || want == null))
        {
            windowSpec = spec;
            var s = Obs.Data(d => Obs.obs_data_set_string(d, "window", spec ?? ""));
            Obs.obs_source_update(game, s);
            Obs.obs_data_release(s);
        }
    }

    bool nowHooked = want != null && Obs.obs_source_get_width(game) > 0;
    if (nowHooked != hooked)
    {
        hooked = nowHooked;
        Obs.obs_sceneitem_set_visible(monitorItem, !hooked);
        Emit(new { @event = hooked ? "hooked" : "unhooked", exe = want });
    }
    // ゲームの音を録るときは、ゲームの画面を取れている間だけ PC 全体の音を止める（二重にならないように）
    if (desktop != IntPtr.Zero && gameAudio) Obs.obs_source_set_muted(desktop, hooked);

    if (lagCheck.Elapsed.TotalSeconds >= 30)
    {
        uint lagged = Obs.obs_get_lagged_frames();
        if (lagged > lastLagged) Emit(new { @event = "lagging", lagged = lagged - lastLagged, total = Obs.obs_get_total_frames() });
        lastLagged = lagged;
        lagCheck.Restart();
    }
}

// ---- 後片付け ----
Obs.obs_output_stop(output);
for (int i = 0; i < 50 && Obs.obs_output_active(output); i++) Thread.Sleep(100);
Obs.obs_output_release(output);
for (uint c = 0; c < channel; c++) Obs.obs_set_output_source(c, IntPtr.Zero);
Obs.obs_scene_release(scene);
// ゲーム・モニターのソースはシーンと一緒に片付く（ここで手放すと二重に解放される）。残りは obs_shutdown が片付ける
foreach (var s in new[] { desktop, mic }) if (s != IntPtr.Zero) Obs.obs_source_release(s);
if (!segmenter.WaitForExit(5000)) segmenter.Kill();
Obs.obs_shutdown();
GC.KeepAlive(logHandler);
return exitCode;

int Fail(string message)
{
    Emit(new { @event = "error", message });
    return 1;
}

static int FreePort()
{
    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l.Start();
    int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

record MonitorPos(int X, int Y);
record MicConfig(string Name, double Volume, bool NoiseSuppression);
record AudioConfig(string? Pc, MicConfig? Mic, bool Separate);
record Config(
    string ObsDir, string Ffmpeg, string OutDir, double SegmentSec, int Wrap,
    int Fps, int BaseWidth, int BaseHeight, int OutputWidth, int OutputHeight,
    MonitorPos Monitor, float SdrWhiteNits, string Encoder, string EncoderSettings,
    string? Target, AudioConfig Audio);
