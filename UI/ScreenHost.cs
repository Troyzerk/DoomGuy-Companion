using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DoomGuy.Doom;
using Frame = DoomGuy.Doom.Frame;

namespace DoomGuy.UI;

/// <summary>Mouse and keyboard since the last tic, in virtual (Doom) pixels.</summary>
public sealed class InputState
{
    public int MX = -1, MY = -1;
    public bool Pressed, RightPressed, DoublePressed;
    public int PX, PY;
    public int Wheel;
    public string Typed = "";
    public readonly List<Key> Keys = [];
    public bool Ctrl => Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

    /// <summary>Anything to react to this tic, short of the mouse moving (the caller tracks that).</summary>
    public bool Busy => Pressed || RightPressed || DoublePressed || Wheel != 0 || Typed.Length > 0 || Keys.Count > 0;

    public void EndTic()
    {
        Pressed = RightPressed = DoublePressed = false;
        Wheel = 0;
        Typed = "";
        Keys.Clear();
    }
}

/// <summary>Shows a <see cref="Frame"/> in a WPF Image with hard pixel edges and maps the mouse back to it.</summary>
public sealed class ScreenHost
{
    public readonly Frame Frame;
    public readonly Image Image;
    public readonly InputState Input = new();
    private readonly WriteableBitmap _bmp;
    private readonly uint[] _px;

    /// <summary>Asked on mouse down: should this point drag the window instead of clicking?</summary>
    public Func<int, int, bool>? IsDragZone;
    public Action<MouseButtonEventArgs>? Drag;

    public ScreenHost(Image image, int w, int h)
    {
        Image = image;
        Frame = new Frame(w, h);
        _px = new uint[w * h];
        _shown = new uint[w * h];
        _shown[0] = 1;
        _bmp =new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        image.Source = _bmp;
        image.SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        image.MouseMove += (_, e) => (Input.MX, Input.MY) = ToVirtual(e);
        image.MouseLeave += (_, _) => Input.MX = Input.MY = -1;
        image.MouseWheel += (_, e) => Input.Wheel += Math.Sign(e.Delta);
        image.MouseRightButtonDown += (_, e) =>
        {
            (Input.PX, Input.PY) = ToVirtual(e);
            Input.RightPressed = true;
        };
        image.MouseLeftButtonDown += (_, e) =>
        {
            var (x, y) = ToVirtual(e);
            (Input.MX, Input.MY) = (x, y);
            if (e.ClickCount == 2) Input.DoublePressed = true;
            if (IsDragZone?.Invoke(x, y) == true && e.ClickCount == 1)
            {
                Drag?.Invoke(e);
                return;
            }
            (Input.PX, Input.PY) = (x, y);
            Input.Pressed = true;
        };
    }

    public bool MouseInside => Input.MX >= 0;

    private (int, int) ToVirtual(MouseEventArgs e)
    {
        var p = e.GetPosition(Image);
        if (Image.ActualWidth <= 0 || Image.ActualHeight <= 0) return (-1, -1);
        return ((int)(p.X * Frame.W / Image.ActualWidth), (int)(p.Y * Frame.H / Image.ActualHeight));
    }

    /// <summary>
    /// Hands the frame to WPF only when it differs from the last one: an update makes WPF re-render the
    /// window (in software, for the see-through ones), and most frames are identical.
    /// </summary>
    public void Present(uint[] palette)
    {
        Frame.Resolve(palette, _px);
        if (_px.AsSpan().SequenceEqual(_shown)) return;
        _px.CopyTo(_shown, 0);
        _bmp.WritePixels(new Int32Rect(0, 0, Frame.W, Frame.H), _px, Frame.W * 4, 0);
    }

    private uint[] _shown = [];

    /// <summary>Sizes the image so one Doom pixel is exactly <paramref name="scale"/> device pixels.</summary>
    public void SetPixelScale(Visual v, int scale)
    {
        var dpi = VisualTreeHelper.GetDpi(v);
        Image.Width = Frame.W * scale / dpi.DpiScaleX;
        Image.Height = Frame.H * scale / dpi.DpiScaleY;
    }
}

/// <summary>Drawing and widget helpers shared by every screen.</summary>
public abstract class Screen
{
    protected readonly Assets A;
    protected readonly ScreenHost Host;
    protected Frame F => Host.Frame;
    protected InputState In => Host.Input;
    private readonly List<(int X, int Y, int W, int H)> _hot = [], _hotLast = [];

    protected Screen(Assets assets, ScreenHost host)
    {
        A = assets;
        Host = host;
    }

    /// <summary>True if the point was over a widget on the last drawn frame.</summary>
    protected bool OverWidget(int x, int y) => _hotLast.Any(r => x >= r.X && y >= r.Y && x < r.X + r.W && y < r.Y + r.H);

    protected void BeginFrame()
    {
        _hotLast.Clear();
        _hotLast.AddRange(_hot);
        _hot.Clear();
        F.ResetClip();
    }

    /// <summary>When set, text and sprites go through this renderer (classic pixels or the HD look).</summary>
    protected IRenderer? R;

    /// <summary>Text with a drop shadow.</summary>
    protected int Text(int x, int y, string s, Ink ink = Ink.Red, bool glow = false)
    {
        if (R != null) return R.Text(x, y, s, ink, glow);
        A.Font.Draw(F, x + 1, y + 1, s, A.Ink(Ink.Black));
        return A.Font.Draw(F, x, y, s, A.Ink(ink));
    }
    protected void TextRight(int right, int y, string s, Ink ink = Ink.Red) => Text(right - A.Font.Width(s), y, s, ink);
    protected void TextCenter(int cx, int y, string s, Ink ink = Ink.Red) => Text(cx - A.Font.Width(s) / 2, y, s, ink);
    protected int Width(string s) => A.Font.Width(s);
    protected string Fit(string s, int w) => A.Font.Fit(s, w);

    protected bool Hover(int x, int y, int w, int h) => In.MX >= x && In.MY >= y && In.MX < x + w && In.MY < y + h;

    protected bool Clicked(int x, int y, int w, int h)
    {
        if (!In.Pressed || In.PX < x || In.PY < y || In.PX >= x + w || In.PY >= y + h) return false;
        In.Pressed = false;
        return true;
    }

    /// <summary>A text button: red, yellow under the mouse.</summary>
    protected bool Button(int x, int y, string label, Ink ink = Ink.Red, Ink hot = Ink.Yellow, bool glow = false)
    {
        int w = Width(label);
        _hot.Add((x - 2, y - 2, w + 4, 12));
        bool over = Hover(x - 2, y - 2, w + 4, 12);
        Text(x, y, label, over ? hot : ink, glow || (over && R?.Hd == true));
        return Clicked(x - 2, y - 2, w + 4, 12);
    }

    protected void Widget(int x, int y, int w, int h) => _hot.Add((x, y, w, h));

    /// <summary>Doom's view border (BRDR_*) drawn just outside a rectangle.</summary>
    protected void Border(int x, int y, int w, int h)
    {
        for (int xx = x; xx < x + w; xx += 8)
        {
            F.Patch(A.BrdrT, xx, y - 8, offsets: false);
            F.Patch(A.BrdrB, xx, y + h, offsets: false);
        }
        for (int yy = y; yy < y + h; yy += 8)
        {
            F.Patch(A.BrdrL, x - 8, yy, offsets: false);
            F.Patch(A.BrdrR, x + w, yy, offsets: false);
        }
        F.Patch(A.BrdrTL, x - 8, y - 8, offsets: false);
        F.Patch(A.BrdrTR, x + w, y - 8, offsets: false);
        F.Patch(A.BrdrBL, x - 8, y + h, offsets: false);
        F.Patch(A.BrdrBR, x + w, y + h, offsets: false);
    }

    /// <summary>A sprite centred on cx with its feet on 'bottom', ignoring its world offsets.</summary>
    protected void Sprite(string name, int cx, int bottom, byte[]? tr = null)
    {
        if (R != null) { R.Sprite(name, cx, bottom); return; }
        var p = A.Sprite(name);
        if (p == null) return;
        F.Patch(p, cx - p.Width / 2, bottom - p.Height, tr, offsets: false);
    }

    protected static readonly char[] BonusCycle = ['A', 'B', 'C', 'D', 'C', 'B'];
}
