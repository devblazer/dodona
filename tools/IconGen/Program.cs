using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// The Dodona icon: the oracle's oak. Dodona was the oldest Greek oracle — Zeus answered
// through the rustling leaves of a sacred oak, read by listeners. Six leaves in the six
// lane colours on one dark tree: many voices, one trunk, which is the product.
//
// Drawn in code on a 256-unit canvas and rendered at every ICO size. Small sizes drop
// the trunk and tighten the leaves — a 16px oak is a smudge, but six coloured leaves
// around a bare centre still read.

const int Design = 256;

// the app's own dark chrome, and the lane palette in slot order (Vm.cs)
var bg = Color.FromRgb(0x17, 0x19, 0x1D);
var bgEdge = Color.FromRgb(0x3A, 0x3E, 0x44);
var trunk = Color.FromRgb(0x8A, 0x6B, 0x4A);
var palette = new[] { "#4FC3F7", "#81C784", "#FFB74D", "#BA68C8", "#E57373", "#FFD54F" }
    .Select(h => (Color)ColorConverter.ConvertFromString(h)).ToArray();

// The canopy: six leaves EVENLY spaced on one arc, each tangent to it — computed, not
// eyeballed, because the eyeballed version clustered at the top and floated at the
// sides. Crown centre C, radius R; leaf i sits at angle θ and lies along the tangent.
// 160°..20° rather than a full half-circle: at 180° the end leaves hang at trunk height
// and look like they are falling off the tree.
var crown = new Point(128, 142);
const double R = 88;
var leaves = Enumerable.Range(0, 6).Select(i =>
{
    double theta = 160 - i * 28.0;
    double rad = theta * Math.PI / 180;
    return (X: crown.X + R * Math.Cos(rad), Y: crown.Y - R * Math.Sin(rad), Rot: 90 - theta);
}).ToArray();

DrawingVisual Draw(int px)
{
    bool tiny = px < 48;                    // below this the trunk is noise, not a tree
    var v = new DrawingVisual();
    using var dc = v.RenderOpen();
    double s = px / (double)Design;
    dc.PushTransform(new ScaleTransform(s, s));

    // dark rounded plate, hairline edge so it holds on white desktops too
    var plate = new Rect(8, 8, 240, 240);
    dc.DrawRoundedRectangle(new SolidColorBrush(bg), new Pen(new SolidColorBrush(bgEdge), 6), plate, 52, 52);

    if (!tiny)
    {
        // trunk + branches: thick round-capped strokes reaching TOWARD the canopy —
        // stopping short of it, so the leaves float just off the fingertips
        var wood = new SolidColorBrush(trunk);
        Pen P(double w) => new(wood, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(P(20), new Point(128, 214), new Point(128, 140));
        foreach (var ang in new[] { 132.0, 90.0, 48.0 })     // toward leaf 2, the apex gap, leaf 5
        {
            double rad = ang * Math.PI / 180;
            var tip = new Point(crown.X + R * 0.60 * Math.Cos(rad), crown.Y - R * 0.60 * Math.Sin(rad));
            dc.DrawLine(P(12), new Point(128, 148), tip);
        }
        // ground: the oracle sits somewhere
        dc.DrawLine(P(9), new Point(88, 216), new Point(168, 216));
    }

    // the six leaves; at tiny sizes pull them toward centre and fatten them
    double pull = tiny ? 0.86 : 1.0;
    double rx = tiny ? 30 : 25, ry = tiny ? 20 : 15;
    for (int i = 0; i < leaves.Length; i++)
    {
        var (x, y, rot) = leaves[i];
        double cx = crown.X + (x - crown.X) * pull;
        double cy = (tiny ? 136 : crown.Y) + (y - crown.Y) * pull - (tiny ? 0 : 0);
        dc.PushTransform(new RotateTransform(rot, cx, cy));
        dc.DrawEllipse(new SolidColorBrush(palette[i]), null, new Point(cx, cy), rx, ry);
        dc.Pop();
    }
    dc.Pop();
    return v;
}

byte[] RenderPng(int px)
{
    var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(Draw(px));
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(rtb));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

// ---- pack the ICO: ICONDIR + one PNG-compressed entry per size (fine on Vista+) ----
var sizes = new[] { 256, 128, 64, 48, 32, 24, 16 };
var pngs = sizes.Select(RenderPng).ToArray();

var repo = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var outIco = Path.Combine(repo, "assets", "dodona.ico");
Directory.CreateDirectory(Path.GetDirectoryName(outIco)!);

using (var f = new BinaryWriter(File.Create(outIco)))
{
    f.Write((ushort)0); f.Write((ushort)1); f.Write((ushort)sizes.Length);   // ICONDIR
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));                      // 0 means 256
        f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        f.Write((byte)0); f.Write((byte)0);                                   // colors, reserved
        f.Write((ushort)1); f.Write((ushort)32);                              // planes, bpp
        f.Write(pngs[i].Length); f.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var png in pngs) f.Write(png);
}
Console.WriteLine($"wrote {outIco} ({new FileInfo(outIco).Length} bytes, sizes: {string.Join(",", sizes)})");

// previews for the human/agent eye
var prev = Path.Combine(repo, "assets", "preview");
Directory.CreateDirectory(prev);
foreach (var px in new[] { 256, 48, 16 })
    File.WriteAllBytes(Path.Combine(prev, $"icon-{px}.png"), RenderPng(px));
Console.WriteLine($"previews in {prev}");
