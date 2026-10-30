// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Co-Next. Part of Highlight Recorder (obs-host). See LICENSE.
// ゲームのウィンドウを実行ファイル名で探し、OBS のゲームキャプチャの "window" 設定の形
// （タイトル:クラス:実行ファイル。# と : は #22 / #3A に置き換える）にする。

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ObsHost;

static class GameWindow
{
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int max);

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }

    const uint GW_OWNER = 4;
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x80;

    // exe（例: Discovery.exe）のいちばん大きい表のウィンドウ。なければ null
    public static string? Find(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        var names = new Dictionary<uint, string>();
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!names.TryGetValue(pid, out var procName))
            {
                try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { procName = ""; }
                names[pid] = procName;
            }
            if (!string.Equals(procName, name, StringComparison.OrdinalIgnoreCase)) return true;
            if (!GetWindowRect(hwnd, out var r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = hwnd; }
            return true;
        }, IntPtr.Zero);
        if (best == IntPtr.Zero) return null;

        var title = new StringBuilder(512);
        var cls = new StringBuilder(256);
        GetWindowTextW(best, title, title.Capacity);
        GetClassNameW(best, cls, cls.Capacity);
        return $"{Encode(title.ToString())}:{Encode(cls.ToString())}:{exe}";
    }

    static string Encode(string s) => s.Replace("#", "#22").Replace(":", "#3A");
}
