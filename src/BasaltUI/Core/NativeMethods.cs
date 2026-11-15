using System.Runtime.InteropServices;

namespace BasaltUI.Core;

/// <summary>
/// Win32 interop needed to start basalt in an invisible console and to stop it the way Ctrl+C would:
/// basalt's Python server handles KeyboardInterrupt (it sends QUIT to the engine and closes the slots),
/// so a graceful stop must deliver a console Ctrl+C event instead of TerminateProcess.
/// </summary>
internal static class NativeMethods
{
    public const uint CTRL_C_EVENT = 0;
    public const uint CTRL_BREAK_EVENT = 1;

    public const uint AF_INET = 2u;
    public const uint AF_INET6 = 23u;
    public const int TCP_TABLE_OWNER_PID_ALL = 5;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;

    public delegate bool ConsoleCtrlDelegate(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handlerRoutine, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetConsoleProcessList(uint[] lpdwProcessList, uint dwProcessListSize);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort,
                                                   uint ipVersion, int ulTblClass, uint reserved);

    /// <summary>
    /// Owns the delegate so it is not collected while the OS may still call it.
    /// </summary>
    private static ConsoleCtrlDelegate? swallow;

    /// <summary>
    /// Why the last console attach failed, as a Win32 code — 5 means the session is not allowed to attach to
    /// another process's console, which is what a restricted or service session does, and the difference between
    /// that and a bug is worth knowing when a stop turns out to take the long way round.
    /// </summary>
    public static int LastConsoleError { get; private set; }

    /// <summary>
    /// The process ids sharing the console of <paramref name="pid"/> — those, and only those, are the ones a
    /// Ctrl+C in that console can reach. Null when the process has no console we can attach to.
    /// </summary>
    public static int[]? ConsolePids(int pid)
    {
        if (pid <= 0)
            return null;
        FreeConsole();
        if (!AttachConsole((uint)pid))
        {
            LastConsoleError = Marshal.GetLastWin32Error();
            return null;
        }
        LastConsoleError = 0;
        try
        {
            var list = new uint[64];
            var count = GetConsoleProcessList(list, (uint)list.Length);
            if (count == 0)
                return null;
            if (count > list.Length)
            {
                list = new uint[count];
                count = GetConsoleProcessList(list, (uint)list.Length);
            }
            return list.Take((int)Math.Min(count, (uint)list.Length)).Select(u => (int)u).ToArray();
        }
        finally
        {
            FreeConsole();
        }
    }

    /// <summary>
    /// Sends Ctrl+C into the console that carries the most of a basalt process tree, the one its Python server
    /// runs on: basalt reacts to Ctrl+C alone (it sends QUIT to the engine and closes the slots), and a console
    /// event never travels to another console. The launcher starts the server as a separate process and that
    /// process holds its own console, so attaching to the launcher alone — the id we started — misses it.
    /// Returns false when no process in the tree has a console we can attach to.
    /// </summary>
    public static bool SendCtrlC(int targetPid, IEnumerable<int>? alsoTry = null, Action<string>? trace = null)
    {
        var tree = new List<int>();
        if (targetPid > 0)
            tree.Add(targetPid);
        tree.AddRange((alsoTry ?? []).Where(p => p > 0 && !tree.Contains(p)));
        if (tree.Count == 0)
            return false;

        swallow ??= _ => true;
        FreeConsole();                                  // a GUI app normally has none; make sure we start detached

        // Map the consoles of the tree: attach to a process, read who shares its console, detach again.
        var seen = new HashSet<string>();
        var consoles = new List<(int AttachTo, int[] Pids)>();
        foreach (var pid in tree)
        {
            var pids = ConsolePids(pid);
            if (pids is not { Length: > 0 })
                continue;
            if (!seen.Add(string.Join(",", pids.OrderBy(p => p))))
                continue;
            consoles.Add((pid, pids));
        }

        foreach (var (attachTo, pids) in consoles.OrderByDescending(c => c.Pids.Count(tree.Contains))
                                              .ThenByDescending(c => c.Pids.Length))
        {
            if (!AttachConsole((uint)attachTo))
                continue;
            try
            {
                SetConsoleCtrlHandler(swallow, true);   // a broadcast would otherwise stop us too
                if (GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0))
                {
                    trace?.Invoke($"ctrl+C to the console of pid {attachTo}, holding [{string.Join(", ", pids)}]");
                    return true;
                }
                trace?.Invoke($"console of pid {attachTo} refused the event: {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                // Detach but keep the handler installed for this process' life: harmless with no console, and it
                // still protects us if another console is ever inherited.
                FreeConsole();
            }
        }

        trace?.Invoke(consoles.Count == 0 && LastConsoleError != 0
            ? $"no console among [{string.Join(", ", tree)}] would take a Ctrl+C (attach refused, win32 {LastConsoleError})"
            : $"no console among [{string.Join(", ", tree)}] would take a Ctrl+C");
        return false;
    }

    /// <summary>Every running process, with the id it was started from and its image name, from one snapshot.</summary>
    public static List<(int Pid, int ParentPid, string Name)> ProcessTable()
    {
        var rows = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE)
            return rows;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry))
                return rows;
            do
            {
                rows.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? ""));
            }
            while (Process32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
        return rows;
    }

    /// <summary>Direct and indirect child process ids, from a Toolhelp snapshot (no WMI, no extra packages).</summary>
    public static List<int> ChildProcesses(int parentPid)
    {
        var table = ProcessTable();
        var kids = table.Where(r => r.ParentPid == parentPid).Select(r => r.Pid).ToList();
        var found = new HashSet<int>(kids);
        var frontier = new Queue<int>(kids);
        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            foreach (var pid in table.Where(r => r.ParentPid == parent).Select(r => r.Pid))
                if (found.Add(pid))
                    frontier.Enqueue(pid);
        }
        return found.ToList();
    }

    /// <summary>
    /// Everything that belongs to one running server: the id we started, whatever answers on the server's port,
    /// and the children of both. basalt's launcher hands the work to a helper that is gone again by the time we
    /// look, so that helper's children — the Python server and the engine under it — are reachable by port and
    /// not by a child walk from the id we started.
    /// </summary>
    public static List<int> ServerTree(int startedPid, int port)
    {
        var found = new HashSet<int>();
        var roots = new List<int>();
        if (startedPid > 0)
            roots.Add(startedPid);
        if (port is > 0 and <= 65535)
            roots.AddRange(ListenersOnPort((ushort)port));

        foreach (var root in roots)
        {
            if (root <= 0)
                continue;
            found.Add(root);
            foreach (var pid in ChildProcesses(root))
                found.Add(pid);
        }
        return found.ToList();
    }

    /// <summary>The main window title of a process, used to name an external instance ("basalt — PowerShell").</summary>
    public static string MainWindowTitleOf(int pid)
    {
        var buffer = new System.Text.StringBuilder(512);
        return EnumWindowThreads(pid, buffer) ? buffer.ToString() : "";
    }

    private static bool EnumWindowThreads(int pid, System.Text.StringBuilder title)
    {
        var found = false;
        EnumThreadWindowsDelegate callback = (hWnd, param) =>
        {
            if (found || !IsWindowVisible(hWnd))
                return true;
            var length = GetWindowTextLength(hWnd);
            if (length <= 0)
                return true;
            var sb = new System.Text.StringBuilder(length + 1);
            if (GetWindowText(hWnd, sb, sb.Capacity) <= 0)
                return true;
            title.Clear().Append(sb);
            found = true;
            return false;
        };

        windowCallbacks.Add(callback);                  // keep the delegate alive while the OS enumerates
        foreach (var tid in ThreadIdsOf(pid))
            EnumThreadWindows((uint)tid, callback, IntPtr.Zero);

        windowCallbacks.Remove(callback);
        return found;
    }

    private static readonly List<EnumThreadWindowsDelegate> windowCallbacks = [];

    private static IEnumerable<int> ThreadIdsOf(int pid)
    {
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snap == INVALID_HANDLE_VALUE)
            yield break;
        try
        {
            var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (!Thread32First(snap, ref entry))
                yield break;
            do
            {
                if (entry.th32OwnerProcessID == (uint)pid)
                    yield return (int)entry.th32ThreadID;
            }
            while (Thread32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // CharSet.Unicode is not decoration here: the structs below are the W layouts, and an Ansi-named import would
    // bind Process32FirstA, which rejects them and leaves every process enumeration empty.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Thread32First(IntPtr snapshot, ref THREADENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Thread32Next(IntPtr snapshot, ref THREADENTRY32 entry);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumThreadWindowsDelegate proc, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    private delegate bool EnumThreadWindowsDelegate(IntPtr hWnd, IntPtr param);

    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const uint TH32CS_SNAPTHREAD = 0x4;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }


    /// <summary>Owning process ids of every socket listening on <paramref name="localPort"/> (IPv4 + IPv6).</summary>
    public static int[] ListenersOnPort(ushort localPort)
    {
        // MIB_TCPROW_OWNER_PID:   state, localAddr, localPort, remoteAddr, remotePort, owningPid  (24 bytes)
        // MIB_TCP6ROW_OWNER_PID: localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid (56)
        var pids = new List<int>();
        pids.AddRange(ListenersOnPort(AF_INET, localPort, rowSize: 24, stateOffset: 0, portOffset: 8, pidOffset: 20));
        pids.AddRange(ListenersOnPort(AF_INET6, localPort, rowSize: 56, stateOffset: 48, portOffset: 20, pidOffset: 52));
        return pids.Distinct().Where(p => p > 0).ToArray();
    }

    private static IEnumerable<int> ListenersOnPort(uint family, ushort port, int rowSize,
                                                    int stateOffset, int portOffset, int pidOffset)
    {
        var swapped = (ushort)((port << 8 & 0xFF00) | (port >> 8));     // the table stores the port in network order
        var size = 0;
        var rc = GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TCP_TABLE_OWNER_PID_ALL, 0);
        if (rc != ERROR_INSUFFICIENT_BUFFER && rc != 0)
            yield break;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedTcpTable(buffer, ref size, true, family, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != 0)
                yield break;

            var rows = Marshal.ReadInt32(buffer);                       // the table starts with the row count
            var basePtr = buffer + 4;
            var length = size - 4;
            for (var i = 0; i < rows; i++)
            {
                var row = basePtr + i * rowSize;
                if (i * rowSize + rowSize > length)
                    break;                                              // guard against a short read
                if (Marshal.ReadInt32(row + stateOffset) != 2 /* LISTEN */)
                    continue;

                var rowPort = (ushort)Marshal.ReadInt32(row + portOffset);
                if (rowPort == port || rowPort == swapped)
                    yield return Marshal.ReadInt32(row + pidOffset);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
