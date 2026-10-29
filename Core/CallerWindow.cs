using System.Runtime.InteropServices;
using System.Text;

namespace DoomGuy.Core;

/// <summary>
/// Finds the window a Claude Code session runs in, from the hook's HTTP connection: the TCP table names the
/// process that connected (curl), and its parents lead up through the shell and Claude to the terminal or
/// editor that owns a window (Windows Terminal, VS Code, a console). Then it can bring that window forward.
/// </summary>
public static class CallerWindow
{
    /// <summary>The window of whatever connected from 'clientPort' to our 'serverPort', or zero.</summary>
    public static IntPtr Find(int clientPort, int serverPort, string cwd)
    {
        try
        {
            int pid = ClientPid(clientPort, serverPort);
            return pid == 0 ? IntPtr.Zero : WindowOf(pid, cwd);
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Restores and raises the window. Works when called while handling the user's own click.</summary>
    public static bool Focus(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
        return SetForegroundWindow(hwnd);
    }

    public static bool Alive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static string Title(IntPtr hwnd)
    {
        if (!Alive(hwnd)) return "";
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- the connecting process

    private static int ClientPid(int clientPort, int serverPort)
    {
        const int AfInet = 2, OwnerPidAll = 5;
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, OwnerPidAll, 0);
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AfInet, OwnerPidAll, 0) != 0) return 0;
            int rows = Marshal.ReadInt32(buf);
            // MIB_TCPROW_OWNER_PID: state, local addr, local port, remote addr, remote port, pid (6 DWORDs).
            for (int i = 0; i < rows; i++)
            {
                var row = buf + 4 + i * 24;
                int local = Port(Marshal.ReadInt32(row, 8)), remote = Port(Marshal.ReadInt32(row, 16));
                if (local == clientPort && remote == serverPort) return Marshal.ReadInt32(row, 20);
            }
            return 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // Ports sit in the low 16 bits in network byte order.
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    // ---------------------------------------------------------------- up to a window

    private static readonly HashSet<string> StopAt = new(StringComparer.OrdinalIgnoreCase) { "explorer", "services", "svchost", "wininit", "winlogon", "sihost" };

    private static IntPtr WindowOf(int pid, string cwd)
    {
        var parents = ParentMap();
        var windows = VisibleWindows();
        var folder = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        for (int depth = 0, p = pid; depth < 16 && p > 4; depth++)
        {
            if (!parents.TryGetValue(p, out var entry) || StopAt.Contains(Path.GetFileNameWithoutExtension(entry.Name))) break;
            // A console app's window belongs to the conhost it started, not to the app itself.
            var mine = windows.Where(w => w.Pid == p || (parents.TryGetValue(w.Pid, out var pp) && pp.Parent == p && pp.Name is "conhost.exe")).ToList();
            if (mine.Count > 0)
            {
                // An editor or terminal with several windows: prefer the one showing this project.
                var match = folder.Length > 0 ? mine.FirstOrDefault(w => w.Title.Contains(folder, StringComparison.OrdinalIgnoreCase)) : default;
                return match.Hwnd != IntPtr.Zero ? match.Hwnd : mine[0].Hwnd;
            }
            p = entry.Parent;
        }
        return IntPtr.Zero;
    }

    private static Dictionary<int, (int Parent, string Name)> ParentMap()
    {
        var map = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                map[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, e.szExeFile.ToLowerInvariant());
        }
        finally { CloseHandle(snap); }
        return map;
    }

    private static List<(IntPtr Hwnd, int Pid, string Title)> VisibleWindows()
    {
        var list = new List<(IntPtr, int, string)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true; // skip owned windows
            var sb = new StringBuilder(256);
            if (GetWindowText(h, sb, sb.Capacity) == 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            list.Add((h, (int)pid, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
}
