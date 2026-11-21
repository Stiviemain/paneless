using System.Runtime.Versioning;
using Paneless.Core;

namespace Paneless.App.Platform;

/// <summary>Tiles mirror windows across the screen, as large as they fit, keeping each one's shape.</summary>
[SupportedOSPlatform("windows")]
public static class WindowArranger
{
    const int Gap = 8;
    const int ToolbarRoom = 56;   // the toolbar docked to the right of each device mirror

    public static void Arrange(IReadOnlyList<MirrorSession> sessions, nint mainWindow)
    {
        var windows = new List<(nint Hwnd, double Aspect, bool Toolbar)>();
        foreach (var s in sessions)
        {
            try { s.Process.Refresh(); } catch { continue; }
            var h = s.Process.MainWindowHandle;
            if (h == 0 || Win32.Bounds(h) is not { } b || b.Right <= b.Left || b.Bottom <= b.Top) continue;
            windows.Add((h, (double)(b.Right - b.Left) / (b.Bottom - b.Top), s.Kind == SessionKind.Mirror));
        }
        if (windows.Count == 0) return;

        var area = Win32.WorkArea(mainWindow != 0 ? mainWindow : windows[0].Hwnd);
        int areaW = area.Right - area.Left, areaH = area.Bottom - area.Top;

        // Pick the number of columns that makes the windows biggest.
        var best = (Cols: 1, Score: 0.0);
        for (var cols = 1; cols <= windows.Count; cols++)
        {
            var rows = (int)Math.Ceiling(windows.Count / (double)cols);
            double cellW = (areaW - Gap * (cols + 1)) / (double)cols, cellH = (areaH - Gap * (rows + 1)) / (double)rows;
            var score = windows.Sum(w => Fit(w.Aspect, cellW - (w.Toolbar ? ToolbarRoom : 0), cellH).Area);
            if (score > best.Score) best = (cols, score);
        }

        var c = best.Cols;
        var r = (int)Math.Ceiling(windows.Count / (double)c);
        double cw = (areaW - Gap * (c + 1)) / (double)c, ch = (areaH - Gap * (r + 1)) / (double)r;
        for (var i = 0; i < windows.Count; i++)
        {
            var (hwnd, aspect, toolbar) = windows[i];
            var (w, h, _) = Fit(aspect, cw - (toolbar ? ToolbarRoom : 0), ch);
            var cellX = area.Left + Gap + (i % c) * (cw + Gap);
            var cellY = area.Top + Gap + (i / c) * (ch + Gap);
            var x = cellX + (cw - (toolbar ? ToolbarRoom : 0) - w) / 2;
            var y = cellY + (ch - h) / 2;
            Win32.Place(hwnd, (int)x, (int)y, (int)w, (int)h);
        }
    }

    static (double W, double H, double Area) Fit(double aspect, double maxW, double maxH)
    {
        if (maxW <= 0 || maxH <= 0) return (0, 0, 0);
        var w = Math.Min(maxW, maxH * aspect);
        var h = w / aspect;
        return (w, h, w * h);
    }
}
