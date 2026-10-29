using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DoomGuy.Core;
using DoomGuy.Doom;
using DoomGuy.UI;

namespace DoomGuy;

public partial class App : Application, IShell
{
    private Mutex? _mutex;
    private Settings _settings = new();
    private Assets? _assets;
    private Hub? _hub;
    private ApiServer? _server;
    private MainWindow? _main;
    private MiniWindow? _mini;
    private AlertWindow? _alert;
    private FaceAnimator? _face;
    private uint[] _faceBuf = [];
    private HdFaces? _hd;
    private ModernRenderer? _modern;
    private HdArt? _art;
    private (bool, bool, int PaletteIndex, bool, uint[]?) _faceShown;
    private readonly Stopwatch _clock = new();
    private long _tics;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Cli.TryRun(e.Args, out var code)) { Shutdown(code); return; }

        _settings = Store.Load<Settings>("settings.json");
        bool startMini = e.Args.Any(a => a.Equals("--mini", StringComparison.OrdinalIgnoreCase)) || _settings.StartMinimized;

        // One copy per data folder, so a test copy (DOOMGUY_DATA) can run beside the real one.
        _mutex = new Mutex(true, "DoomGuy.SingleInstance." + (uint)Store.Dir.ToLowerInvariant().GetHashCode(StringComparison.Ordinal), out bool first);
        if (!first)
        {
            Cli.Send(_settings.Port, startMini ? "/mini" : "/show", "");
            Shutdown();
            return;
        }

        var wad = ResolveWad();
        if (wad == null) { Shutdown(); return; }
        try { _assets = new Assets(new WadFile(wad)); }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't read {wad}:\n{ex.Message}", "DoomGuy", MessageBoxButton.OK, MessageBoxImage.Error);
            _settings.WadPath = null;
            SaveSettings();
            Shutdown();
            return;
        }

        _hub = new Hub(_settings, Store.Load<SavedState>("state.json"))
        {
            Sound = new Sound(_assets.Wad) { Enabled = _settings.Sound, Volume = _settings.Volume },
        };
        _server = new ApiServer(_hub, Dispatcher) { ShowMain = ShowMain, ShowMini = Minimize };
        if (!_server.Start(_settings.Port)) _hub.Message($"API OFFLINE: {_server.Error}", Ink.Red, "error");

        var hd = _hd = new HdFaces(_assets);
        _face = new FaceAnimator(hd);
        var face = _face;
        _art = new HdArt(_assets);
        _modern = new ModernRenderer(_assets, _art, hd, () => _settings.HdFace ? face.Current : hd.PixelFace(_hub.FaceIndex));
        _faceBuf = new uint[hd.W * hd.H];
        _main = new MainWindow(_assets, hd, _hub, this, showTitle: !startMini && _settings.TitleScreen, _modern);
        _mini = new MiniWindow(_assets, hd, _hub, this);
        _alert = new AlertWindow(_assets, _hub);
        _mini.Clicked = () =>
        {
            _hub.MarkAllSeen();
            if (_hub.FiringCount > 0) _alert.ShowAlert();
        };
        _hub.ReminderFired += () => _alert.ShowAlert();
        _server.ShowTab = _main.Screen.ShowTab;
        _server.ProfileChanged = SaveSettings;
        _server.Perf = PerfReport;

        if (startMini) Minimize(); else ShowMain();
        if (_hub.FiringCount > 0) _alert.ShowAlert();

        _clock.Start();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => RunFrame();
        timer.Start();
        SessionEnding += (_, _) => SaveAll();
        ZOrder.WatchForeground(KeepOnTop);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(KeepOnTop);
    }

    /// <summary>Runs the 35 Hz game clock and redraws whatever is on screen.</summary>
    private void RunFrame()
    {
        if (_hub == null || _main == null || _mini == null || _alert == null || _assets == null || _face == null) return;
        long target = _clock.ElapsedMilliseconds * Hub.TicRate / 1000;
        long n = target - _tics;
        if (n <= 0) return;
        _tics = target;
        long t0 = Stopwatch.GetTimestamp();
        for (long i = 0; i < Math.Min(n, 10); i++)
        {
            _hub.Tick();
            _main.Screen.Tick();
            _face.Tick(_hub.FaceIndex);
            _mini.TickBubble();
            if (_hub.Tic % Hub.TicRate == 0) KeepOnTop();
        }
        long t1 = Stopwatch.GetTimestamp();
        Draw();
        long t2 = Stopwatch.GetTimestamp();
        _perf.Frames++;
        _perf.Tick += t1 - t0;
        _perf.Draw += t2 - t1;
        _perf.Worst = Math.Max(_perf.Worst, t2 - t0);
    }

    private void Draw()
    {
        if (_hub == null || _main == null || _mini == null || _alert == null || _assets == null || _face == null) return;
        var pal = _assets.Palettes[_hub.PaletteIndex];
        if (_main.IsVisible && MainNeedsFrame())
        {
            _main.Screen.Draw();
            if (_main.Screen.Modern)
            {
                // The HD canvas gets the same palette flash as the pixel screen, as a tint.
                var c = _main.Screen.Canvas;
                if (_hub.PaletteIndex != 0) HdFaces.Tint(c.Px, c.Px, _hub.PaletteIndex);
                _main.Stage.PresentHd(c.Px);
            }
            else
            {
                _main.Host.Present(pal);
                if (_main.Screen.Overlay is { } o)
                {
                    if (_hub.PaletteIndex != 0) HdFaces.Tint(o.Px, o.Px, _hub.PaletteIndex);
                    _main.Stage.PresentHd(o.Px);
                }
            }
            _main.Stage.ShowHd(_main.Screen.Modern, overlay: _main.Screen.Overlay != null);
        }
        _main.UpdateIcon(_hub.FaceIndex);
        if (_mini.IsVisible)
        {
            _mini.Screen.Draw();
            _mini.Host.Present(pal);
            _mini.UpdateBubble();
        }
        PresentHdFace();
        if (_alert.IsVisible)
        {
            if (_hub.FiringCount == 0) _alert.Hide();
            else
            {
                _alert.Screen.Draw();
                _alert.Host.Present(pal);
            }
        }
    }

    /// <summary>
    /// The minimized window's face layer: the HD animation (or, with HD off, the original pixels blown up),
    /// tinted by the current palette flash. The main window draws its own face in its renderer.
    /// </summary>
    private void PresentHdFace()
    {
        bool faceOnly = _mini!.Screen.FaceOnly;
        _mini.ApplyMode();
        bool miniBar = _mini.IsVisible && !faceOnly && _settings.HdFace;
        bool miniFace = _mini.IsVisible && faceOnly;
        _mini.ShowHd(miniBar);
        var src = _settings.HdFace ? _face!.Current : _hd!.PixelFace(_hub!.FaceIndex);
        if ((!miniBar && !miniFace) || src == null) return;
        var key = (false, true, _hub!.PaletteIndex, faceOnly, src);
        if (!_face!.Changed && key == _faceShown) return;
        HdFaces.Tint(src, _faceBuf, key.PaletteIndex);
        _mini.PresentHd(_faceBuf);
        _face.Changed = false;
        _faceShown = key;
    }

    private (long Tic, int MX, int MY, uint[]? Face, int Palette) _mainLast;

    /// <summary>
    /// The main window costs a few ms a frame at 1280x800. Anything that moves gets every frame (input, the
    /// face, flashes, the melt); otherwise ~12 a second keeps the clocks and blinks going.
    /// </summary>
    private bool MainNeedsFrame()
    {
        var m = _main!.Host.Input;
        var now = (_hub!.Tic, m.MX, m.MY, _settings.HdFace ? _face!.Current : _hd!.PixelFace(_hub.FaceIndex), _hub.PaletteIndex);
        bool due = _main.Screen.Busy || m.Busy || now.MX != _mainLast.MX || now.MY != _mainLast.MY
                   || !ReferenceEquals(now.Item4, _mainLast.Face) || now.PaletteIndex != _mainLast.Palette
                   || _hub.Tic - _mainLast.Tic >= 3 || _hub.Tic < _mainLast.Tic;
        if (due) _mainLast = now;
        return due;
    }

    private (long Frames, long Tick, long Draw, long Worst) _perf;
    private (DateTime At, TimeSpan Cpu) _perfLast = (DateTime.Now, TimeSpan.Zero);

    /// <summary>/perf: memory and caches now, frame costs since the previous call.</summary>
    private object PerfReport()
    {
        var proc = Process.GetCurrentProcess();
        var now = DateTime.Now;
        double secs = Math.Max(0.001, (now - _perfLast.At).TotalSeconds);
        double cpu = (proc.TotalProcessorTime - _perfLast.Cpu).TotalMilliseconds / 10 / secs;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var gc = GC.GetGCMemoryInfo();
        var faces = _hd!.CacheStats;
        var art = _art!.CacheStats;
        long f = Math.Max(1, _perf.Frames);
        var report = new
        {
            Seconds = Math.Round(secs, 1),
            CpuPercentOfOneCore = Math.Round(cpu, 2),
            FramesPerSecond = Math.Round(_perf.Frames / secs, 1),
            TickMsAvg = Math.Round(Ms(_perf.Tick) / f, 3),
            DrawMsAvg = Math.Round(Ms(_perf.Draw) / f, 3),
            FrameMsWorst = Math.Round(Ms(_perf.Worst), 2),
            WorkingSetMB = proc.WorkingSet64 >> 20,
            PrivateMB = proc.PrivateMemorySize64 >> 20,
            ManagedHeapMB = gc.HeapSizeBytes >> 20,
            ManagedCommittedMB = gc.TotalCommittedBytes >> 20,
            ManagedLiveMB = GC.GetTotalMemory(false) >> 20,
            FaceCache = new { faces.Faces, faces.Pairs, faces.PixelFaces, MB = faces.Bytes >> 20 },
            ArtCache = new { art.Count, MB = art.Bytes >> 20 },
            MainOpen = _main!.IsVisible,
            Mini = _mini!.IsVisible ? _settings.MiniMode : "hidden",
        };
        _perf = default;
        _perfLast = (now, proc.TotalProcessorTime);
        return report;
    }

    /// <summary>The head and any due reminder stay in front of everything else.</summary>
    private void KeepOnTop()
    {
        _mini?.Maintain();
        if (_alert != null) ZOrder.BringToTop(_alert);
    }

    private string? ResolveWad()
    {
        if (_settings.WadPath is { } p && File.Exists(p)) return p;
        var found = WadFile.FindInstalled();
        if (found == null)
        {
            MessageBox.Show("DoomGuy draws everything from your own copy of Doom.\n\nPick DOOM.WAD or DOOM2.WAD (Steam: Ultimate Doom\\base\\DOOM.WAD).",
                "DoomGuy", MessageBoxButton.OK, MessageBoxImage.Information);
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Doom IWAD (*.wad)|*.wad", Title = "Find DOOM.WAD" };
            if (dlg.ShowDialog() != true) return null;
            found = dlg.FileName;
        }
        _settings.WadPath = found;
        SaveSettings();
        return found;
    }

    // ---------------------------------------------------------------- IShell

    public void Minimize()
    {
        if (_main == null || _mini == null) return;
        _main.RememberPosition();
        _main.Hide();
        _mini.Wanted = true;
        _mini.Show();
        SaveSettings();
        // The main window's frames were big; once it's gone, give that memory back instead of sitting on it.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        });
    }

    public void ShowMain()
    {
        if (_main == null || _mini == null) return;
        _mini.Wanted = false;
        _mini.Hide();
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void Quit()
    {
        SaveAll();
        _server?.Stop();
        if (_main != null) _main.AllowClose = true;
        if (_mini != null) _mini.AllowClose = true;
        if (_alert != null) _alert.AllowClose = true;
        Shutdown();
    }

    public void ApplyWindowScale() => _main?.ApplyScale();

    public void ApplyMini() => _mini?.ApplyScale(keepPosition: true);

    public void SaveSettings() => Store.Save("settings.json", _settings);

    public string WadName => Path.GetFileName(_settings.WadPath ?? "");

    public string ApiStatus => _server?.Error ?? $"127.0.0.1:{_settings.Port}";

    private void SaveAll()
    {
        _main?.RememberPosition();
        SaveSettings();
        _hub?.Save();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hub != null) SaveAll();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
