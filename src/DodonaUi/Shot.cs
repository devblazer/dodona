using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DodonaUi;

/// <summary>
/// Self-rendering PNG capture (§17). The UI renders its own visual tree rather than being
/// photographed: no window-finding, no occlusion, no DPI drift, and it works on a window
/// that is behind three other things. 96dpi means pixel == DIP, so a fixed window size
/// gives byte-identical output across machines — which is what makes screenshots usable
/// as assertions instead of just illustrations.
/// </summary>
static class Shot
{
    public static string Save(FrameworkElement target, string outPath)
    {
        target.UpdateLayout();
        int w = (int)Math.Ceiling(target.ActualWidth), h = (int)Math.Ceiling(target.ActualHeight);
        if (w == 0 || h == 0) return "error: target has no size (window not rendered yet?)";
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(target);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(outPath);
        enc.Save(fs);
        return $"screenshot {w}x{h} -> {outPath}";
    }
}
