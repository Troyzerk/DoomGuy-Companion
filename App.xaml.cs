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

        var wads = ResolveWads();
        if (wads == null) { Shutdown(); return; }
        var (art, faceFrom) = wads.Value;
        try
        {
            WadFile? faceWad = null;
            try { if (faceFrom != null) faceWad = new WadFile(faceFrom); } catch { } // a broken face WAD just means Freedoom's face
            _assets = new Assets(new WadFile(art), faceWad);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't read {art}:\n{ex.Message}", "DoomGuy", MessageBoxButton.OK, MessageBoxImage.Error);
            if (!WadFile.IsBundled(art)) { _settings.WadPath = null; _settings.Art = "freedoom"; }
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
        // Started as the head: draw one main-window frame unseen a few seconds in, so its art (upscaled on
        // first use, about 100 ms in HD) is ready and opening it later doesn't stall.
        if (startMini)
        {
            var warm = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(5) };
            warm.Tick += (_, _) =>
            {
                warm.Stop();
                if (!_main.IsVisible) _main.Screen.Draw();
            };
            warm.Start();
        }

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

    /// <summary>
    /// The art and the face. The art is Freedoom, shipped beside the exe, or with GAME ART on DOOM your own Doom
    /// WAD. Doomguy's face comes from your Doom WAD whenever there is one, so he's on Freedoom's bar too; only
    /// with no Doom at all is the face Freedoom's. With neither WAD around, it asks.
    /// </summary>
    private (string Art, string? Face)? ResolveWads()
    {
        var doom = _yourDoom = YourDoom();
        var free = WadFile.Bundled();
        if (_settings.Art == "doom" && doom != null) return (_wadInUse = doom, null);
        if (free != null) return (_wadInUse = free, doom);
        if (doom != null) return (_wadInUse = doom, null);

        MessageBox.Show("DoomGuy draws everything from a Doom IWAD, and the Freedoom one it comes with is missing.\n\nPick DOOM.WAD, DOOM2.WAD or freedoom1.wad.",
            "DoomGuy", MessageBoxButton.OK, MessageBoxImage.Information);
        var picked = AskForWad();
        if (picked == null) return null;
        _settings.WadPath = picked;
        SaveSettings();
        return (_wadInUse = picked, null);
    }

    /// <summary>A Doom WAD with Doomguy in it: the one picked before, else one found in Steam or GOG.</summary>
    private string? YourDoom()
    {
        if (_settings.WadPath is { } p && File.Exists(p) && !WadFile.IsBundled(p)) return p;
        var found = WadFile.FindInstalled();
        if (found != null && found != _settings.WadPath)
        {
            _settings.WadPath = found;
            SaveSettings();
        }
        return found;
    }

    private string _wadInUse = "";
    private string? _yourDoom;

    private static string? AskForWad()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Doom IWAD (*.wad)|*.wad", Title = "Pick a Doom IWAD (DOOM.WAD, DOOM2.WAD or the shareware DOOM1.WAD)" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>Found once at startup (SETUP asks every frame; the search walks the Steam and GOG folders).</summary>
    public bool HasDoom => _yourDoom != null;

    /// <summary>
    /// SETUP -> GAME ART. With no Doom WAD yet, a click picks one (Doomguy's face, art unchanged); otherwise it
    /// switches between Freedoom's art and Doom's. The art is loaded at startup, so DoomGuy restarts.
    /// </summary>
    public void SwitchArt()
    {
        if (!HasDoom)
        {
            var picked = AskForWad();
            if (picked == null) return;
            try
            {
                var wad = new WadFile(picked);
                if (!wad.Has("PLAYPAL") || !wad.Has("STFST00")) throw new InvalidDataException("It has no Doomguy face, so it isn't a Doom IWAD.");
            }
            catch (Exception ex)
            {
                _hub?.Message($"CAN'T USE {Path.GetFileName(picked)}: {ex.Message}", Ink.Red, "error");
                return;
            }
            _settings.WadPath = picked;
        }
        else _settings.Art = _settings.Art == "doom" ? "freedoom" : "doom";
        Restart();
    }

    /// <summary>Starts a fresh copy and quits this one (it lets go of the single-instance lock and the port first).</summary>
    private void Restart()
    {
        SaveAll();
        _server?.Stop();
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { }
        _mutex = null;
        if (Environment.ProcessPath is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        if (_main != null) _main.AllowClose = true;
        if (_mini != null) _mini.AllowClose = true;
        if (_alert != null) _alert.AllowClose = true;
        Shutdown();
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

    public string WadName => WadFile.IsBundled(_wadInUse) ? "FREEDOOM" : Path.GetFileName(_wadInUse).ToUpperInvariant();

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
