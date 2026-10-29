using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using DoomGuy.Core;
using DoomGuy.Doom;
using Frame = DoomGuy.Doom.Frame;

namespace DoomGuy.UI;

/// <summary>
/// A pixel screen with the HD face layered over it at the face's spot, scaled together by one Viewbox:
/// the screen with hard pixel edges, the face smoothly (it is drawn at 6x and only ever scaled down).
/// </summary>
public sealed class Stage
{
    public readonly Viewbox Root;
    public readonly Image Screen;
    public readonly Image Face;
    private readonly WriteableBitmap _face;
    private readonly int _w, _h;

    private WriteableBitmap? _hdBmp;
    private Image? _hdImg;

    public Stage(int w, int h, HdFaces hd, int faceX, int faceY, bool hdLayer = false)
    {
        _w = w; _h = h;
        Screen = new Image { Width = w, Height = h, Stretch = Stretch.Fill };
        _face = new WriteableBitmap(hd.W, hd.H, 96, 96, PixelFormats.Pbgra32, null);
        Face = new Image { Source = _face, Width = hd.SmallW, Height = hd.SmallH, Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(Face, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(Face, faceX - hd.OriginX);
        Canvas.SetTop(Face, faceY - hd.OriginY);
        var canvas = new Canvas { Width = w, Height = h, Children = { Screen, Face } };
        if (hdLayer)
        {
            // The HD renderer's canvas, over the pixel screen (which stays underneath to take the mouse).
            _hdBmp = new WriteableBitmap(w * HdCanvas.K, h * HdCanvas.K, 96, 96, PixelFormats.Pbgra32, null);
            _hdImg = new Image { Source = _hdBmp, Width = w, Height = h, Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(_hdImg, BitmapScalingMode.HighQuality);
            canvas.Children.Add(_hdImg);
        }
        Root = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
    }

    /// <summary>Like ScreenHost.Present, skips frames identical to the last one (most are).</summary>
    public void PresentHd(uint[] px)
    {
        if (_hdBmp == null) return;
        if (_hdShown.Length == px.Length && px.AsSpan().SequenceEqual(_hdShown)) return;
        if (_hdShown.Length != px.Length) _hdShown = new uint[px.Length];
        px.CopyTo(_hdShown, 0);
        _hdBmp.WritePixels(new Int32Rect(0, 0, _hdBmp.PixelWidth, _hdBmp.PixelHeight), px, _hdBmp.PixelWidth * 4, 0);
    }

    private uint[] _hdShown = [];

    /// <summary>
    /// Shows the HD canvas instead of the pixel screen (the pixel image stays, invisible, for input); or, with
    /// overlay, over it (the 8-bit look's small text).
    /// </summary>
    public void ShowHd(bool on, bool overlay = false)
    {
        if (_hdImg == null) return;
        _hdImg.Visibility = on || overlay ? Visibility.Visible : Visibility.Collapsed;
        Screen.Opacity = on ? 0 : 1;
    }

    public void PresentFace(uint[] px) => _face.WritePixels(new Int32Rect(0, 0, _face.PixelWidth, _face.PixelHeight), px, _face.PixelWidth * 4, 0);

    public void ShowFace(bool on, bool shadow)
    {
        Face.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var want = on && shadow;
        if (want == Face.Effect is not null) return;
        Face.Effect = want ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 5, ShadowDepth = 1.5, Opacity = 0.55, Color = Colors.Black } : null;
    }

    /// <summary>One screen pixel = exactly 'scale' device pixels, at a fixed size (no stretching).</summary>
    public void SetPixelScale(Visual v, int scale)
    {
        var dpi = VisualTreeHelper.GetDpi(v);
        Root.Stretch = Stretch.None;
        ((FrameworkElement)Root.Child).LayoutTransform = new ScaleTransform(scale / dpi.DpiScaleX, scale / dpi.DpiScaleY);
    }

    /// <summary>Shows only the top part of the screen (in screen pixels).</summary>
    public void SetVisibleHeight(int h)
    {
        var canvas = (Canvas)Root.Child;
        canvas.Height = h;
        canvas.ClipToBounds = h < _h;
    }

    public double PixelWidth(Visual v, int scale) => _w * scale / VisualTreeHelper.GetDpi(v).DpiScaleX;
    public double PixelHeight(Visual v, int scale) => _h * scale / VisualTreeHelper.GetDpi(v).DpiScaleY;
}

/// <summary>The full window: borderless, resizable, letterboxed 320x200.</summary>
public sealed class MainWindow : Window
{
    public readonly ScreenHost Host;
    public readonly MainScreen Screen;
    public readonly Stage Stage;
    private readonly Assets _assets;
    private readonly HdFaces _hd;
    private readonly Settings _settings;
    private readonly IShell _shell;
    private int _iconFace = -1;
    public bool AllowClose;

    public MainWindow(Assets assets, HdFaces hd, Hub hub, IShell shell, bool showTitle, ModernRenderer modern)
    {
        _hd = hd;
        _assets = assets;
        _settings = hub.Settings;
        _shell = shell;
        Title = "DoomGuy";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Brushes.Black;
        MinWidth = 320;
        MinHeight = 200;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        Stage = new Stage(320, 200, hd, StatusBar.FaceX, 168, hdLayer: true);
        Content = new Grid { Background = Brushes.Black, Children = { Stage.Root } };
        Host = new ScreenHost(Stage.Screen, 320, 200) { Drag = _ => { try { DragMove(); } catch { } } };
        Screen = new MainScreen(assets, Host, hub, shell, showTitle, modern);

        TextInput += (_, e) => Host.Input.Typed += e.Text;
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.F4 && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
            Host.Input.Keys.Add(key);
            if (key is Key.Tab or Key.Up or Key.Down or Key.Left or Key.Right or Key.Back or Key.Enter or Key.Escape) e.Handled = true;
        };
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) return;
            WindowState = WindowState.Normal;
            shell.Minimize();
        };
        SourceInitialized += (_, _) =>
        {
            ApplyScale(position: true);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(KeepAspect);
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS { public IntPtr Hwnd, After; public int X, Y, Cx, Cy; public uint Flags; }

    /// <summary>
    /// Resizing keeps the screen's 320x200 shape, so it always scales evenly and fills the window with no
    /// black bars. Dragging an edge: the other side follows; a corner: whichever way the mouse moved more
    /// leads. Any other resize (Win+arrow snapping, a DPI change) keeps the width and fixes the height.
    /// The size is remembered when a resize ends.
    /// </summary>
    private IntPtr KeepAspect(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SIZING = 0x0214, WM_WINDOWPOSCHANGING = 0x0046, WM_EXITSIZEMOVE = 0x0232;
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var p = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((p.Flags & 0x1) != 0 || p.Cx <= 0) return IntPtr.Zero;
            int want = (int)Math.Round(p.Cx * 200 / 320.0);
            // Within a pixel is rounding from WM_SIZING already holding the shape; leave it.
            if (Math.Abs(p.Cy - want) <= 1) return IntPtr.Zero;
            p.Cy = want;
            Marshal.StructureToPtr(p, lParam, false);
            return IntPtr.Zero;
        }
        if (msg == WM_EXITSIZEMOVE)
        {
            if (_settings.MainWidth is not double saved || Math.Abs(saved - ActualWidth) >= 1)
            {
                _settings.MainWidth = ActualWidth;
                _shell.SaveSettings();
            }
            return IntPtr.Zero;
        }
        if (msg != WM_SIZING) return IntPtr.Zero;
        var r = Marshal.PtrToStructure<MiniWindow.RECT>(lParam);
        int edge = wParam.ToInt32(), w = r.R - r.L, h = r.B - r.T;
        bool heightLeads = edge is 3 or 6;
        if (edge is 4 or 5 or 7 or 8 && MiniWindow.GetWindowRect(hwnd, out var old))
            heightLeads = Math.Abs(h - (old.B - old.T)) * 320 > Math.Abs(w - (old.R - old.L)) * 200;
        if (heightLeads) w = (int)Math.Round(h * 320 / 200.0); else h = (int)Math.Round(w * 200 / 320.0);
        // Grow or shrink away from the edge being dragged.
        if (edge is 1 or 4 or 7) r.L = r.R - w; else r.R = r.L + w;
        if (edge is 3 or 4 or 5) r.T = r.B - h; else r.B = r.T + h;
        Marshal.StructureToPtr(r, lParam, false);
        handled = true;
        return 1;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowClose) { base.OnClosing(e); return; }
        e.Cancel = true;
        Screen.AskQuit();
    }

    public void RememberPosition()
    {
        if (!IsVisible) return;
        _settings.MainLeft = Left;
        _settings.MainTop = Top;
    }

    public void ApplyScale(bool position = false)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var work = SystemParameters.WorkArea;
        if (_settings.MainWidth is double mw && mw >= MinWidth)
        {
            // The size it was last dragged to, shrunk if it no longer fits this screen.
            double w = Math.Min(mw, Math.Min(work.Width, work.Height * 320 / 200));
            Width = w;
            Height = w * 200 / 320;
        }
        else
        {
            int s = Math.Max(1, _settings.WindowScale);
            while (s > 1 && (320 * s / dpi.DpiScaleX > work.Width || 200 * s / dpi.DpiScaleY > work.Height)) s--;
            Width = 320 * s / dpi.DpiScaleX;
            Height = 200 * s / dpi.DpiScaleY;
        }
        if (!position) return;
        if (_settings.MainLeft is double l && _settings.MainTop is double t && OnScreen(l, t, Width, Height)) { Left = l; Top = t; }
        else { Left = work.Left + (work.Width - Width) / 2; Top = work.Top + (work.Height - Height) / 2; }
    }

    internal static bool OnScreen(double l, double t, double w, double h) =>
        l + w > SystemParameters.VirtualScreenLeft + 40 && t >= SystemParameters.VirtualScreenTop - 5
        && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
        && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    /// <summary>The taskbar icon is the marine's current face.</summary>
    public void UpdateIcon(int face)
    {
        if (face == _iconFace) return;
        _iconFace = face;
        Icon = _settings.HdFace && _hd.Face(face) is { } px ? HdIcon(px) : FaceBitmap(_assets, face);
    }

    /// <summary>The HD face cropped to a square; Windows scales it down for the taskbar.</summary>
    private BitmapSource HdIcon(uint[] px)
    {
        int side = Math.Min(_hd.W, _hd.H);
        var full = BitmapSource.Create(_hd.W, _hd.H, 96, 96, PixelFormats.Pbgra32, null, px, _hd.W * 4);
        var crop = new CroppedBitmap(full, new Int32Rect((_hd.W - side) / 2, (_hd.H - side) / 2, side, side));
        crop.Freeze();
        return crop;
    }

    public static BitmapSource FaceBitmap(Assets a, int face)
    {
        var f = new Frame(32, 32);
        var p = a.Faces[Math.Clamp(face, 0, a.Faces.Length - 1)];
        if (p != null) f.Patch(p, (32 - p.Width) / 2, (32 - p.Height) / 2, offsets: false);
        var px = new uint[32 * 32];
        f.Resolve(a.Palettes[0], px);
        var bmp = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Pbgra32, null, px, 32 * 4);
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>The floating status bar shown while minimized. Transparent pixels click through.</summary>
public sealed class MiniWindow : Window
{
    public readonly ScreenHost Host;
    public readonly MiniScreen Screen;
    public readonly Stage Stage;
    private readonly Hub _hub;
    private bool _hiddenForFullscreen;
    public bool Wanted;
    public Action? Clicked;

    public MiniWindow(Assets assets, HdFaces hd, Hub hub, IShell shell)
    {
        _hub = hub;
        Title = "DoomGuy Status Bar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        _hd = hd;
        _bubble = new Bubble(assets, hub);
        Stage = new Stage(MiniScreen.W, MiniScreen.H, hd, StatusBar.FaceX, MiniScreen.BarY);
        // Face-only mode: the big HD head with its speech bubble beside it; the pixel bar is hidden.
        _big = new WriteableBitmap(hd.W, hd.H, 96, 96, PixelFormats.Pbgra32, null);
        _bigFace = new Image
        {
            Source = _big,
            Stretch = Stretch.Fill,
            VerticalAlignment = VerticalAlignment.Bottom,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.55, Color = Colors.Black },
        };
        RenderOptions.SetBitmapScalingMode(_bigFace, BitmapScalingMode.HighQuality);
        _bubbleBmp = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Pbgra32, null);
        _bubbleImg = new Image { Source = _bubbleBmp, Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
        _faceRow = new Grid { Visibility = Visibility.Collapsed };
        _faceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _faceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _faceRow.Children.Add(_bigFace);
        _faceRow.Children.Add(_bubbleImg);
        SetSide(bubbleLeft: false);
        Content = new Grid { Children = { Stage.Root, _faceRow } };
        Host = new ScreenHost(Stage.Screen, MiniScreen.W, MiniScreen.H);
        foreach (var el in new FrameworkElement[] { _bigFace, _bubbleImg })
        {
            el.MouseEnter += (_, _) => (Host.Input.MX, Host.Input.MY) = (MiniScreen.W / 2, MiniScreen.H - 1);
            el.MouseLeave += (_, _) => Host.Input.MX = Host.Input.MY = -1;
        }
        _bigFace.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) Host.Input.DoublePressed = true;
            else Host.Drag?.Invoke(e);
        };
        // Clicking the announcement is the only way it goes away; the last one also marks everything seen.
        // Something waiting on you won't go: Doom's "oof" says so.
        _bubbleImg.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_bubble.Pinned) { hub.Play("NOWAY"); return; }
            if (!_bubble.Announcing) { Clicked?.Invoke(); return; }
            _bubble.Dismiss();
            hub.Play("SWTCHN");
            if (hub.Announcements.Count == 0) Clicked?.Invoke();
        };
        Screen = new MiniScreen(assets, Host, hub) { OpenMain = shell.ShowMain };
        Host.Drag = _ =>
        {
            double l = Left, t = Top;
            // Nothing else may move the window mid-drag, or the head slides away from the cursor.
            _dragging = true;
            try { DragMove(); } catch { }
            finally { _dragging = false; }
            if (Math.Abs(Left - l) < 2 && Math.Abs(Top - t) < 2) Clicked?.Invoke();
            else
            {
                EnsureOnMonitor();
                UpdateSide();
                hub.Settings.MiniLeft = Left;
                hub.Settings.MiniTop = Top;
                shell.SaveSettings();
            }
        };
        SourceInitialized += (_, _) => { ApplyScale(); ZOrder.HideFromAltTab(this); };
        DpiChanged += (_, _) => ApplyScale(keepPosition: true);
    }

    private readonly HdFaces _hd;
    private readonly Bubble _bubble;
    private readonly WriteableBitmap _big;
    private WriteableBitmap _bubbleBmp;
    private readonly Image _bigFace, _bubbleImg;
    private readonly Grid _faceRow;
    private int _bubbleAnchor = Bubble.H / 2;
    private bool? _faceMode, _pixelBubble;
    private bool _bubbleLeft, _dragging;

    private bool FaceMode => _hub.Settings.MiniMode == "face";

    /// <summary>Called every frame; re-lays out only when the mode (bar/face, HD on/off) or a size changed.</summary>
    public void ApplyMode()
    {
        if (_faceMode == FaceMode && _pixelBubble == !_hub.Settings.HdFace) return;
        ApplyScale(keepPosition: true);
    }

    /// <summary>The HD face over the pixel bar (face-only mode always shows its own big face).</summary>
    public void ShowHd(bool on) => Stage.ShowFace(on && !FaceMode, shadow: false);

    public void PresentHd(uint[] px)
    {
        if (FaceMode) _big.WritePixels(new Int32Rect(0, 0, _big.PixelWidth, _big.PixelHeight), px, _big.PixelWidth * 4, 0);
        else Stage.PresentFace(px);
    }

    /// <summary>
    /// Per tic: announcements only play out in face-only mode; with the bar or the main window up, the HUD has
    /// already said it. They wait (rather than drop) while the head is stepped aside for a fullscreen game.
    /// </summary>
    public void TickBubble()
    {
        if (FaceMode && Wanted) _bubble.Tick();
        else { _hub.Announcements.Clear(); _bubble.Dismiss(); }
    }

    public void UpdateBubble()
    {
        if (!FaceMode) return;
        _bubble.Draw(Host.MouseInside, pointRight: _bubbleLeft, _bubbleAnchor, pixel: _pixelBubble == true);
        if (_bubble.Dirty)
        {
            var c = _bubble.Canvas;
            if (_bubbleBmp.PixelWidth != c.W)
            {
                _bubbleBmp = new WriteableBitmap(c.W, c.H, 96, 96, PixelFormats.Pbgra32, null);
                _bubbleImg.Source = _bubbleBmp;
            }
            _bubbleBmp.WritePixels(new Int32Rect(0, 0, c.W, c.H), c.Px, c.W * 4, 0);
            _bubble.Dirty = false;
        }
        _bubbleImg.Opacity = _bubble.Opacity;
        _bubbleImg.Visibility = _bubble.Opacity > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    private void SetSide(bool bubbleLeft)
    {
        _bubbleLeft = bubbleLeft;
        Grid.SetColumn(_bubbleImg, bubbleLeft ? 0 : 1);
        Grid.SetColumn(_bigFace, bubbleLeft ? 1 : 0);
    }

    /// <summary>
    /// The bubble opens towards the middle of the screen; the head stays where it is. Decided by where the
    /// head is, not the whole window: the window's middle moves when the bubble swaps sides, and a head
    /// near the screen's middle would flip back and forth every second.
    /// </summary>
    public void UpdateSide()
    {
        if (!FaceMode || !IsVisible || _dragging) return;
        var h = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(h, out var r)) return;
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return;
        double faceW = (r.R - r.L) * _bigFace.Width / (_bigFace.Width + _bubbleImg.Width);
        double head = _bubbleLeft ? r.R - faceW / 2 : r.L + faceW / 2;
        bool left = head > (mi.Monitor.L + mi.Monitor.R) / 2.0;
        if (left == _bubbleLeft) return;
        SetSide(left);
        // In physical pixels: DIPs differ between monitors with different scaling.
        int bubbleW = (int)Math.Round(r.R - r.L - faceW);
        SetWindowPos(h, IntPtr.Zero, r.L + (left ? -bubbleW : bubbleW), r.T, 0, 0, 0x0001 | 0x0004 | 0x0010);
        _hub.Settings.MiniLeft = Left;
    }

    public void ApplyScale(bool keepPosition = false)
    {
        int scale = Math.Clamp(_hub.Settings.MiniScale, 1, 6);
        Stage.SetPixelScale(this, scale);
        Stage.SetVisibleHeight(MiniScreen.H);
        Stage.Root.Visibility = FaceMode ? Visibility.Collapsed : Visibility.Visible;
        _faceRow.Visibility = FaceMode ? Visibility.Visible : Visibility.Collapsed;
        var dpi = VisualTreeHelper.GetDpi(this);
        int faceScale = Math.Clamp(_hub.Settings.FaceScale, 2, 8);
        // The bubble is shown at 3/4 of the face's size: 3 screen pixels per Doom pixel at FACE SIZE 4. HD draws
        // it at 4x and smooths it down; the 8-bit one keeps whole pixels so every letter stays crisp.
        bool pixel = !_hub.Settings.HdFace;
        _pixelBubble = pixel;
        double textScale = pixel ? Math.Max(1, Math.Round(faceScale * 0.75)) : faceScale * 0.75;
        RenderOptions.SetBitmapScalingMode(_bubbleImg, pixel ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _bigFace.Width = _hd.SmallW * faceScale / dpi.DpiScaleX;
        _bigFace.Height = _hd.SmallH * faceScale / dpi.DpiScaleY;
        _bubbleImg.Width = Bubble.W * textScale / dpi.DpiScaleX;
        _bubbleImg.Height = Bubble.H * textScale / dpi.DpiScaleY;
        // The head sits at the bottom of the row and the bubble in its middle: point the tail at the head's middle.
        double rowH = Math.Max(_bigFace.Height, _bubbleImg.Height);
        _bubbleAnchor = (int)Math.Round((rowH - _bigFace.Height / 2 - (rowH - _bubbleImg.Height) / 2) / _bubbleImg.Height * Bubble.H);
        _faceMode = FaceMode;
        if (keepPosition) return;
        var w = FaceMode ? _bigFace.Width + _bubbleImg.Width : Stage.PixelWidth(this, scale);
        var hh = FaceMode ? Math.Max(_bigFace.Height, _bubbleImg.Height) : Stage.PixelHeight(this, scale);
        var work = SystemParameters.WorkArea;
        if (_hub.Settings.MiniLeft is double l && _hub.Settings.MiniTop is double t && MainWindow.OnScreen(l, t, w, hh)) { Left = l; Top = t; }
        else { Left = work.Right - w - 16; Top = work.Bottom - hh - 8; }
    }

    /// <summary>
    /// Once a second, and whenever another window comes to the front: step aside for a real fullscreen
    /// game or video on this monitor, otherwise make sure we are visible, on a monitor, and on top.
    /// </summary>
    public void Maintain()
    {
        UpdateSide();
        if (!Wanted || _dragging) return;
        bool hide = _hub.Settings.HideOverFullscreen && ForegroundIsFullscreen();
        if (hide != _hiddenForFullscreen)
        {
            _hiddenForFullscreen = hide;
            if (hide) Hide(); else Show();
        }
        if (_hiddenForFullscreen) return;
        if (!IsVisible) base.Show();
        EnsureOnMonitor();
        ZOrder.BringToTop(this);
    }

    public bool AllowClose;

    /// <summary>Alt+F4 or a WM_CLOSE on the head is ignored: a closed window can't be shown again, and it is shown every second.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose) e.Cancel = true;
        base.OnClosing(e);
    }

    public new void Show()
    {
        if (_hiddenForFullscreen) return;
        base.Show();
        ZOrder.BringToTop(this);
    }

    /// <summary>
    /// Snaps the head (or the bar) back inside the nearest screen's work area when it is dragged partly off,
    /// or stranded by a monitor being unplugged. Only the solid part counts: the bubble and the bar's message
    /// space are transparent, and the bubble opens towards the middle anyway. Works in physical pixels.
    /// </summary>
    private void EnsureOnMonitor()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) return;
        RECT s = r;
        if (FaceMode)
        {
            double rowH = Math.Max(_bigFace.Height, _bubbleImg.Height);
            int faceW = (int)Math.Round((r.R - r.L) * _bigFace.Width / (_bigFace.Width + _bubbleImg.Width));
            int faceH = (int)Math.Round((r.B - r.T) * _bigFace.Height / rowH);
            if (_bubbleLeft) s.L = s.R - faceW; else s.R = s.L + faceW;
            s.T = s.B - faceH;
        }
        else s.T = s.B - (r.B - r.T) * 32 / MiniScreen.H;

        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromRect(ref s, 2), ref mi)) return;
        var w = mi.Work;
        int dx = s.L < w.L ? w.L - s.L : s.R > w.R ? w.R - s.R : 0;
        int dy = s.T < w.T ? w.T - s.T : s.B > w.B ? w.B - s.B : 0;
        if (dx == 0 && dy == 0) return;
        SetWindowPos(h, IntPtr.Zero, r.L + dx, r.T + dy, 0, 0, 0x0001 | 0x0004 | 0x0010);
        _hub.Settings.MiniLeft = Left;
        _hub.Settings.MiniTop = Top;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);

    /// <summary>
    /// A true fullscreen window (game, video, F11 browser) on the same monitor as the head: borderless,
    /// not merely maximized, covering the whole monitor. A maximized window with an auto-hide taskbar,
    /// or anything on another monitor, doesn't count.
    /// </summary>
    private bool ForegroundIsFullscreen()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(h, out var pid);
        if (pid == Environment.ProcessId) return false;
        var cls = new StringBuilder(64);
        GetClassName(h, cls, 64);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (IsZoomed(h)) return false;
        const int WS_CAPTION = 0x00C00000;
        if ((GetWindowLong(h, -16) & WS_CAPTION) == WS_CAPTION) return false;
        var monitor = MonitorFromWindow(h, 2);
        var self = new WindowInteropHelper(this).Handle;
        if (self != IntPtr.Zero && MonitorFromWindow(self, 2) != monitor) return false;
        if (!GetWindowRect(h, out var r)) return false;
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;
        return r.L <= mi.Monitor.L && r.T <= mi.Monitor.T && r.R >= mi.Monitor.R && r.B >= mi.Monitor.B;
    }
}

/// <summary>
/// Keeps windows at the top of the always-on-top band. WPF's Topmost is set once and is easily lost: any
/// other topmost window that activates later goes above, and a hide/show can drop it. So we re-assert it
/// (without activating) when the foreground window changes, and the app does it every second as well.
/// </summary>
public static class ZOrder
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10, NoOwnerZOrder = 0x200;
    private const uint EventSystemForeground = 0x3, EventSystemMinimizeEnd = 0x17, OutOfContext = 0x0;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    private static WinEventProc? _proc;

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLong(IntPtr h, int index, IntPtr value);

    /// <summary>
    /// Keeps a floating window out of Alt+Tab and Task View (a tool window), so switching apps only offers
    /// real ones. Call once its handle exists.
    /// </summary>
    public static void HideFromAltTab(Window w)
    {
        const int ExStyle = -20;
        const long ToolWindow = 0x80, AppWindow = 0x40000;
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        long ex = GetWindowLong(h, ExStyle).ToInt64();
        SetWindowLong(h, ExStyle, new IntPtr(ex & ~AppWindow | ToolWindow));
    }

    public static void BringToTop(Window w)
    {
        if (!w.IsVisible) return;
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) SetWindowPos(h, HwndTopmost, 0, 0, 0, 0, NoSize | NoMove | NoActivate | NoOwnerZOrder);
    }

    /// <summary>Calls back (on this thread) shortly after any window comes to the foreground or is restored.</summary>
    public static void WatchForeground(Action onChange)
    {
        // The new window may raise itself a moment after activation, so answer twice: now and a beat later.
        var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        later.Tick += (_, _) => { later.Stop(); onChange(); };
        _proc = (_, _, _, _, _, _, _) =>
        {
            onChange();
            later.Stop();
            later.Start();
        };
        SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, 0, 0, OutOfContext);
        SetWinEventHook(EventSystemMinimizeEnd, EventSystemMinimizeEnd, IntPtr.Zero, _proc, 0, 0, OutOfContext);
    }
}
/// <summary>The reminder pop-up: topmost, never steals focus.</summary>
public sealed class AlertWindow : Window
{
    public readonly ScreenHost Host;
    public readonly AlertScreen Screen;
    private readonly Hub _hub;

    public AlertWindow(Assets assets, Hub hub)
    {
        _hub = hub;
        Title = "DoomGuy Reminder";
        WindowStyle = WindowStyle.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Black;
        var image = new Image();
        Content = image;
        Host = new ScreenHost(image, AlertScreen.W, AlertScreen.H) { Drag = _ => { try { DragMove(); } catch { } } };
        Screen = new AlertScreen(assets, Host, hub);
        SourceInitialized += (_, _) => { Host.SetPixelScale(this, Scale); ZOrder.HideFromAltTab(this); };
    }

    private int Scale => Math.Clamp(_hub.Settings.MiniScale + 1, 2, 4);

    public bool AllowClose;

    /// <summary>Alt+F4 or a WM_CLOSE is ignored: DONE or a snooze clears it, and a closed window can't come back.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose) e.Cancel = true;
        base.OnClosing(e);
    }

    public void ShowAlert()
    {
        if (IsVisible) return;
        Show();
        Host.SetPixelScale(this, Scale);
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Host.Image.Width) / 2;
        Top = work.Top + 60;
    }
}
