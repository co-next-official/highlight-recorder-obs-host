// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Co-Next. Part of Highlight Recorder (obs-host). See LICENSE.
// 画面認識: ゲームの決まった位置に出るマーク（VALORANT のキル表示など）をテンプレート照合で見つける。
//
// OBS がエンコード用に CPU へ持ってきている出力フレーム（NV12）を、数フレームに 1 枚だけ受け取る。
// 受け取ったら、対象ゲームの検知設定（DetectProfile）ごとに Y 面（= グレースケール）の決まった正方形を
// Grid×Grid に縮めて写し、別スレッドで「中央の近く（±Jitter）でテンプレートと比べる」正規化相互相関をかける。
// 正方形の位置と大きさは画面の高さに対する割合なので、解像度が変わっても同じ見え方になる
// （テンプレートは 1080p で 256px 四方を 128×128 に縮めた絵から切り出したもの）。
//
// 判定: 一致度が On を 2 枚続けて超えたら 1 回。Off を下回るまでは数えない。前の 1 回から RefractoryMs の間も数えない
// （演出の途中で一瞬崩れる・スコアボードが重なる、で二重に数えないため）。
// 見つけたら {"event":"detect","type":"kill","exe":...,"score":0.97,"ageMs":180}。Bench のときは 10 秒ごとに {"event":"detect_stats",...}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ObsHost;

// Cx / Cy: 正方形の中心（画面の幅・高さに対する割合）。Size: 一辺（画面の高さに対する割合）
// Templates: P5 形式の PGM（8bit グレー、長方形でよい・同じ設定の中では同じ大きさ）を base64 にしたもの。どれか 1 つに合えばよい
record DetectProfile(string Exe, string Type, double Cx, double Cy, double Size, string[] Templates,
    double On = 0.7, double Off = 0.45, int Jitter = 6, int RefractoryMs = 1000);
record DetectConfig(int Fps, DetectProfile[] Profiles);

sealed unsafe class Detector : IDisposable
{
    const int Grid = 128;

    // struct video_data { uint8_t *data[8]; uint32_t linesize[8]; uint64_t timestamp; }
    [StructLayout(LayoutKind.Sequential)]
    struct VideoData
    {
        public fixed ulong Data[8];
        public fixed uint Linesize[8];
        public ulong Timestamp;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void RawVideoCallback(IntPtr param, IntPtr frame);

    [DllImport("obs.dll")]
    static extern void obs_add_raw_video_callback2(IntPtr conversion, uint frameRateDivisor, RawVideoCallback callback, IntPtr param);
    [DllImport("obs.dll")]
    static extern void obs_remove_raw_video_callback(RawVideoCallback callback, IntPtr param);
    [DllImport("obs.dll")]
    static extern ulong os_gettime_ns(); // フレームの timestamp と同じ時計

    // 検知設定 1 つぶんの状態
    sealed class Probe
    {
        public required DetectProfile Profile;
        public required int X0, Y0, Side; // 出力フレーム上の正方形（画素）
        public required float[][] Templates; // 平均を引いて長さ 1 にしたもの
        public required int TemplateW, TemplateH;
        public readonly byte[] Front = new byte[Grid * Grid], Back = new byte[Grid * Grid];
        public bool Pending; // Front に新しい 1 枚がある
        public ulong FrontTs, BackTs; // そのフレームの時刻（OBS の時計・ns）
        // 判定の状態（判定スレッドだけが触る）
        public int Above;
        public ulong AboveSince; // On を超え始めたフレームの時刻
        public bool Active;
        public long LastHit = long.MinValue / 2;
        public float LastScore, MaxScore;
    }

    readonly RawVideoCallback callback; // OBS が呼ぶ間は手放さない
    readonly Action<object> emit;
    readonly bool bench;
    readonly Probe[] probes;
    volatile Probe[] active = []; // いまの対象ゲームの分
    readonly object swap = new();
    readonly Thread worker;
    readonly AutoResetEvent wake = new(false);
    volatile bool stopping;

    // 計測（Bench のとき 10 秒ごとにまとめて出して戻す）
    long frames, busySkips, cbTicks, cbMax, workTicks, workMax, works;
    readonly Stopwatch interval = Stopwatch.StartNew();

    public Detector(DetectConfig config, int outputWidth, int outputHeight, int fps, string? target, bool bench, Action<object> emit)
    {
        this.emit = emit;
        this.bench = bench;
        probes = config.Profiles.Select(p => MakeProbe(p, outputWidth, outputHeight)).Where(p => p != null).ToArray()!;
        SetTarget(target);

        worker = new Thread(Work) { IsBackground = true, Name = "detector", Priority = ThreadPriority.BelowNormal };
        worker.Start();

        callback = OnFrame;
        uint divisor = (uint)Math.Max(1, (int)Math.Round((double)fps / Math.Max(1, config.Fps)));
        obs_add_raw_video_callback2(IntPtr.Zero, divisor, callback, IntPtr.Zero);
        emit(new
        {
            @event = "detect_started",
            fps = (double)fps / divisor,
            profiles = probes.Select(p => new { exe = p.Profile.Exe, type = p.Profile.Type, x = p.X0, y = p.Y0, side = p.Side, templates = p.Templates.Length, refractoryMs = p.Profile.RefractoryMs }),
        });
    }

    Probe? MakeProbe(DetectProfile p, int width, int height)
    {
        int side = Math.Min((int)Math.Round(p.Size * height), Math.Min(width, height));
        int x0 = Math.Clamp((int)Math.Round(p.Cx * width - side / 2.0), 0, width - side);
        int y0 = Math.Clamp((int)Math.Round(p.Cy * height - side / 2.0), 0, height - side);
        var templates = new List<float[]>();
        int tw = 0, th = 0;
        foreach (var b64 in p.Templates)
        {
            var (w, h, pixels) = ReadPgm(Convert.FromBase64String(b64));
            if ((tw != 0 && (w != tw || h != th)) || w + 2 * p.Jitter > Grid || h + 2 * p.Jitter > Grid)
                throw new Exception($"bad template size {w}x{h} for {p.Exe} {p.Type}");
            (tw, th) = (w, h);
            templates.Add(Normalize(pixels));
        }
        if (templates.Count == 0 || side < 16) return null;
        return new Probe { Profile = p, X0 = x0, Y0 = y0, Side = side, Templates = templates.ToArray(), TemplateW = tw, TemplateH = th };
    }

    public void SetTarget(string? exe)
    {
        active = exe == null ? [] : probes.Where(p => string.Equals(p.Profile.Exe, exe, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    // OBS の映像スレッドで呼ばれる。ここでは正方形を縮めて写すだけにして、すぐ返す
    void OnFrame(IntPtr param, IntPtr framePtr)
    {
        var list = active;
        if (list.Length == 0) return;
        long t0 = Stopwatch.GetTimestamp();
        var frame = (VideoData*)framePtr;
        byte* y = (byte*)frame->Data[0];
        int stride = (int)frame->Linesize[0];
        lock (swap)
        {
            foreach (var p in list)
            {
                fixed (byte* dst = p.Front) Shrink(y, stride, p.X0, p.Y0, p.Side, dst);
                if (p.Pending) busySkips++; // 判定が前の 1 枚をまだ取っていない（上書きする）
                p.Pending = true;
                p.FrontTs = frame->Timestamp;
            }
        }
        wake.Set();
        long dt = Stopwatch.GetTimestamp() - t0;
        Interlocked.Increment(ref frames);
        Interlocked.Add(ref cbTicks, dt);
        if (dt > Interlocked.Read(ref cbMax)) Interlocked.Exchange(ref cbMax, dt);
    }

    // side 四方を Grid 四方に縮める（各マスに入る画素の平均。1080p の 256px なら 2×2 の平均）
    static void Shrink(byte* src, int stride, int x0, int y0, int side, byte* dst)
    {
        for (int r = 0; r < Grid; r++)
        {
            int ya = y0 + r * side / Grid, yb = Math.Max(ya + 1, y0 + (r + 1) * side / Grid);
            for (int c = 0; c < Grid; c++)
            {
                int xa = x0 + c * side / Grid, xb = Math.Max(xa + 1, x0 + (c + 1) * side / Grid);
                int sum = 0;
                for (int yy = ya; yy < yb; yy++)
                {
                    byte* row = src + yy * stride;
                    for (int xx = xa; xx < xb; xx++) sum += row[xx];
                }
                int n = (yb - ya) * (xb - xa);
                dst[r * Grid + c] = (byte)((sum + n / 2) / n);
            }
        }
    }

    void Work()
    {
        while (!stopping)
        {
            wake.WaitOne(1000);
            if (stopping) break;
            foreach (var p in active)
            {
                lock (swap)
                {
                    if (!p.Pending) continue;
                    Buffer.BlockCopy(p.Front, 0, p.Back, 0, p.Front.Length);
                    p.BackTs = p.FrontTs;
                    p.Pending = false;
                }
                long t0 = Stopwatch.GetTimestamp();
                float score = -1;
                foreach (var t in p.Templates) score = Math.Max(score, Match(p.Back, t, p.TemplateW, p.TemplateH, p.Profile.Jitter));
                Judge(p, score);
                long dt = Stopwatch.GetTimestamp() - t0;
                works++;
                workTicks += dt;
                if (dt > workMax) workMax = dt;
            }
            if (bench) MaybeReport();
        }
    }

    void Judge(Probe p, float score)
    {
        p.LastScore = score;
        p.MaxScore = Math.Max(p.MaxScore, score);
        long now = Environment.TickCount64;
        if (p.Active)
        {
            if (score < p.Profile.Off) { p.Active = false; p.Above = 0; }
            return;
        }
        p.Above = score > p.Profile.On ? p.Above + 1 : 0;
        if (p.Above == 1) p.AboveSince = p.BackTs;
        if (p.Above < 2) return;
        p.Active = true;
        if (now - p.LastHit < p.Profile.RefractoryMs) return;
        p.LastHit = now;
        // ageMs: マークが映り始めたフレームから今までの時間（受け取った側が、起きた時刻を now - ageMs で出す）
        double ageMs = Math.Max(0, (os_gettime_ns() - p.AboveSince) / 1e6);
        emit(new { @event = "detect", type = p.Profile.Type, exe = p.Profile.Exe, score = Math.Round(score, 3), ageMs = Math.Round(ageMs) });
    }

    static (int w, int h, byte[] pixels) ReadPgm(byte[] data)
    {
        // "P5" 幅 高さ 最大値 の後に空白 1 文字、その後が画素
        int pos = 0;
        string Token()
        {
            while (pos < data.Length && char.IsWhiteSpace((char)data[pos])) pos++;
            int start = pos;
            while (pos < data.Length && !char.IsWhiteSpace((char)data[pos])) pos++;
            return System.Text.Encoding.ASCII.GetString(data, start, pos - start);
        }
        if (Token() != "P5") throw new Exception("template is not a P5 PGM");
        int w = int.Parse(Token()), h = int.Parse(Token()), max = int.Parse(Token());
        pos++;
        if (max != 255 || data.Length - pos < w * h) throw new Exception("bad PGM template");
        return (w, h, data[pos..(pos + w * h)]);
    }

    static float[] Normalize(byte[] pixels)
    {
        var t = pixels.Select(b => (float)b).ToArray();
        float mean = t.Average();
        double ss = 0;
        for (int i = 0; i < t.Length; i++) { t[i] -= mean; ss += t[i] * t[i]; }
        float norm = (float)Math.Sqrt(ss);
        if (norm < 1e-6) throw new Exception("flat template");
        for (int i = 0; i < t.Length; i++) t[i] /= norm;
        return t;
    }

    // 中央 ±jitter の位置ごとに正規化相互相関をとり、いちばん高い値（-1〜1）を返す
    static float Match(byte[] img, float[] t, int tw, int th, int jitter)
    {
        int cx0 = (Grid - tw) / 2, cy0 = (Grid - th) / 2;
        int n = tw * th;
        float best = -1;
        for (int dy = -jitter; dy <= jitter; dy++)
        {
            for (int dx = -jitter; dx <= jitter; dx++)
            {
                int ox = cx0 + dx, oy = cy0 + dy;
                double sum = 0, sum2 = 0, cross = 0;
                for (int r = 0; r < th; r++)
                {
                    int row = (oy + r) * Grid + ox, trow = r * tw;
                    for (int c = 0; c < tw; c++)
                    {
                        float v = img[row + c];
                        sum += v;
                        sum2 += v * v;
                        cross += v * t[trow + c];
                    }
                }
                double var = sum2 - sum * sum / n;
                if (var < 1e-6) continue;
                // テンプレートは平均 0 なので、画素の平均を引かなくても相互相関は同じ
                float score = (float)(cross / Math.Sqrt(var));
                if (score > best) best = score;
            }
        }
        return best;
    }

    void MaybeReport()
    {
        if (interval.Elapsed.TotalSeconds < 10) return;
        double sec = interval.Elapsed.TotalSeconds;
        double us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;
        long f = Interlocked.Exchange(ref frames, 0);
        long cb = Interlocked.Exchange(ref cbTicks, 0);
        long cbm = Interlocked.Exchange(ref cbMax, 0);
        long skips;
        lock (swap) { skips = busySkips; busySkips = 0; }
        emit(new
        {
            @event = "detect_stats",
            seconds = Math.Round(sec, 1),
            frames = f,
            callbackAvgUs = f > 0 ? Math.Round(us(cb) / f, 1) : 0,
            callbackMaxUs = Math.Round(us(cbm), 1),
            matches = works,
            matchAvgUs = works > 0 ? Math.Round(us(workTicks) / works, 1) : 0,
            matchMaxUs = Math.Round(us(workMax), 1),
            skipped = skips,
            scores = active.Select(p => new { type = p.Profile.Type, last = Math.Round(p.LastScore, 3), max = Math.Round(p.MaxScore, 3) }),
        });
        foreach (var p in active) p.MaxScore = -1;
        works = 0; workTicks = 0; workMax = 0;
        interval.Restart();
    }

    public void Dispose()
    {
        obs_remove_raw_video_callback(callback, IntPtr.Zero);
        stopping = true;
        wake.Set();
        worker.Join(2000);
        GC.KeepAlive(callback);
    }
}
