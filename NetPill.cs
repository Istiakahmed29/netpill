// NetPill.cs
// A tiny floating readout of current download/upload speed, anchored above the taskbar.
//
// Target: C# 5 / .NET Framework 4.8, x64, compiled with the in-box csc.exe.
// No designer file, no NuGet, no SDK, no installer, no admin rights.
//
// Two working classes:
//   PillForm  - the borderless layered overlay window + tray icon + menu + painting
//   NetSampler- the single background thread that reads GetIfTable2 every 1000 ms
// plus the data-history pair:
//   UsageStore- folds the raw octet deltas into per-day totals, kept in a plain TSV
//   UsageForm - the borderless layered history window (chart, summary, hover)
// Everything else is P/Invoke plumbing (Native) and a small settings file (Settings).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetPill
{
    internal static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        private static void Main()
        {
            bool created;
            // Per-session single instance. "Local\" (the default) needs no privileges.
            _mutex = new Mutex(true, "NetPill.SingleInstance.9C1F", out created);
            if (!created)
            {
                _mutex.Dispose();
                return; // second launch exits silently
            }

            EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new PillForm());
            }
            finally
            {
                GC.KeepAlive(_mutex);
                _mutex.Dispose();
            }
        }

        // The shipped app.manifest already declares PerMonitorV2. This is the fallback for
        // anyone who compiles without /win32manifest: - it fails harmlessly if already set.
        private static void EnableDpiAwareness()
        {
            try
            {
                if (Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            try
            {
                if (Native.SetProcessDpiAwareness(2) == 0) return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            try { Native.SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }
    }

    // ---------------------------------------------------------------- settings

    internal sealed class Settings
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "NetPill";

        private static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetPill");

        private static readonly string FileName = Path.Combine(Folder, "settings.ini");

        internal bool UseMbps;
        internal bool ClickThrough;
        internal bool TrayNumbers;
        internal bool PillTotal;
        internal bool HideFullscreen = true;
        internal int OpacityPercent = 90;
        internal int AdapterIndex;
        internal bool PositionSet;
        internal int PosX;
        internal int PosY;

        internal static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FileName)) return s;
                string[] lines = File.ReadAllLines(FileName);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    int t;
                    switch (k)
                    {
                        case "Units": s.UseMbps = (v == "Mbps"); break;
                        case "ClickThrough": s.ClickThrough = (v == "1"); break;
                        case "TrayNumbers": s.TrayNumbers = (v == "1"); break;
                        case "PillTotal": s.PillTotal = (v == "1"); break;
                        case "HideFullscreen": s.HideFullscreen = (v == "1"); break;
                        case "PositionSet": s.PositionSet = (v == "1"); break;
                        case "Opacity":
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out t) && t >= 50 && t <= 100)
                                s.OpacityPercent = t;
                            break;
                        case "Adapter":
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out t) && t >= 0)
                                s.AdapterIndex = t;
                            break;
                        case "X":
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) s.PosX = t;
                            break;
                        case "Y":
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) s.PosY = t;
                            break;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return s;
        }

        // Only ever called from a user action, never from the sampling loop.
        internal void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                StringBuilder sb = new StringBuilder(256);
                sb.Append("# NetPill settings - written only when something changes\r\n");
                sb.Append("Units=").Append(UseMbps ? "Mbps" : "MBps").Append("\r\n");
                sb.Append("ClickThrough=").Append(ClickThrough ? "1" : "0").Append("\r\n");
                sb.Append("TrayNumbers=").Append(TrayNumbers ? "1" : "0").Append("\r\n");
                sb.Append("PillTotal=").Append(PillTotal ? "1" : "0").Append("\r\n");
                sb.Append("HideFullscreen=").Append(HideFullscreen ? "1" : "0").Append("\r\n");
                sb.Append("Opacity=").Append(OpacityPercent.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                sb.Append("Adapter=").Append(AdapterIndex.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                sb.Append("PositionSet=").Append(PositionSet ? "1" : "0").Append("\r\n");
                sb.Append("X=").Append(PosX.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                sb.Append("Y=").Append(PosY.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                File.WriteAllText(FileName, sb.ToString(), Encoding.ASCII);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static bool IsStartWithWindows()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    string v = k.GetValue(RunValue) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static void SetStartWithWindows(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- sampling

    internal sealed class NetSampler : IDisposable
    {
        private const double Alpha = 0.35;          // EMA smoothing
        private const double MaxElapsed = 3.0;      // discard the sample beyond this (sleep/resume)
        private const int MaxAdapters = 16;
        private const int DiscoverTicks = 15;       // re-enumerate the adapter set every ~15 s
        private const int IntervalMs = 1000;

        // Adapter filtering: real, active links only.
        private static readonly string[] ExcludeKeywords = new string[]
        {
            "virtualbox", "vmware", "hyper-v", "tap-", "tap ", "wan miniport",
            "loopback", "bluetooth", "vpn", "tunnel", "wintun", "npcap", "pseudo"
        };

        private readonly object _sync = new object();
        private readonly Thread _thread;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly UsageStore _usage;

        // Everything below is preallocated: the steady-state sampling loop allocates nothing.
        private readonly uint[] _id = new uint[MaxAdapters];
        private readonly string[] _name = new string[MaxAdapters];
        private readonly ulong[] _prevIn = new ulong[MaxAdapters];
        private readonly ulong[] _prevOut = new ulong[MaxAdapters];
        private readonly double[] _dIn = new double[MaxAdapters];
        private readonly double[] _dOut = new double[MaxAdapters];
        private readonly double[] _emaIn = new double[MaxAdapters];
        private readonly double[] _emaOut = new double[MaxAdapters];
        private readonly bool[] _fresh = new bool[MaxAdapters];
        private readonly bool[] _seen = new bool[MaxAdapters];
        private int _count;

        private long _prevTicks;
        private bool _primed;
        private double _aggIn;
        private double _aggOut;

        private long _inBits;
        private long _outBits;
        private volatile int _selected;

        internal NetSampler(UsageStore usage)
        {
            _usage = usage;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.BelowNormal;
            _thread.Name = "NetPill.Sampler";
        }

        internal void Start()
        {
            _clock.Start();
            _thread.Start();
        }

        internal double DownBps
        {
            get { return BitConverter.Int64BitsToDouble(Interlocked.Read(ref _inBits)); }
        }

        internal double UpBps
        {
            get { return BitConverter.Int64BitsToDouble(Interlocked.Read(ref _outBits)); }
        }

        // 0 = aggregate all qualifying adapters, otherwise a specific InterfaceIndex.
        internal int Selected
        {
            get { return _selected; }
            set { _selected = value; }
        }

        private void Loop()
        {
            int tick = 0;
            while (true)
            {
                long started = _clock.ElapsedTicks;
                try
                {
                    if (tick % DiscoverTicks == 0) Discover();
                    Sample();
                }
                catch (Exception)
                {
                    // A dead sampling thread is worse than a bad sample: never let it escape.
                }
                tick++;

                long spentMs = (_clock.ElapsedTicks - started) * 1000L / Stopwatch.Frequency;
                int wait = IntervalMs - (int)spentMs;
                if (wait < 1) wait = 1;
                if (_stop.WaitOne(wait)) return;
            }
        }

        private void Sample()
        {
            long now = _clock.ElapsedTicks;
            double dt = _primed ? (double)(now - _prevTicks) / Stopwatch.Frequency : 0.0;
            _prevTicks = now;

            IntPtr table;
            if (Native.GetIfTable2(out table) != 0u)
            {
                _primed = false;
                return;
            }

            bool ok = _primed && dt > 0.0 && dt <= MaxElapsed;
            int count = _count; // same thread as Discover, no lock needed
            for (int i = 0; i < count; i++)
            {
                _dIn[i] = 0.0;
                _dOut[i] = 0.0;
            }

            // Raw octet deltas, summed for the daily usage history. Unlike the speed
            // readout these are never discarded: a byte the counters saw was really used,
            // even if the sample is too old or too short to report a meaningful rate.
            long addIn = 0L;
            long addOut = 0L;

            try
            {
                int rows = Marshal.ReadInt32(table, 0);
                long baseAddr = table.ToInt64() + Native.RowOffset;
                int stride = Native.RowSize;

                for (int r = 0; r < rows; r++)
                {
                    IntPtr row = new IntPtr(baseAddr + (long)r * stride);
                    uint id = (uint)Marshal.ReadInt32(row, Native.OffIndex);
                    int slot = SlotOf(id, count);
                    if (slot < 0) continue;

                    ulong inNow = (ulong)Marshal.ReadInt64(row, Native.OffIn);
                    ulong outNow = (ulong)Marshal.ReadInt64(row, Native.OffOut);

                    // Negative delta (counter reset / adapter re-plugged) or a brand new
                    // adapter: rebaseline and drop this sample instead of reporting a spike.
                    if (_fresh[slot] || inNow < _prevIn[slot] || outNow < _prevOut[slot])
                    {
                        _prevIn[slot] = inNow;
                        _prevOut[slot] = outNow;
                        _fresh[slot] = false;
                        ok = false;
                        continue;
                    }

                    _dIn[slot] += (double)(inNow - _prevIn[slot]);
                    _dOut[slot] += (double)(outNow - _prevOut[slot]);
                    addIn += (long)(inNow - _prevIn[slot]);
                    addOut += (long)(outNow - _prevOut[slot]);
                    _prevIn[slot] = inNow;
                    _prevOut[slot] = outNow;
                }
            }
            finally
            {
                Native.FreeMibTable(table);
            }

            if (addIn > 0L || addOut > 0L) _usage.Add(addIn, addOut);

            _primed = true;
            if (!ok || dt <= 0.0) return; // keep the previous reading: no spike, no 0-dip

            double sumIn = 0.0;
            double sumOut = 0.0;
            for (int i = 0; i < count; i++)
            {
                double ri = _dIn[i] / dt;
                double ro = _dOut[i] / dt;
                _emaIn[i] += Alpha * (ri - _emaIn[i]);
                _emaOut[i] += Alpha * (ro - _emaOut[i]);
                sumIn += ri;
                sumOut += ro;
            }
            _aggIn += Alpha * (sumIn - _aggIn);
            _aggOut += Alpha * (sumOut - _aggOut);

            int sel = _selected;
            if (sel == 0)
            {
                Publish(_aggIn, _aggOut);
            }
            else
            {
                int s = SlotOf((uint)sel, count);
                if (s >= 0) Publish(_emaIn[s], _emaOut[s]);
                else Publish(_aggIn, _aggOut);
            }
        }

        private void Publish(double down, double up)
        {
            Interlocked.Exchange(ref _inBits, BitConverter.DoubleToInt64Bits(down));
            Interlocked.Exchange(ref _outBits, BitConverter.DoubleToInt64Bits(up));
        }

        private int SlotOf(uint id, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_id[i] == id) return i;
            }
            return -1;
        }

        private void Discover()
        {
            IntPtr table;
            if (Native.GetIfTable2(out table) != 0u) return;
            try
            {
                int rows = Marshal.ReadInt32(table, 0);
                long baseAddr = table.ToInt64() + Native.RowOffset;
                int stride = Native.RowSize;

                lock (_sync)
                {
                    for (int i = 0; i < _count; i++) _seen[i] = false;

                    for (int r = 0; r < rows; r++)
                    {
                        IntPtr row = new IntPtr(baseAddr + (long)r * stride);
                        if (!Qualifies(row)) continue;

                        uint id = (uint)Marshal.ReadInt32(row, Native.OffIndex);
                        int slot = SlotOf(id, _count);
                        if (slot < 0)
                        {
                            if (_count >= MaxAdapters) continue;
                            slot = _count;
                            _count++;
                            _id[slot] = id;
                            _name[slot] = Marshal.PtrToStringUni(new IntPtr(row.ToInt64() + Native.OffDesc));
                            _fresh[slot] = true;
                            _emaIn[slot] = 0.0;
                            _emaOut[slot] = 0.0;
                            _prevIn[slot] = 0;
                            _prevOut[slot] = 0;
                        }
                        _seen[slot] = true;
                    }

                    // Compact out adapters that vanished (unplugged / disabled).
                    int w = 0;
                    for (int i = 0; i < _count; i++)
                    {
                        if (!_seen[i]) continue;
                        if (w != i)
                        {
                            _id[w] = _id[i];
                            _name[w] = _name[i];
                            _prevIn[w] = _prevIn[i];
                            _prevOut[w] = _prevOut[i];
                            _emaIn[w] = _emaIn[i];
                            _emaOut[w] = _emaOut[i];
                            _fresh[w] = _fresh[i];
                        }
                        w++;
                    }
                    for (int i = w; i < _count; i++) _name[i] = null;
                    _count = w;
                }
            }
            finally
            {
                Native.FreeMibTable(table);
            }
        }

        private static bool Qualifies(IntPtr row)
        {
            if (Marshal.ReadInt32(row, Native.OffOper) != 1) return false;   // IfOperStatusUp

            int type = Marshal.ReadInt32(row, Native.OffType);
            if (type != 6 && type != 71) return false;                        // Ethernet / IEEE80211

            int flags = Marshal.ReadByte(row, Native.OffFlags);
            if ((flags & 1) == 0) return false;                               // HardwareInterface
            if ((flags & 2) != 0) return false;                               // FilterInterface

            string desc = Marshal.PtrToStringUni(new IntPtr(row.ToInt64() + Native.OffDesc));
            if (string.IsNullOrEmpty(desc)) return false;
            for (int i = 0; i < ExcludeKeywords.Length; i++)
            {
                if (desc.IndexOf(ExcludeKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
            return true;
        }

        // Called from the UI thread only when a menu or tooltip is actually built.
        internal int CopyAdapters(uint[] ids, string[] names, double[] down, double[] up)
        {
            lock (_sync)
            {
                int n = _count;
                if (n > names.Length) n = names.Length;
                for (int i = 0; i < n; i++)
                {
                    ids[i] = _id[i];
                    names[i] = _name[i];
                    down[i] = _emaIn[i];
                    up[i] = _emaOut[i];
                }
                return n;
            }
        }

        public void Dispose()
        {
            _stop.Set();
            bool stopped = false;
            try { stopped = _thread.Join(1500); }
            catch (ThreadStateException) { stopped = true; }
            // Only close the handle once the thread is out, so nobody waits on a dead handle.
            if (stopped) _stop.Close();
        }
    }

    // ---------------------------------------------------------------- interop

    internal static class Native
    {
        // window styles
        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_EX_TOPMOST = 0x00000008;
        internal const int WS_EX_TRANSPARENT = 0x00000020;
        internal const int WS_EX_LAYERED = 0x00080000;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int WS_POPUP = unchecked((int)0x80000000);

        internal const int SW_HIDE = 0;
        internal const int SW_SHOWNA = 8;

        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        internal const uint ULW_ALPHA = 0x00000002;
        internal const byte AC_SRC_OVER = 0x00;
        internal const byte AC_SRC_ALPHA = 0x01;

        internal const int ABM_GETSTATE = 0x00000004;
        internal const int ABS_AUTOHIDE = 0x00000001;

        internal const uint MONITOR_DEFAULTTOPRIMARY = 1;
        internal const uint MONITOR_DEFAULTTONEAREST = 2;

        internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uCallbackMessage;
            public int uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        // Exact x64 layout of MIB_IF_ROW2 (sizeof = 1352). Declared in full so that
        // Marshal.SizeOf gives the true row stride; offsets are read back with OffsetOf,
        // and the counters are read straight out of the table with Marshal.ReadInt64.
        [StructLayout(LayoutKind.Sequential)]
        internal struct MIB_IF_ROW2
        {
            public ulong InterfaceLuid;
            public uint InterfaceIndex;
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 514)] public byte[] AliasRaw;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 514)] public byte[] DescriptionRaw;
            public uint PhysicalAddressLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
            public uint Mtu;
            public uint Type;
            public uint TunnelType;
            public uint MediaType;
            public uint PhysicalMediumType;
            public uint AccessType;
            public uint DirectionType;
            public byte InterfaceAndOperStatusFlags;
            public uint OperStatus;
            public uint AdminStatus;
            public uint MediaConnectState;
            public Guid NetworkGuid;
            public uint ConnectionType;
            public ulong TransmitLinkSpeed;
            public ulong ReceiveLinkSpeed;
            public ulong InOctets;
            public ulong InUcastPkts;
            public ulong InNUcastPkts;
            public ulong InDiscards;
            public ulong InErrors;
            public ulong InUnknownProtos;
            public ulong InUcastOctets;
            public ulong InMulticastOctets;
            public ulong InBroadcastOctets;
            public ulong OutOctets;
            public ulong OutUcastPkts;
            public ulong OutNUcastPkts;
            public ulong OutDiscards;
            public ulong OutErrors;
            public ulong OutUcastOctets;
            public ulong OutMulticastOctets;
            public ulong OutBroadcastOctets;
            public ulong OutQLen;
        }

        // MIB_IF_TABLE2 { ULONG NumEntries; MIB_IF_ROW2 Table[]; } - the row array is
        // 8-aligned, so the first row starts 8 bytes into the buffer on x64.
        internal static readonly int RowOffset = 8;
        internal static readonly int RowSize = Marshal.SizeOf(typeof(MIB_IF_ROW2));
        internal static readonly int OffIndex = (int)Marshal.OffsetOf(typeof(MIB_IF_ROW2), "InterfaceIndex");
        internal static readonly int OffDesc = (int)Marshal.OffsetOf(typeof(MIB_IF_ROW2), "DescriptionRaw");
        internal static readonly int OffFlags = (int)Marshal.OffsetOf(typeof(MIB_IF_ROW2), "InterfaceAndOperStatusFlags");
        internal static readonly int OffOper = (int)Marshal.OffsetOf(typeof(MIB_IF_ROW2), "OperStatus");
        internal static readonly int OffType = (int)Marshal.OffsetOf(typeof(MIB_IF_ROW2), "Type");
        internal static readonly int OffIn = (int)Marshal.OffsetOf(typeof(MIB_IF_ROW2), "InOctets");
        internal static readonly int OffOut = (int)Marshal.OffsetOf(typeof(MIB_IF_ROW2), "OutOctets");

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        internal static extern uint GetIfTable2(out IntPtr table);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        internal static extern void FreeMibTable(IntPtr table);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindWindowW(string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetCapture(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        [DllImport("shcore.dll")]
        internal static extern int SetProcessDpiAwareness(int value);

        [DllImport("shell32.dll")]
        internal static extern IntPtr SHAppBarMessage(int message, ref APPBARDATA data);

        [DllImport("shell32.dll")]
        internal static extern int SHQueryUserNotificationState(out int state);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hwnd);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr obj);

        internal static int GetExStyle(IntPtr hwnd)
        {
            if (IntPtr.Size == 8) return (int)GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64();
            return GetWindowLong32(hwnd, GWL_EXSTYLE);
        }

        internal static void SetExStyle(IntPtr hwnd, int value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(value));
            else SetWindowLong32(hwnd, GWL_EXSTYLE, value);
        }

        // Pushes a premultiplied 32bpp bitmap to a layered window at (x, y).
        // The destination point is authoritative: this call also moves the window.
        internal static void Blit(IntPtr hwnd, Bitmap bmp, int x, int y, int w, int h)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBmp);

            SIZE size;
            size.cx = w;
            size.cy = h;
            POINT dst;
            dst.X = x;
            dst.Y = y;
            POINT src;
            src.X = 0;
            src.Y = 0;
            BLENDFUNCTION blend;
            blend.BlendOp = AC_SRC_OVER;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = AC_SRC_ALPHA;

            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend,
                (int)ULW_ALPHA);

            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ---------------------------------------------------------------- usage history

    // One local day's totals. Key is yyyymmdd, so it sorts and compares as a plain int.
    internal struct DayUsage
    {
        internal int Key;
        internal long In;
        internal long Out;
    }

    // Folds the raw octet deltas into per-day totals and keeps them in a plain TSV:
    //   yyyy-MM-dd <tab> inBytes <tab> outBytes
    // Add() runs on the sampler thread once a second; Snapshot() runs on the UI thread.
    // One lock, held for microseconds. The file is rewritten whole (~45 bytes a day) and
    // swapped in atomically, so a crash mid-write cannot leave a half-written history.
    internal sealed class UsageStore
    {
        private const int FlushIntervalMs = 60000;

        private readonly object _lock = new object();
        private readonly Dictionary<int, long[]> _days = new Dictionary<int, long[]>(512);

        private int _todayKey;
        private bool _dirty;
        private int _lastFlush;

        internal static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetPill");

        internal static readonly string FileName = Path.Combine(Folder, "usage.tsv");

        internal UsageStore()
        {
            _todayKey = KeyOf(DateTime.Now);
            _lastFlush = Environment.TickCount;
            Load();
        }

        internal static int KeyOf(DateTime t)
        {
            return t.Year * 10000 + t.Month * 100 + t.Day;
        }

        internal void Add(long inBytes, long outBytes)
        {
            if (inBytes <= 0L && outBytes <= 0L) return;

            int key = KeyOf(DateTime.Now);
            lock (_lock)
            {
                long[] row;
                if (!_days.TryGetValue(key, out row))
                {
                    row = new long[2];
                    _days[key] = row;
                }
                row[0] += inBytes;
                row[1] += outBytes;
                _dirty = true;

                if (key != _todayKey)
                {
                    // Midnight: get the finished day onto disk right away.
                    _todayKey = key;
                    FlushLocked();
                    return;
                }
                if (unchecked((uint)(Environment.TickCount - _lastFlush)) >= FlushIntervalMs)
                {
                    FlushLocked();
                }
            }
        }

        // Cheap poll for the UI: no array allocation, just today's two counters.
        internal void Today(out long inBytes, out long outBytes)
        {
            lock (_lock)
            {
                long[] row;
                if (_days.TryGetValue(KeyOf(DateTime.Now), out row))
                {
                    inBytes = row[0];
                    outBytes = row[1];
                }
                else
                {
                    inBytes = 0L;
                    outBytes = 0L;
                }
            }
        }

        internal DayUsage[] Snapshot()
        {
            lock (_lock)
            {
                // Today always exists, so an idle machine reads 0 and not "no data".
                int today = KeyOf(DateTime.Now);
                if (!_days.ContainsKey(today)) _days[today] = new long[2];

                List<int> keys = new List<int>(_days.Keys);
                keys.Sort();
                DayUsage[] result = new DayUsage[keys.Count];
                for (int i = 0; i < keys.Count; i++)
                {
                    long[] row = _days[keys[i]];
                    result[i].Key = keys[i];
                    result[i].In = row[0];
                    result[i].Out = row[1];
                }
                return result;
            }
        }

        internal void Flush()
        {
            lock (_lock) { FlushLocked(); }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(FileName)) return;
                string[] lines = File.ReadAllLines(FileName);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 3) continue;

                    string[] d = parts[0].Trim().Split('-');
                    if (d.Length != 3) continue;
                    int y, m, dd;
                    if (!int.TryParse(d[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) continue;
                    if (!int.TryParse(d[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) continue;
                    if (!int.TryParse(d[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out dd)) continue;
                    if (y < 2000 || y > 2200 || m < 1 || m > 12 || dd < 1 || dd > 31) continue;

                    long inB, outB;
                    if (!long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out inB)) continue;
                    if (!long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out outB)) continue;
                    if (inB < 0L) inB = 0L;
                    if (outB < 0L) outB = 0L;

                    int key = y * 10000 + m * 100 + dd;
                    long[] row;
                    if (!_days.TryGetValue(key, out row))
                    {
                        row = new long[2];
                        _days[key] = row;
                    }
                    row[0] += inB;
                    row[1] += outB;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (FormatException) { }
            catch (OverflowException) { }
        }

        private void FlushLocked()
        {
            if (!_dirty) return;
            _lastFlush = Environment.TickCount;

            try
            {
                Directory.CreateDirectory(Folder);

                List<int> keys = new List<int>(_days.Keys);
                keys.Sort();
                StringBuilder sb = new StringBuilder(keys.Count * 32 + 96);
                sb.Append("# NetPill daily usage - one line per local day: yyyy-MM-dd <tab> inBytes <tab> outBytes\r\n");
                for (int i = 0; i < keys.Count; i++)
                {
                    int k = keys[i];
                    long[] row = _days[k];
                    if (row[0] == 0L && row[1] == 0L) continue; // nothing to record
                    AppendDate(sb, k);
                    sb.Append('\t');
                    sb.Append(row[0].ToString(CultureInfo.InvariantCulture));
                    sb.Append('\t');
                    sb.Append(row[1].ToString(CultureInfo.InvariantCulture));
                    sb.Append("\r\n");
                }

                string tmp = FileName + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.ASCII);
                if (File.Exists(FileName))
                {
                    try
                    {
                        File.Replace(tmp, FileName, null);
                    }
                    catch (IOException)
                    {
                        // Replace can fail on odd filesystems / filters; a copy is still whole.
                        File.Copy(tmp, FileName, true);
                        File.Delete(tmp);
                    }
                }
                else
                {
                    File.Move(tmp, FileName);
                }
                _dirty = false;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void AppendDate(StringBuilder sb, int key)
        {
            int y = key / 10000;
            int m = (key / 100) % 100;
            int d = key % 100;
            sb.Append(y.ToString("0000", CultureInfo.InvariantCulture));
            sb.Append('-');
            if (m < 10) sb.Append('0');
            sb.Append(m.ToString(CultureInfo.InvariantCulture));
            sb.Append('-');
            if (d < 10) sb.Append('0');
            sb.Append(d.ToString(CultureInfo.InvariantCulture));
        }
    }

    // ---------------------------------------------------------------- usage window

    // A borderless layered panel: today's total, a stacked bar chart (down green / up amber),
    // and a summary grid. Same rendering route as the pill - a premultiplied bitmap pushed
    // with UpdateLayeredWindow - so the rounded corners are antialiased and there are no
    // child controls to fight with.
    internal sealed class UsageForm : Form
    {
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_DPICHANGED = 0x02E0;

        private const float WLogical = 420f;
        private const float HLogical = 492f;

        private enum Zone { None, Title, Close, Tab, OpenFolder }

        private struct Bar
        {
            internal DateTime Date;
            internal long In;
            internal long Out;
            internal bool HasData;
            internal bool IsToday;
        }

        private struct Cell
        {
            internal string Key;
            internal long Value;
            internal bool IsBytes;
        }

        private readonly UsageStore _store;
        private readonly System.Windows.Forms.Timer _tick;

        private readonly Dictionary<int, DayUsage> _index = new Dictionary<int, DayUsage>();
        private DayUsage[] _all = new DayUsage[0];
        private Bar[] _bars = new Bar[0];
        private Cell[] _cells = new Cell[6];
        private int _view = 7;
        private int _hover = -1;
        private long _lastToday = -1L;

        private int _w = 420;
        private int _h = 492;
        private int _x;
        private int _y;
        private float _scale = 1f;

        private Bitmap _bmp;
        private Graphics _g;
        private GraphicsPath _path;
        private SolidBrush _bg;
        private SolidBrush _white;
        private SolidBrush _dim;
        private SolidBrush _dim2;
        private SolidBrush _dim3;
        private SolidBrush _down;
        private SolidBrush _up;
        private SolidBrush _band;
        private Pen _border;
        private Pen _hair;
        private Pen _hair2;
        private Pen _dash;
        private Pen _today;
        private Pen _tabOff;
        private Pen _tabOn;
        private Pen _xPen;
        private Font _fTitle;
        private Font _fBig;
        private Font _fUnit;
        private Font _fLabel;
        private Font _fBody;
        private Font _fTab;
        private Font _fCap;
        private Font _fCapB;
        private Font _fVal;
        private Font _fValU;
        private Font _fFoot;
        private readonly StringFormat _fmt;

        private readonly RectangleF[] _tabRects = new RectangleF[3];
        private RectangleF _closeRect;
        private RectangleF _openRect;

        private Zone _zone = Zone.None;
        private int _hitTab;
        private bool _moved;
        private bool _dragging;
        private Point _dragScr;
        private int _dragX;
        private int _dragY;

        private static readonly string[] TabLabels = new string[] { "7 days", "30 days", "12 months" };

        internal UsageForm(UsageStore store)
        {
            _store = store;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            TopMost = true;
            KeyPreview = true;
            Text = "NetPill data usage";
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            _fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            _fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip |
                                StringFormatFlags.MeasureTrailingSpaces;

            RefreshData();

            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = 1000;
            _tick.Tick += OnTick;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style = Native.WS_POPUP;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST |
                              Native.WS_EX_LAYERED;
                return cp;
            }
        }

        // ------------------------------------------------------------ lifecycle

        internal void ShowNear(Rectangle anchor)
        {
            Show();
            Place(anchor);
            // The pill never takes focus, so the foreground lock can swallow Activate().
            // The window is topmost either way; this just buys us the Esc key.
            Activate();
            Native.SetForegroundWindow(Handle);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateMetrics();
            _tick.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _tick.Stop();
            _tick.Dispose();
            if (_g != null) { _g.Dispose(); _g = null; }
            if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
            if (_path != null) { _path.Dispose(); _path = null; }
            DisposeBrush(_bg); DisposeBrush(_white); DisposeBrush(_dim); DisposeBrush(_dim2);
            DisposeBrush(_dim3); DisposeBrush(_down); DisposeBrush(_up); DisposeBrush(_band);
            DisposePen(_border); DisposePen(_hair); DisposePen(_hair2); DisposePen(_dash);
            DisposePen(_today); DisposePen(_tabOff); DisposePen(_tabOn); DisposePen(_xPen);
            DisposeFont(_fTitle); DisposeFont(_fBig); DisposeFont(_fUnit); DisposeFont(_fLabel);
            DisposeFont(_fBody); DisposeFont(_fTab); DisposeFont(_fCap); DisposeFont(_fCapB);
            DisposeFont(_fVal); DisposeFont(_fValU); DisposeFont(_fFoot);
            _fmt.Dispose();
            base.OnFormClosed(e);
        }

        private static void DisposeBrush(SolidBrush b) { if (b != null) b.Dispose(); }
        private static void DisposePen(Pen p) { if (p != null) p.Dispose(); }
        private static void DisposeFont(Font f) { if (f != null) f.Dispose(); }

        private float P(float v) { return v * _scale; }

        private void Place(Rectangle anchor)
        {
            Rectangle scr = Screen.FromRectangle(anchor).WorkingArea;
            int gap = (int)Math.Round(8 * _scale);

            int x = anchor.X + (anchor.Width - _w) / 2;
            int y = anchor.Y - _h - gap;
            if (y < scr.Top + gap) y = anchor.Bottom + gap;
            if (x < scr.Left + gap) x = scr.Left + gap;
            if (x + _w > scr.Right - gap) x = scr.Right - gap - _w;
            if (y + _h > scr.Bottom - gap) y = scr.Bottom - gap - _h;
            if (y < scr.Top + gap) y = scr.Top + gap;

            _x = x;
            _y = y;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _x, _y, _w, _h,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Render();
        }

        private void UpdateMetrics()
        {
            int dpi = 96;
            try
            {
                uint d = Native.GetDpiForWindow(Handle);
                if (d >= 72 && d <= 960) dpi = (int)d;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            _scale = dpi / 96f;
            _w = (int)Math.Ceiling(WLogical * _scale);
            _h = (int)Math.Ceiling(HLogical * _scale);

            if (_g != null) { _g.Dispose(); _g = null; }
            if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
            if (_path != null) { _path.Dispose(); _path = null; }

            _bmp = new Bitmap(_w, _h, PixelFormat.Format32bppPArgb);
            _g = Graphics.FromImage(_bmp);
            _path = RoundedPath(0f, 0f, _w, _h, P(10f));

            BuildResources();
            BuildTabs();
        }

        private void BuildResources()
        {
            DisposeBrush(_bg); DisposeBrush(_white); DisposeBrush(_dim); DisposeBrush(_dim2);
            DisposeBrush(_dim3); DisposeBrush(_down); DisposeBrush(_up); DisposeBrush(_band);
            DisposePen(_border); DisposePen(_hair); DisposePen(_hair2); DisposePen(_dash);
            DisposePen(_today); DisposePen(_tabOff); DisposePen(_tabOn); DisposePen(_xPen);
            DisposeFont(_fTitle); DisposeFont(_fBig); DisposeFont(_fUnit); DisposeFont(_fLabel);
            DisposeFont(_fBody); DisposeFont(_fTab); DisposeFont(_fCap); DisposeFont(_fCapB);
            DisposeFont(_fVal); DisposeFont(_fValU); DisposeFont(_fFoot);

            _bg = new SolidBrush(Color.FromArgb(255, 26, 26, 26));           // #1A1A1A
            _white = new SolidBrush(Color.White);
            _dim = new SolidBrush(Color.FromArgb(0x8D, 0x8D, 0x8D));
            _dim2 = new SolidBrush(Color.FromArgb(0x9A, 0x9A, 0x9A));
            _dim3 = new SolidBrush(Color.FromArgb(0x6D, 0x6D, 0x6D));
            _down = new SolidBrush(Color.FromArgb(0x4A, 0xDE, 0x80));       // #4ADE80
            _up = new SolidBrush(Color.FromArgb(0xFB, 0xBF, 0x24));         // #FBBF24
            _band = new SolidBrush(Color.FromArgb(12, 255, 255, 255));

            _border = new Pen(Color.FromArgb(36, 255, 255, 255), 1f);
            _border.Alignment = PenAlignment.Inset;
            _hair = new Pen(Color.FromArgb(26, 255, 255, 255), 1f);
            _hair2 = new Pen(Color.FromArgb(16, 255, 255, 255), 1f);
            _dash = new Pen(Color.FromArgb(58, 255, 255, 255), P(1f));
            _dash.DashStyle = DashStyle.Dash;
            _today = new Pen(Color.FromArgb(110, 255, 255, 255), P(1.4f));
            _tabOff = new Pen(Color.FromArgb(26, 255, 255, 255), 1f);
            _tabOn = new Pen(Color.FromArgb(64, 255, 255, 255), 1f);
            _xPen = new Pen(Color.FromArgb(0x8D, 0x8D, 0x8D), P(1.2f));

            _fTitle = new Font("Segoe UI", 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fBig = new Font("Segoe UI Semibold", 33f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fUnit = new Font("Segoe UI", 14f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fLabel = new Font("Segoe UI Semibold", 9.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fBody = new Font("Segoe UI", 11.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fTab = new Font("Segoe UI Semibold", 11f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fCap = new Font("Segoe UI", 9.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fCapB = new Font("Segoe UI Semibold", 9.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fVal = new Font("Segoe UI Semibold", 13f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fValU = new Font("Segoe UI", 9.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _fFoot = new Font("Segoe UI", 10f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void BuildTabs()
        {
            float x = P(16f);
            float y = P(146f);
            float h = P(26f);
            using (Graphics mg = Graphics.FromHwnd(IntPtr.Zero))
            {
                mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                for (int i = 0; i < 3; i++)
                {
                    float tw = mg.MeasureString(TabLabels[i], _fTab, PointF.Empty, _fmt).Width;
                    float w = (float)Math.Ceiling(tw) + P(20f);
                    _tabRects[i] = new RectangleF(x, y, w, h);
                    x += w + P(6f);
                }
            }
        }

        private static GraphicsPath RoundedPath(float x, float y, float w, float h, float r)
        {
            float d = r * 2f;
            if (d > w) d = w;
            if (d > h) d = h;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(x, y, d, d, 180f, 90f);
            p.AddArc(x + w - d, y, d, d, 270f, 90f);
            p.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
            p.AddArc(x, y + h - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        private static GraphicsPath RoundedPath(RectangleF r, float radius)
        {
            return RoundedPath(r.X, r.Y, r.Width, r.Height, radius);
        }

        // Square bottom, rounded top: the shape a stacked bar needs.
        private static GraphicsPath TopRounded(float x, float y, float w, float h, float r)
        {
            if (r > w * 0.5f) r = w * 0.5f;
            if (r > h * 0.5f) r = h * 0.5f;
            if (r < 0.5f) r = 0.5f;
            float d = r * 2f;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(x, y, d, d, 180f, 90f);
            p.AddArc(x + w - d, y, d, d, 270f, 90f);
            p.AddLine(x + w, y + h, x, y + h);
            p.CloseFigure();
            return p;
        }

        // ------------------------------------------------------------ data

        private static DateTime DateOf(int key)
        {
            int y = key / 10000;
            int m = (key / 100) % 100;
            int d = key % 100;
            if (m < 1) m = 1;
            if (m > 12) m = 12;
            if (d < 1) d = 1;
            if (d > 28)
            {
                int max = DateTime.DaysInMonth(y, m);
                if (d > max) d = max;
            }
            return new DateTime(y, m, d);
        }

        private void RefreshData()
        {
            _all = _store.Snapshot();
            _index.Clear();
            for (int i = 0; i < _all.Length; i++) _index[_all[i].Key] = _all[i];

            _bars = BuildBars();
            BuildSummary();

            long ti, to;
            _store.Today(out ti, out to);
            _lastToday = ti + to;
        }

        private Bar[] BuildBars()
        {
            DateTime today = DateTime.Today;

            if (_view == 12)
            {
                Bar[] months = new Bar[12];
                DateTime first = new DateTime(today.Year, today.Month, 1).AddMonths(-11);
                for (int i = 0; i < 12; i++)
                {
                    months[i].Date = first.AddMonths(i);
                    months[i].HasData = false;
                }
                for (int i = 0; i < _all.Length; i++)
                {
                    DateTime dt = DateOf(_all[i].Key);
                    int mi = (dt.Year - first.Year) * 12 + (dt.Month - first.Month);
                    if (mi < 0 || mi > 11) continue;
                    months[mi].In += _all[i].In;
                    months[mi].Out += _all[i].Out;
                    if (_all[i].In + _all[i].Out > 0L) months[mi].HasData = true;
                }
                months[11].IsToday = true; // the month in progress
                return months;
            }

            int n = _view;
            Bar[] days = new Bar[n];
            for (int i = 0; i < n; i++)
            {
                DateTime d = today.AddDays(-(n - 1 - i));
                days[i].Date = d;
                DayUsage du;
                if (_index.TryGetValue(UsageStore.KeyOf(d), out du))
                {
                    days[i].In = du.In;
                    days[i].Out = du.Out;
                    days[i].HasData = (du.In + du.Out) > 0L;
                }
                days[i].IsToday = (i == n - 1);
            }
            return days;
        }

        private void BuildSummary()
        {
            DateTime today = DateTime.Today;
            DateTime prev = new DateTime(today.Year, today.Month, 1).AddMonths(-1);

            long sumToday = 0L;
            long sumMonth = 0L;
            long sumPrev = 0L;
            long sumAll = 0L;
            long sum30 = 0L;
            int days30 = 0;
            int tracked = 0;
            DateTime windowStart = today.AddDays(-29);

            for (int i = 0; i < _all.Length; i++)
            {
                long t = _all[i].In + _all[i].Out;
                sumAll += t;
                DateTime dt = DateOf(_all[i].Key);
                if (t > 0L) tracked++;
                if (dt.Year == today.Year && dt.Month == today.Month) sumMonth += t;
                if (dt.Year == prev.Year && dt.Month == prev.Month) sumPrev += t;
                if (dt >= windowStart && dt <= today && t > 0L)
                {
                    sum30 += t;
                    days30++;
                }
            }
            DayUsage y;
            if (_index.TryGetValue(UsageStore.KeyOf(today.AddDays(-1)), out y)) sumToday = y.In + y.Out;

            long avg = days30 > 0 ? sum30 / days30 : 0L;

            _cells[0].Key = "YESTERDAY"; _cells[0].Value = sumToday; _cells[0].IsBytes = true;
            _cells[1].Key = "THIS MONTH"; _cells[1].Value = sumMonth; _cells[1].IsBytes = true;
            _cells[2].Key = "LAST MONTH"; _cells[2].Value = sumPrev; _cells[2].IsBytes = true;
            _cells[3].Key = "AVG / DAY \u00B7 30D"; _cells[3].Value = avg; _cells[3].IsBytes = true;
            _cells[4].Key = "ALL TIME"; _cells[4].Value = sumAll; _cells[4].IsBytes = true;
            _cells[5].Key = "DAYS TRACKED"; _cells[5].Value = tracked; _cells[5].IsBytes = false;
        }

        private long TodayTotal()
        {
            long ti, to;
            _store.Today(out ti, out to);
            return ti + to;
        }

        private long MaxTotal()
        {
            long max = 0L;
            for (int i = 0; i < _bars.Length; i++)
            {
                if (!_bars[i].HasData) continue;
                long t = _bars[i].In + _bars[i].Out;
                if (t > max) max = t;
            }
            return max;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            if (TodayTotal() == _lastToday) return;
            RefreshData();
            Render();
        }

        // ------------------------------------------------------------ formatting

        private static void SplitSize(long bytes, out string num, out string unit)
        {
            if (bytes < 0L) bytes = 0L;
            if (bytes >= 1099511627776L)
            {
                num = (bytes / 1099511627776.0).ToString("0.00", CultureInfo.InvariantCulture);
                unit = "TB";
            }
            else if (bytes >= 1073741824L)
            {
                num = (bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture);
                unit = "GB";
            }
            else if (bytes >= 1048576L)
            {
                num = (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture);
                unit = "MB";
            }
            else
            {
                num = (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture);
                unit = "KB";
            }
        }

        private static string SizeText(long bytes)
        {
            string n, u;
            SplitSize(bytes, out n, out u);
            return n + " " + u;
        }

        private static float Ascent(Font f)
        {
            return f.FontFamily.GetCellAscent(f.Style) * f.Size /
                   f.FontFamily.GetEmHeight(f.Style);
        }

        private float DrawSeg(Graphics g, string s, Font f, Brush br, float x, float y)
        {
            if (s.Length == 0) return x;
            g.DrawString(s, f, br, x, y, _fmt);
            return x + g.MeasureString(s, f, PointF.Empty, _fmt).Width;
        }

        private void DrawCentered(Graphics g, string s, Font f, Brush br, float cx, float y)
        {
            float w = g.MeasureString(s, f, PointF.Empty, _fmt).Width;
            g.DrawString(s, f, br, cx - w * 0.5f, y, _fmt);
        }

        private void DrawRight(Graphics g, string s, Font f, Brush br, float right, float y)
        {
            float w = g.MeasureString(s, f, PointF.Empty, _fmt).Width;
            g.DrawString(s, f, br, right - w, y, _fmt);
        }

        // ------------------------------------------------------------ painting

        private void Render()
        {
            if (_g == null || _bmp == null || _path == null) return;
            if (!IsHandleCreated) return;

            Graphics g = _g;
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            g.FillPath(_bg, _path);
            g.DrawPath(_border, _path);

            DrawTitle(g);
            DrawHero(g);
            DrawTabs(g);
            DrawReadout(g);
            DrawChart(g);
            DrawCaps(g);
            DrawGrid(g);
            DrawFooter(g);

            Native.Blit(Handle, _bmp, _x, _y, _w, _h);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Nothing: the layered surface is pushed with UpdateLayeredWindow.
        }

        private void DrawTitle(Graphics g)
        {
            float cy = P(20f);
            g.FillEllipse(_down, P(16f), cy - P(3f), P(6f), P(6f));
            g.DrawString("NetPill \u00B7 Data Usage", _fTitle, _dim2, P(28f), cy - P(8f), _fmt);

            _closeRect = new RectangleF(_w - P(32f), cy - P(10f), P(20f), P(20f));
            if (_zone == Zone.Close)
            {
                using (GraphicsPath hover = RoundedPath(_closeRect, P(5f)))
                {
                    g.FillPath(_band, hover);
                }
            }

            float cx = _closeRect.X + _closeRect.Width * 0.5f;
            float ccy = _closeRect.Y + _closeRect.Height * 0.5f;
            float k = P(3.6f);
            Pen pen = _zone == Zone.Close ? Pens.White : _xPen;
            g.DrawLine(pen, cx - k, ccy - k, cx + k, ccy + k);
            g.DrawLine(pen, cx + k, ccy - k, cx - k, ccy + k);
        }

        private void DrawHero(Graphics g)
        {
            long inB, outB;
            _store.Today(out inB, out outB);
            long tot = inB + outB;

            string num, unit;
            SplitSize(tot, out num, out unit);

            g.DrawString("TODAY", _fLabel, _dim, P(16f), P(47f), _fmt);

            float bigTop = P(57f);
            g.DrawString(num, _fBig, _white, P(16f), bigTop, _fmt);
            float numW = g.MeasureString(num, _fBig, PointF.Empty, _fmt).Width;
            float baseline = bigTop + Ascent(_fBig);
            g.DrawString(unit, _fUnit, _dim2, P(16f) + numW + P(4f), baseline - Ascent(_fUnit), _fmt);

            float y = P(108f);
            float x = P(16f);
            x = DrawSeg(g, "\u2193 " + SizeText(inB), _fBody, _down, x, y);
            x += P(14f);
            DrawSeg(g, "\u2191 " + SizeText(outB), _fBody, _up, x, y);

            // How today compares with a normal day - the one number that gives the bytes meaning.
            long avg = _cells[3].Value;
            if (avg > 0L)
            {
                int pct = (int)Math.Round(tot * 100.0 / avg);
                DrawRight(g, pct.ToString(CultureInfo.InvariantCulture) + "% of your 30-day average",
                    _fBody, _dim3, _w - P(16f), y);
            }
        }

        private void DrawTabs(Graphics g)
        {
            for (int i = 0; i < 3; i++)
            {
                bool on = (i == 0 && _view == 7) || (i == 1 && _view == 30) || (i == 2 && _view == 12);
                using (GraphicsPath pill = RoundedPath(_tabRects[i], P(6f)))
                {
                    if (on) g.FillPath(_band, pill);
                    g.DrawPath(on ? _tabOn : _tabOff, pill);
                }
                DrawCentered(g, TabLabels[i], _fTab, on ? _white : _dim2,
                    _tabRects[i].X + _tabRects[i].Width * 0.5f,
                    _tabRects[i].Y + (_tabRects[i].Height - _fTab.GetHeight(g)) * 0.5f + P(0.5f));
            }
        }

        private void DrawReadout(Graphics g)
        {
            float x = P(16f);
            float y = P(179f);

            if (_hover < 0 || _hover >= _bars.Length)
            {
                DrawSeg(g, "Hover a bar for that day's exact numbers", _fBody, _dim3, x, y);
                return;
            }

            Bar b = _bars[_hover];
            string when;
            if (_view == 12)
            {
                when = b.Date.ToString("MMM yyyy", CultureInfo.InvariantCulture);
            }
            else
            {
                when = b.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture);
            }
            x = DrawSeg(g, when, _fBody, _white, x, y);
            x = DrawSeg(g, "  \u00B7  ", _fBody, _dim3, x, y);

            if (!b.HasData)
            {
                DrawSeg(g, "NetPill was not running", _fBody, _dim, x, y);
                return;
            }
            x = DrawSeg(g, "\u2193 " + SizeText(b.In), _fBody, _down, x, y);
            x = DrawSeg(g, "   ", _fBody, _dim3, x, y);
            x = DrawSeg(g, "\u2191 " + SizeText(b.Out), _fBody, _up, x, y);
            x = DrawSeg(g, "   ", _fBody, _dim3, x, y);
            x = DrawSeg(g, SizeText(b.In + b.Out) + " total", _fBody, _dim2, x, y);
            if (b.IsToday && _view != 12) DrawSeg(g, "  (so far)", _fBody, _dim3, x, y);
        }

        private RectangleF ChartRect()
        {
            return new RectangleF(P(16f), P(200f), _w - P(32f), P(132f));
        }

        private void DrawChart(Graphics g)
        {
            RectangleF r = ChartRect();
            g.DrawLine(_hair2, r.Left, r.Top, r.Right, r.Top);

            int n = _bars.Length;
            if (n == 0) return;

            float slot = r.Width / n;
            float maxW = P(_view == 30 ? 11f : (_view == 12 ? 26f : 32f));
            float bw = Math.Min(maxW, slot - P(3f));
            if (bw < P(2f)) bw = P(2f);

            // Hover band behind the bars.
            if (_hover >= 0 && _hover < n)
            {
                g.FillRectangle(_band, r.Left + _hover * slot, r.Top, slot, r.Height);
            }

            long max = MaxTotal();
            if (max <= 0L) max = 1L;

            for (int i = 0; i < n; i++)
            {
                Bar b = _bars[i];
                float cx = r.Left + i * slot + (slot - bw) * 0.5f;
                float bottom = r.Bottom;

                if (!b.HasData)
                {
                    g.DrawLine(_dash, cx, bottom - P(1f), cx + bw, bottom - P(1f));
                    continue;
                }

                long tot = b.In + b.Out;
                float bh = (float)((double)tot / max) * r.Height;
                if (bh < P(2f)) bh = P(2f);
                float top = bottom - bh;
                float outH = tot > 0L ? (float)((double)b.Out / tot) * bh : 0f;

                if (outH > P(0.6f))
                {
                    using (GraphicsPath p = TopRounded(cx, top, bw, bh, P(2f)))
                    {
                        g.FillPath(_up, p);
                    }
                    g.FillRectangle(_down, cx, top + outH, bw, bh - outH);
                }
                else
                {
                    using (GraphicsPath p = TopRounded(cx, top, bw, bh, P(2f)))
                    {
                        g.FillPath(_down, p);
                    }
                }

                if (b.IsToday)
                {
                    using (GraphicsPath p = TopRounded(cx, top, bw, bh, P(2f)))
                    {
                        g.DrawPath(_today, p);
                    }
                }
            }
        }

        private void DrawCaps(Graphics g)
        {
            int n = _bars.Length;
            if (n == 0) return;

            RectangleF r = ChartRect();
            float slot = r.Width / n;
            float y = P(335f);

            for (int i = 0; i < n; i++)
            {
                Bar b = _bars[i];
                string label = "";
                if (_view == 7)
                {
                    label = b.Date.ToString("ddd d", CultureInfo.InvariantCulture);
                }
                else if (_view == 30)
                {
                    if (i % 5 == 0 || i == n - 1)
                        label = b.Date.Day.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    label = b.Date.ToString("MMM", CultureInfo.InvariantCulture);
                }
                if (label.Length == 0) continue;

                DrawCentered(g, label, b.IsToday ? _fCapB : _fCap,
                    b.IsToday ? _white : _dim3, r.Left + i * slot + slot * 0.5f, y);
            }
        }

        private void DrawGrid(Graphics g)
        {
            float left = P(16f);
            float width = _w - P(32f);
            float cellW = width / 3f;
            float top = P(352f);
            float rowH = P(53f);

            g.DrawLine(_hair, left, top, left + width, top);
            g.DrawLine(_hair2, left, top + rowH, left + width, top + rowH);
            g.DrawLine(_hair2, left + cellW, top, left + cellW, top + rowH * 2f);
            g.DrawLine(_hair2, left + cellW * 2f, top, left + cellW * 2f, top + rowH * 2f);

            for (int i = 0; i < 6; i++)
            {
                int col = i % 3;
                int row = i / 3;
                float cx = left + col * cellW + (col == 0 ? 0f : P(14f));
                float cy = top + row * rowH;

                g.DrawString(_cells[i].Key, _fLabel, _dim3, cx, cy + P(9f), _fmt);

                string num, unit;
                if (_cells[i].IsBytes)
                {
                    SplitSize(_cells[i].Value, out num, out unit);
                }
                else
                {
                    num = _cells[i].Value.ToString(CultureInfo.InvariantCulture);
                    unit = "d";
                }

                float valTop = cy + P(25f);
                g.DrawString(num, _fVal, _white, cx, valTop, _fmt);
                float numW = g.MeasureString(num, _fVal, PointF.Empty, _fmt).Width;
                float baseline = valTop + Ascent(_fVal);
                g.DrawString(unit, _fValU, _dim, cx + numW + P(3f), baseline - Ascent(_fValU), _fmt);
            }
        }

        private void DrawFooter(Graphics g)
        {
            float left = P(16f);
            float right = _w - P(16f);
            float lineY = P(462f);

            g.DrawLine(_hair, left, lineY, right, lineY);

            string since;
            if (_all.Length > 0)
            {
                DateTime first = DateOf(_all[0].Key);
                since = "usage.tsv \u00B7 tracking since " +
                        first.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            }
            else
            {
                since = "usage.tsv \u00B7 no data yet";
            }
            g.DrawString(since, _fFoot, _dim3, left, P(469f), _fmt);

            string open = "Open folder";
            float w = g.MeasureString(open, _fFoot, PointF.Empty, _fmt).Width;
            _openRect = new RectangleF(right - w - P(2f), P(464f), w + P(4f), P(18f));
            if (_zone == Zone.OpenFolder)
            {
                using (GraphicsPath hover = RoundedPath(_openRect, P(4f)))
                {
                    g.FillPath(_band, hover);
                }
            }
            g.DrawString(open, _fFoot, _dim2, right - w, P(469f), _fmt);
            g.DrawLine(_hair2, right - w, P(481f), right, P(481f));
        }

        // ------------------------------------------------------------ input

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            _hitTab = -1;
            _zone = HitTest(e.Location);
            _moved = false;

            if (_zone == Zone.Title)
            {
                _dragging = true;
                _dragScr = Cursor.Position;
                _dragX = _x;
                _dragY = _y;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_dragging && e.Button == MouseButtons.Left)
            {
                Point cp = Cursor.Position;
                int dx = cp.X - _dragScr.X;
                int dy = cp.Y - _dragScr.Y;
                if (!_moved && (Math.Abs(dx) > 2 || Math.Abs(dy) > 2)) _moved = true;
                if (_moved)
                {
                    _x = _dragX + dx;
                    _y = _dragY + dy;
                    Native.SetWindowPos(Handle, IntPtr.Zero, _x, _y, 0, 0,
                        Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                }
                return;
            }

            Zone z = HitTest(e.Location);
            int bar = HitBar(e.Location);
            bool dirty = false;

            if (z != _zone) { _zone = z; dirty = true; }
            if (bar != _hover) { _hover = bar; dirty = true; }
            if (dirty) Render();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            bool wasDrag = _moved;
            Zone z = _zone;
            int tab = _hitTab;

            _dragging = false;
            Capture = false;
            _moved = false;

            if (wasDrag)
            {
                ClampToMonitor();
                _zone = Zone.None;
                Render();
                return;
            }

            if (z == Zone.Close) { Close(); return; }
            if (z == Zone.OpenFolder) { OpenFolder(); return; }
            if (z == Zone.Tab && tab >= 0)
            {
                int want = tab == 0 ? 7 : (tab == 1 ? 30 : 12);
                if (want != _view)
                {
                    _view = want;
                    _hover = -1;
                    RefreshData();
                    Render();
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1)
            {
                _hover = -1;
                Render();
            }
            if (_zone != Zone.None && !_dragging)
            {
                _zone = Zone.None;
                Render();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        private Zone HitTest(Point p)
        {
            if (_closeRect.Contains(p.X, p.Y)) return Zone.Close;
            for (int i = 0; i < 3; i++)
            {
                if (_tabRects[i].Contains(p.X, p.Y))
                {
                    _hitTab = i;
                    return Zone.Tab;
                }
            }
            if (_openRect.Contains(p.X, p.Y)) return Zone.OpenFolder;
            if (p.Y < P(40f)) return Zone.Title;
            return Zone.None;
        }

        private int HitBar(Point p)
        {
            if (_bars.Length == 0) return -1;
            RectangleF r = ChartRect();
            if (p.Y < r.Top - P(8f) || p.Y > r.Bottom + P(8f)) return -1;
            if (p.X < r.Left || p.X >= r.Right) return -1;

            float slot = r.Width / _bars.Length;
            int i = (int)((p.X - r.Left) / slot);
            if (i < 0) i = 0;
            if (i >= _bars.Length) i = _bars.Length - 1;
            return i;
        }

        private void ClampToMonitor()
        {
            Rectangle scr = Screen.FromRectangle(new Rectangle(_x, _y, _w, _h)).WorkingArea;
            if (_x < scr.Left) _x = scr.Left;
            if (_y < scr.Top) _y = scr.Top;
            if (_x + _w > scr.Right) _x = scr.Right - _w;
            if (_y + _h > scr.Bottom) _y = scr.Bottom - _h;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _x, _y, _w, _h,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(UsageStore.Folder);
                Process.Start("explorer.exe", "\"" + UsageStore.Folder + "\"");
            }
            catch (Exception)
            {
                // Explorer missing or blocked: nothing useful to do, and not worth a dialog.
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new MethodInvoker(Relayout));
                return;
            }
            if (m.Msg == WM_ERASEBKGND)
            {
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        private void Relayout()
        {
            if (!IsHandleCreated || IsDisposed) return;
            UpdateMetrics();
            ClampToMonitor();
            Render();
        }
    }

    // ---------------------------------------------------------------- the pill

    internal sealed class PillForm : Form
    {
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_DPICHANGED = 0x02E0;

        private const string ArrowDown = "\u2193 ";
        private const string ArrowUp = "\u2191 ";
        private const string RefValue = "1023.9 MB/s"; // widest string the readout can produce
        private const string RefTotal = "999.9 GB";    // widest today-total the pill can show

        private readonly Settings _s;
        private readonly UsageStore _store;
        private readonly NetSampler _sampler;
        private readonly System.Windows.Forms.Timer _ui;
        private readonly Stopwatch _clock = new Stopwatch();

        private UsageForm _usageForm;

        private NotifyIcon _notify;
        private ToolTip _tip;
        private ContextMenuStrip _menuPill;
        private ContextMenuStrip _menuTray;
        private Icon _trayIcon;
        private string _traySig = "";

        private readonly char[] _bufDown = new char[32];
        private readonly char[] _bufUp = new char[32];
        private readonly char[] _bufTotal = new char[32];
        private readonly StringBuilder _sb = new StringBuilder(512);
        private readonly uint[] _ids = new uint[16];
        private readonly string[] _names = new string[16];
        private readonly double[] _dn = new double[16];
        private readonly double[] _up = new double[16];

        private string _txtDown = "";
        private string _txtUp = "";
        private string _txtTotal = "";
        private string _tipText = "";
        private string _shortDown = "0K";
        private string _shortUp = "0K";

        private Bitmap _bmp;
        private Graphics _g;
        private GraphicsPath _path;
        private SolidBrush _bgBrush;
        private SolidBrush _textBrush;
        private SolidBrush _dimBrush;
        private SolidBrush _downBrush;
        private SolidBrush _upBrush;
        private Pen _borderPen;
        private Pen _sepPen;
        private Font _font;
        private readonly StringFormat _fmt;

        private int _dpi = 96;
        private float _scale = 1f;
        private int _w = 150;
        private int _h = 28;
        private float _padX;
        private float _arrowW;
        private float _lineH;
        private float _textY;
        private float _leftX;
        private float _rightX;
        private float _sepX;
        private float _totX;
        private int _x;
        private int _y;

        private bool _hiddenByFullscreen;
        private int _fsHits;                    // consecutive fullscreen checks (debounce)
        private bool _dragging;
        private int _dragCurX;
        private int _dragCurY;
        private int _dragWinX;
        private int _dragWinY;
        private bool _haveTray;
        private IntPtr _trayHwnd;
        private Native.RECT _trayRect;
        private long _lastFullscreen;
        private long _lastGeom;
        private long _lastTip;
        private bool _closing;

        internal PillForm()
        {
            _s = Settings.Load();
            _store = new UsageStore();
            _sampler = new NetSampler(_store);
            _sampler.Selected = _s.AdapterIndex;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            Text = "NetPill";
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            _fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            _fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip |
                                StringFormatFlags.MeasureTrailingSpaces;

            _ui = new System.Windows.Forms.Timer();
            _ui.Interval = 250;
            _ui.Tick += OnUiTick;

            // Seed the readout so the very first paint shows "0", not blank arrows.
            int zn = FormatRate(_bufDown, 0, 0.0, _s.UseMbps);
            _txtDown = new string(_bufDown, 0, zn);
            zn = FormatRate(_bufUp, 0, 0.0, _s.UseMbps);
            _txtUp = new string(_bufUp, 0, zn);
            zn = FormatTotal(_bufTotal, 0, 0L);
            _txtTotal = new string(_bufTotal, 0, zn);

            _clock.Start();
            _sampler.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style = Native.WS_POPUP;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST |
                              Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
                return cp;
            }
        }

        // ------------------------------------------------------------ lifecycle

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            BuildMenu();
            BuildTray();

            UpdateMetrics();

            if (_s.PositionSet)
            {
                _x = _s.PosX;
                _y = _s.PosY;
                ClampToScreen();
                // Heal a position saved for a taller display or a different taskbar height.
                // Without this, the stale Y pulls the pill straight back under the taskbar on
                // every launch and the taskbar paints over it until the next topmost re-assert.
                if (_x != _s.PosX || _y != _s.PosY)
                {
                    _s.PosX = _x;
                    _s.PosY = _y;
                    _s.Save();
                }
            }
            else
            {
                PositionDefault();
            }

            Native.SetWindowPos(Handle, IntPtr.Zero, _x, _y, _w, _h,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Render();
            ApplyClickThrough();
            ApplyVisibility();

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _ui.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyVisibility(); // honours "tray numbers only" on the very first show
            if (!_s.TrayNumbers) Render();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            if (_usageForm != null && !_usageForm.IsDisposed) _usageForm.Close();
            _store.Flush();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _ui.Stop();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_g != null) { _g.Dispose(); _g = null; }
            if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
            if (_path != null) { _path.Dispose(); _path = null; }
            if (_bgBrush != null) { _bgBrush.Dispose(); _bgBrush = null; }
            if (_textBrush != null) { _textBrush.Dispose(); _textBrush = null; }
            if (_dimBrush != null) { _dimBrush.Dispose(); _dimBrush = null; }
            if (_downBrush != null) { _downBrush.Dispose(); _downBrush = null; }
            if (_upBrush != null) { _upBrush.Dispose(); _upBrush = null; }
            if (_borderPen != null) { _borderPen.Dispose(); _borderPen = null; }
            if (_sepPen != null) { _sepPen.Dispose(); _sepPen = null; }
            if (_font != null) { _font.Dispose(); _font = null; }
            if (_tip != null) { _tip.Dispose(); _tip = null; }
            if (_menuPill != null) { _menuPill.Dispose(); _menuPill = null; }
            if (_menuTray != null) { _menuTray.Dispose(); _menuTray = null; }
            if (_notify != null) { _notify.Visible = false; _notify.Dispose(); _notify = null; }
            if (_trayIcon != null) { _trayIcon.Dispose(); _trayIcon = null; }
            _ui.Dispose();
            _sampler.Dispose();
            _store.Flush(); // the sampler is stopped, so this is the last word on disk
            _fmt.Dispose();
            base.OnFormClosed(e);
        }

        // ------------------------------------------------------------ geometry

        // GetDpiForWindow is Win10 1607+; fall back to 96 dpi rather than fail on older builds.
        private static int DpiOf(IntPtr hwnd)
        {
            try
            {
                uint d = Native.GetDpiForWindow(hwnd);
                if (d >= 72 && d <= 960) return (int)d;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            return 96;
        }

        private void UpdateMetrics()
        {
            int dpi = DpiOf(Handle);
            _dpi = dpi;
            _scale = dpi / 96f;

            if (_font != null) _font.Dispose();
            _font = new Font("Segoe UI Semibold", 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);

            float arrowW;
            float valW;
            float totRefW;
            float lineH;
            using (Graphics mg = Graphics.FromHwnd(IntPtr.Zero))
            {
                mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                arrowW = mg.MeasureString(ArrowDown, _font, PointF.Empty, _fmt).Width;
                valW = mg.MeasureString(RefValue, _font, PointF.Empty, _fmt).Width;
                totRefW = mg.MeasureString(RefTotal, _font, PointF.Empty, _fmt).Width;
                lineH = _font.GetHeight(mg);
            }

            _padX = 9f * _scale;
            _arrowW = (float)Math.Ceiling(arrowW) + 1f * _scale;
            float zoneW = _arrowW + (float)Math.Ceiling(valW) + 1f * _scale;
            float gap = 4f * _scale;

            // Optional third segment: "| 1.7 GB". Measured against the widest possible
            // total, so the pill does not resize as the number grows during the day.
            float totW = 0f;
            if (_s.PillTotal) totW = (float)Math.Ceiling(totRefW) + 1f * _scale;

            float needW = _padX * 2f + zoneW * 2f + gap;
            if (_s.PillTotal) needW += 18f * _scale + totW;

            _w = (int)Math.Ceiling(Math.Max(150f * _scale, needW));
            _h = (int)Math.Ceiling(Math.Max(28f * _scale, lineH + 8f * _scale));
            _leftX = _padX;
            if (_s.PillTotal)
            {
                _totX = _w - _padX - totW;
                _sepX = _totX - 9f * _scale;
                _rightX = _sepX - 9f * _scale - zoneW;
            }
            else
            {
                _rightX = _w - _padX - zoneW;
                _totX = 0f;
                _sepX = 0f;
            }
            _lineH = lineH;
            _textY = (float)Math.Floor((_h - lineH) * 0.5f);

            BuildSurface();
            BuildBrushes();
        }

        private void BuildSurface()
        {
            if (_g != null) { _g.Dispose(); _g = null; }
            if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
            if (_path != null) { _path.Dispose(); _path = null; }

            // Premultiplied alpha: UpdateLayeredWindow(AC_SRC_ALPHA) expects it.
            _bmp = new Bitmap(_w, _h, PixelFormat.Format32bppPArgb);
            _g = Graphics.FromImage(_bmp);
            _path = RoundedPath(0f, 0f, _w, _h, 6f * _scale);
        }

        private void BuildBrushes()
        {
            if (_bgBrush != null) _bgBrush.Dispose();
            if (_textBrush != null) _textBrush.Dispose();
            if (_dimBrush != null) _dimBrush.Dispose();
            if (_downBrush != null) _downBrush.Dispose();
            if (_upBrush != null) _upBrush.Dispose();
            if (_borderPen != null) _borderPen.Dispose();
            if (_sepPen != null) _sepPen.Dispose();

            int alpha = (int)Math.Round(0xCC * (_s.OpacityPercent / 100.0));
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;

            _bgBrush = new SolidBrush(Color.FromArgb(alpha, 26, 26, 26));       // #1A1A1A
            _textBrush = new SolidBrush(Color.White);
            _dimBrush = new SolidBrush(Color.FromArgb(0xB9, 0xB9, 0xB9));
            _downBrush = new SolidBrush(Color.FromArgb(0x4A, 0xDE, 0x80));      // #4ADE80
            _upBrush = new SolidBrush(Color.FromArgb(0xFB, 0xBF, 0x24));        // #FBBF24
            _borderPen = new Pen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 1f * _scale);
            _borderPen.Alignment = PenAlignment.Inset;
            _sepPen = new Pen(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF), 1f * _scale);
        }

        private static GraphicsPath RoundedPath(float x, float y, float w, float h, float r)
        {
            float d = r * 2f;
            if (d > w) d = w;
            if (d > h) d = h;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(x, y, d, d, 180f, 90f);
            p.AddArc(x + w - d, y, d, d, 270f, 90f);
            p.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
            p.AddArc(x, y + h - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        private void PositionDefault()
        {
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.POINT origin = new Native.POINT();
            IntPtr mon = Native.MonitorFromPoint(origin, Native.MONITOR_DEFAULTTOPRIMARY);
            if (!Native.GetMonitorInfoW(mon, ref mi)) return;

            Native.RECT m = mi.rcMonitor;
            Native.RECT work = mi.rcWork;
            int margin = (int)Math.Round(12 * _scale);
            int gap = (int)Math.Round(6 * _scale);

            int edge = 0; // 0 bottom, 1 top, 2 left, 3 right
            Native.RECT tr = new Native.RECT();
            bool haveTray = false;
            IntPtr tray = Native.FindWindowW("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out tr)) haveTray = true;

            if (haveTray && !IsTaskbarAutoHide())
            {
                int tw = tr.Right - tr.Left;
                int th = tr.Bottom - tr.Top;
                if (tw >= th) edge = (tr.Top <= m.Top + 2) ? 1 : 0;
                else edge = (tr.Left <= m.Left + 2) ? 2 : 3;
            }
            else
            {
                if (work.Top > m.Top + 2) edge = 1;
                else if (work.Bottom < m.Bottom - 2) edge = 0;
                else if (work.Left > m.Left + 2) edge = 2;
                else if (work.Right < m.Right - 2) edge = 3;
                else edge = 0;
            }

            // Place against the work area, never the raw monitor rect: the pill parks just
            // clear of the taskbar instead of on top of it.
            switch (edge)
            {
                case 1: // taskbar at top
                    _x = work.Right - _w - margin;
                    _y = work.Top;
                    break;
                case 2: // taskbar at left
                    _x = work.Left;
                    _y = work.Bottom - _h - margin;
                    break;
                case 3: // taskbar at right
                    _x = work.Right - _w;
                    _y = work.Bottom - _h - margin;
                    break;
                default: // taskbar at bottom (and the auto-hide fallback)
                    _x = work.Right - _w - margin;
                    _y = work.Bottom - _h;
                    break;
            }
            ClampToScreen();
        }

        private void ClampToScreen()
        {
            Native.RECT r = new Native.RECT();
            r.Left = _x;
            r.Top = _y;
            r.Right = _x + _w;
            r.Bottom = _y + _h;

            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            IntPtr mon = Native.MonitorFromRect(ref r, Native.MONITOR_DEFAULTTONEAREST);
            if (!Native.GetMonitorInfoW(mon, ref mi)) return;

            // rcWork, not rcMonitor: the work area already excludes the taskbar, so the pill
            // can never be dragged under it (or behind a top/side taskbar).
            int left = mi.rcWork.Left;
            int top = mi.rcWork.Top;
            int right = mi.rcWork.Right - _w;
            int bottom = mi.rcWork.Bottom - _h;
            if (right < left) right = left;     // pill wider than the work area
            if (bottom < top) bottom = top;
            if (_x < left) _x = left;
            if (_y < top) _y = top;
            if (_x > right) _x = right;
            if (_y > bottom) _y = bottom;
        }

        private static bool IsTaskbarAutoHide()
        {
            Native.APPBARDATA d = new Native.APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA));
            IntPtr r = Native.SHAppBarMessage(Native.ABM_GETSTATE, ref d);
            return (r.ToInt32() & Native.ABS_AUTOHIDE) != 0;
        }

        private void Relayout()
        {
            if (!IsHandleCreated || _closing) return;
            UpdateMetrics();
            if (!_s.PositionSet) PositionDefault();
            else ClampToScreen();
            Native.SetWindowPos(Handle, IntPtr.Zero, _x, _y, _w, _h,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Render();
            _traySig = "";
            UpdateTrayIcon();
        }

        // ------------------------------------------------------------ painting

        private void Render()
        {
            if (_g == null || _bmp == null || _path == null) return;

            Graphics g = _g;
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            g.FillPath(_bgBrush, _path);
            g.DrawPath(_borderPen, _path);

            float y = _textY;
            g.DrawString(ArrowDown, _font, _downBrush, _leftX, y, _fmt);
            g.DrawString(_txtDown, _font, _textBrush, _leftX + _arrowW, y, _fmt);
            g.DrawString(ArrowUp, _font, _upBrush, _rightX, y, _fmt);
            g.DrawString(_txtUp, _font, _textBrush, _rightX + _arrowW, y, _fmt);

            if (_s.PillTotal)
            {
                g.DrawLine(_sepPen, _sepX, y + 2f * _scale, _sepX, y + _lineH - 2f * _scale);
                g.DrawString(_txtTotal, _font, _dimBrush, _totX, y, _fmt);
            }

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = _bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(memDc, hBmp);

            Native.SIZE size;
            size.cx = _w;
            size.cy = _h;
            Native.POINT dst;
            dst.X = _x;
            dst.Y = _y;
            Native.POINT src;
            src.X = 0;
            src.Y = 0;
            Native.BLENDFUNCTION blend;
            blend.BlendOp = Native.AC_SRC_OVER;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = Native.AC_SRC_ALPHA;

            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend,
                (int)Native.ULW_ALPHA);

            Native.SelectObject(memDc, old);
            Native.DeleteObject(hBmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Nothing: the layered surface is pushed with UpdateLayeredWindow.
        }

        // ------------------------------------------------------------ formatting

        private static int AppendLong(char[] b, int p, long v)
        {
            if (v == 0)
            {
                b[p++] = '0';
                return p;
            }
            int start = p;
            while (v > 0)
            {
                b[p++] = (char)('0' + (int)(v % 10));
                v /= 10;
            }
            for (int i = start, j = p - 1; i < j; i++, j--)
            {
                char c = b[i];
                b[i] = b[j];
                b[j] = c;
            }
            return p;
        }

        private static int AppendTenths(char[] b, int p, double v)
        {
            long t = (long)(v * 10.0 + 0.5);
            if (t < 0) t = 0;
            p = AppendLong(b, p, t / 10);
            long frac = t % 10;
            if (frac != 0)
            {
                b[p++] = '.';
                b[p++] = (char)('0' + (int)frac);
            }
            return p;
        }

        private static int AppendZero(char[] b, int p, bool mbps)
        {
            b[p++] = '0';
            b[p++] = ' ';
            b[p++] = 'K';
            if (mbps)
            {
                b[p++] = 'b';
                b[p++] = 'p';
                b[p++] = 's';
            }
            else
            {
                b[p++] = 'B';
                b[p++] = '/';
                b[p++] = 's';
            }
            return p;
        }

        private static char UnitChar(int u)
        {
            return u == 1 ? 'K' : (u == 2 ? 'M' : 'G');
        }

        // Writes "12.4 MB/s" / "99.3 Mbps" / "0 KB/s" into a reusable buffer. No allocation.
        private static int FormatRate(char[] b, int p, double bps, bool mbps)
        {
            if (bps < 0.0) bps = 0.0;
            double v;
            int u; // 1 = K, 2 = M, 3 = G

            if (mbps)
            {
                double bits = bps * 8.0;
                if (bits < 1000.0) return AppendZero(b, p, true);
                v = bits / 1000.0;
                u = 1;
                // Rounding to one decimal can turn 999.96 into "1000": carry it up a unit so
                // the readout never shows "1000 Mbps" where "1 Gbps" belongs.
                while (u < 3 && v >= 999.95) { v /= 1000.0; u++; }
                p = AppendTenths(b, p, v);
                b[p++] = ' ';
                b[p++] = UnitChar(u);
                b[p++] = 'b';
                b[p++] = 'p';
                b[p++] = 's';
                return p;
            }

            if (bps < 1024.0) return AppendZero(b, p, false);
            v = bps / 1024.0;
            u = 1;
            while (u < 3 && v >= 1023.95) { v /= 1024.0; u++; }
            p = AppendTenths(b, p, v);
            b[p++] = ' ';
            b[p++] = UnitChar(u);
            b[p++] = 'B';
            b[p++] = '/';
            b[p++] = 's';
            return p;
        }

        // "812 MB" / "1.7 GB" / "1.2 TB" for the optional today-total segment.
        private static int FormatTotal(char[] b, int p, long bytes)
        {
            if (bytes < 0L) bytes = 0L;

            if (bytes < 1048576L)
            {
                p = AppendLong(b, p, bytes / 1024L);
                b[p++] = ' ';
                b[p++] = 'K';
                b[p++] = 'B';
                return p;
            }
            if (bytes < 1073741824L)
            {
                p = AppendLong(b, p, bytes / 1048576L);
                b[p++] = ' ';
                b[p++] = 'M';
                b[p++] = 'B';
                return p;
            }

            double g = bytes / 1073741824.0;
            if (g >= 1023.95)
            {
                p = AppendTenths(b, p, g / 1024.0);
                b[p++] = ' ';
                b[p++] = 'T';
                b[p++] = 'B';
                return p;
            }
            p = AppendTenths(b, p, g);
            b[p++] = ' ';
            b[p++] = 'G';
            b[p++] = 'B';
            return p;
        }

        private static bool TextEquals(string s, char[] b, int len)
        {
            if (s == null || s.Length != len) return false;
            for (int i = 0; i < len; i++)
            {
                if (s[i] != b[i]) return false;
            }
            return true;
        }

        // Compact form for the 16x16 tray icon: "12M", "1.2M", "980K".
        private static string ShortRate(double bps, bool mbps)
        {
            double v;
            int u;
            if (mbps)
            {
                double bits = bps * 8.0;
                if (bits < 1000.0) return "0K";
                v = bits / 1000.0;
                u = 1;
                while (u < 3 && v >= 999.5) { v /= 1000.0; u++; }
            }
            else
            {
                if (bps < 1024.0) return "0K";
                v = bps / 1024.0;
                u = 1;
                while (u < 3 && v >= 999.5) { v /= 1024.0; u++; }
            }
            string num = v < 9.95
                ? v.ToString("0.0", CultureInfo.InvariantCulture)
                : ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
            return num + UnitChar(u);
        }

        private void AppendRate(StringBuilder sb, double bps)
        {
            int n = FormatRate(_bufDown, 0, bps, _s.UseMbps);
            sb.Append(_bufDown, 0, n);
        }

        private void AppendTotal(StringBuilder sb, long bytes)
        {
            int n = FormatTotal(_bufTotal, 0, bytes);
            sb.Append(_bufTotal, 0, n);
        }

        // ------------------------------------------------------------ ui tick

        private void OnUiTick(object sender, EventArgs e)
        {
            if (_closing) return;

            double dn = _sampler.DownBps;
            double up = _sampler.UpBps;
            bool changed = false;

            int n = FormatRate(_bufDown, 0, dn, _s.UseMbps);
            if (!TextEquals(_txtDown, _bufDown, n))
            {
                _txtDown = new string(_bufDown, 0, n);
                changed = true;
            }
            n = FormatRate(_bufUp, 0, up, _s.UseMbps);
            if (!TextEquals(_txtUp, _bufUp, n))
            {
                _txtUp = new string(_bufUp, 0, n);
                changed = true;
            }

            if (_s.PillTotal)
            {
                long ti, to;
                _store.Today(out ti, out to);
                n = FormatTotal(_bufTotal, 0, ti + to);
                if (!TextEquals(_txtTotal, _bufTotal, n))
                {
                    _txtTotal = new string(_bufTotal, 0, n);
                    changed = true;
                }
            }

            if (changed)
            {
                if (!_s.TrayNumbers) Render();
                if (_s.TrayNumbers)
                {
                    _shortDown = ShortRate(dn, _s.UseMbps);
                    _shortUp = ShortRate(up, _s.UseMbps);
                    UpdateTrayIcon();
                }
            }

            long ms = _clock.ElapsedMilliseconds;
            if (ms - _lastFullscreen >= 1000)
            {
                _lastFullscreen = ms;
                CheckFullscreen();
            }
            if (ms - _lastGeom >= 5000)
            {
                _lastGeom = ms;
                CheckGeometry();
            }
            if (ms - _lastTip >= 900)
            {
                _lastTip = ms;
                UpdateTooltip();
            }
        }

        private void CheckFullscreen()
        {
            // Debounce: hide only after a real fullscreen window has held for three consecutive
            // one-second checks, and come back the instant it goes away. Alt-tab, the desktop,
            // a UAC dim or the text-input host can no longer make the pill blink out.
            bool fs = IsForegroundFullscreen();
            if (fs)
            {
                if (_fsHits < 3) _fsHits++;
            }
            else
            {
                _fsHits = 0;
            }

            bool hide = _fsHits >= 3;
            if (hide == _hiddenByFullscreen) return;
            _hiddenByFullscreen = hide;
            ApplyVisibility();
            if (!hide && !_s.TrayNumbers) Render();
        }

        private bool IsForegroundFullscreen()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return false;
            if (!Native.IsWindow(fg) || !Native.IsWindowVisible(fg)) return false;

            // The desktop, the taskbar and the touch-keyboard host all report a monitor-sized
            // rectangle, so a pure geometry test calls them "fullscreen". Clicking the desktop
            // used to hide the pill until some other window was activated. None of them is a
            // real fullscreen app, so they are filtered out before the rect check.
            if (IsShellWindow(fg)) return false;

            Native.RECT fgr;
            if (!Native.GetWindowRect(fg, out fgr)) return false;

            // Get the monitor the pill is on.
            Native.RECT pillR = new Native.RECT();
            pillR.Left = _x; pillR.Top = _y;
            pillR.Right = _x + _w; pillR.Bottom = _y + _h;
            IntPtr mon = Native.MonitorFromRect(ref pillR, Native.MONITOR_DEFAULTTONEAREST);
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (!Native.GetMonitorInfoW(mon, ref mi)) return false;

            Native.RECT m = mi.rcMonitor;
            int tol = 8; // pixel tolerance for borderless / shadow etc.
            bool covers =
                fgr.Left <= m.Left + tol &&
                fgr.Top <= m.Top + tol &&
                fgr.Right >= m.Right - tol &&
                fgr.Bottom >= m.Bottom - tol;
            return covers;
        }

        // Shell surfaces that happen to be monitor-sized but must never count as fullscreen.
        private static bool IsShellWindow(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(64);
            if (Native.GetClassNameW(hwnd, sb, 64) <= 0) return false;
            string cls = sb.ToString();

            if (cls == "Progman" || cls == "WorkerW" ||
                cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;

            // Windows.UI.Core.CoreWindow covers the whole monitor for the touch keyboard /
            // handwriting panel, which pops up whenever a text field is focused.
            if (cls == "Windows.UI.Core.CoreWindow")
            {
                StringBuilder t = new StringBuilder(128);
                if (Native.GetWindowTextW(hwnd, t, 128) > 0)
                    return t.ToString() == "Microsoft Text Input Application";
            }
            return false;
        }

        private void CheckGeometry()
        {
            // Keep the pill above the taskbar (which is also topmost).
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            Native.RECT r = new Native.RECT();
            IntPtr tray = Native.FindWindowW("Shell_TrayWnd", null);
            bool ok = tray != IntPtr.Zero && Native.GetWindowRect(tray, out r);

            bool trayChanged = (ok != _haveTray) || (tray != _trayHwnd) ||
                (ok && (r.Left != _trayRect.Left || r.Top != _trayRect.Top ||
                        r.Right != _trayRect.Right || r.Bottom != _trayRect.Bottom));
            _haveTray = ok;
            _trayHwnd = tray;
            _trayRect = r;

            int dpi = DpiOf(Handle);
            if (dpi != _dpi)
            {
                Relayout();
                return;
            }

            if (trayChanged && !_s.PositionSet)
            {
                int ox = _x;
                int oy = _y;
                PositionDefault();
                if (_x != ox || _y != oy)
                {
                    Native.SetWindowPos(Handle, IntPtr.Zero, _x, _y, _w, _h,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                    Render();
                }
            }
        }

        private void UpdateTooltip()
        {
            if (_tip == null) return;
            Point cp = PointToClient(Cursor.Position);
            if (cp.X < 0 || cp.Y < 0 || cp.X >= _w || cp.Y >= _h) return;

            int count = _sampler.CopyAdapters(_ids, _names, _dn, _up);
            _sb.Length = 0;
            if (count == 0)
            {
                _sb.Append("No active network adapter");
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) _sb.Append('\n');
                    _sb.Append(_names[i]);
                    _sb.Append(":  ");
                    _sb.Append(ArrowDown);
                    AppendRate(_sb, _dn[i]);
                    _sb.Append("   ");
                    _sb.Append(ArrowUp);
                    AppendRate(_sb, _up[i]);
                }
            }

            // Today's running total, so the feature is discoverable from the pill alone.
            long todayIn, todayOut;
            _store.Today(out todayIn, out todayOut);
            _sb.Append('\n');
            _sb.Append("Today:  ");
            _sb.Append(ArrowDown);
            AppendTotal(_sb, todayIn);
            _sb.Append("   ");
            _sb.Append(ArrowUp);
            AppendTotal(_sb, todayOut);

            string text = _sb.ToString();
            if (text != _tipText)
            {
                _tipText = text;
                _tip.SetToolTip(this, text);
            }
        }

        // ------------------------------------------------------------ visibility

        private void ApplyVisibility()
        {
            bool show = !_s.TrayNumbers && !(_s.HideFullscreen && _hiddenByFullscreen);
            if (show)
            {
                Native.ShowWindow(Handle, Native.SW_SHOWNA);
                // Keep the pill above the taskbar (which is also topmost).
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            else
            {
                Native.ShowWindow(Handle, Native.SW_HIDE);
            }
        }

        private void ApplyClickThrough()
        {
            int ex = Native.GetExStyle(Handle);
            int want = _s.ClickThrough
                ? (ex | Native.WS_EX_TRANSPARENT)
                : (ex & ~Native.WS_EX_TRANSPARENT);
            if (want != ex) Native.SetExStyle(Handle, want);
        }

        // ------------------------------------------------------------ tray icon

        private void BuildTray()
        {
            _tip = new ToolTip();
            _tip.InitialDelay = 600;
            _tip.ReshowDelay = 200;
            _tip.AutoPopDelay = 30000;
            _tip.UseFading = false;
            _tip.UseAnimation = false;

            _notify = new NotifyIcon();
            _notify.Text = "NetPill";
            _trayIcon = MakeTrayIcon();
            _notify.Icon = _trayIcon;
            _notify.ContextMenuStrip = _menuTray;
            _notify.MouseDoubleClick += OnTrayDoubleClick;
            _notify.Visible = true;
            _traySig = _s.TrayNumbers ? (_shortDown + "|" + _shortUp) : "pill";
        }

        private void OnTrayDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            SetTrayNumbers(!_s.TrayNumbers);
        }

        private void UpdateTrayIcon()
        {
            if (_notify == null) return;
            string sig = _s.TrayNumbers ? (_shortDown + "|" + _shortUp) : "pill";
            if (sig == _traySig) return;
            _traySig = sig;

            Icon next = MakeTrayIcon();
            Icon old = _trayIcon;
            _trayIcon = next;
            _notify.Icon = next;
            if (old != null) old.Dispose();
        }

        private Icon MakeTrayIcon()
        {
            int s = SystemInformation.SmallIconSize.Width;
            if (s < 16) s = 16;

            Icon result;
            using (Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

                using (SolidBrush bg = new SolidBrush(Color.FromArgb(255, 26, 26, 26)))
                using (GraphicsPath p = RoundedPath(0f, 0f, s, s, s * 0.26f))
                {
                    g.FillPath(bg, p);
                }

                if (_s.TrayNumbers)
                {
                    using (Font f = new Font("Segoe UI", s * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush d = new SolidBrush(Color.FromArgb(0x4A, 0xDE, 0x80)))
                    using (SolidBrush u = new SolidBrush(Color.FromArgb(0xFB, 0xBF, 0x24)))
                    {
                        g.DrawString(_shortDown, f, d, -s * 0.03f, -s * 0.07f);
                        g.DrawString(_shortUp, f, u, -s * 0.03f, s * 0.48f);
                    }
                }
                else
                {
                    using (Font f = new Font("Segoe UI", s * 0.58f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush d = new SolidBrush(Color.FromArgb(0x4A, 0xDE, 0x80)))
                    using (SolidBrush u = new SolidBrush(Color.FromArgb(0xFB, 0xBF, 0x24)))
                    {
                        g.DrawString(ArrowDown, f, d, -s * 0.04f, -s * 0.02f);
                        g.DrawString(ArrowUp, f, u, s * 0.40f, s * 0.34f);
                    }
                }

                IntPtr h = bmp.GetHicon();
                result = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
            }
            return result;
        }

        // ------------------------------------------------------------ menu

        private void BuildMenu()
        {
            _menuPill = new ContextMenuStrip();
            _menuPill.ShowImageMargin = false;
            _menuPill.Font = SystemFonts.MenuFont;
            _menuPill.Opening += OnPillMenuOpening;

            _menuTray = new ContextMenuStrip();
            _menuTray.ShowImageMargin = false;
            _menuTray.Font = SystemFonts.MenuFont;
            _menuTray.Opening += OnTrayMenuOpening;
        }

        private void OnPillMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            FillMenu(_menuPill, false);
        }

        private void OnTrayMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            FillMenu(_menuTray, true);
        }

        private void ClearMenu(ContextMenuStrip menu)
        {
            while (menu.Items.Count > 0) menu.Items[0].Dispose();
        }

        private ToolStripMenuItem Item(string text, bool checkable, bool isChecked, EventHandler onClick)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text);
            mi.Checked = checkable && isChecked;
            if (onClick != null) mi.Click += onClick;
            return mi;
        }

        private void FillMenu(ContextMenuStrip menu, bool fromTray)
        {
            ClearMenu(menu);

            menu.Items.Add(Item("Data usage\u2026", false, false, OnDataUsage));
            menu.Items.Add(new ToolStripSeparator());

            // Units
            ToolStripMenuItem units = new ToolStripMenuItem("Units");
            units.DropDownItems.Add(Item("MB/s", true, !_s.UseMbps, OnUnitsMb));
            units.DropDownItems.Add(Item("Mbps", true, _s.UseMbps, OnUnitsMbps));
            menu.Items.Add(units);

            // Adapters
            ToolStripMenuItem adapters = new ToolStripMenuItem("Adapters");
            int count = _sampler.CopyAdapters(_ids, _names, _dn, _up);
            int selected = _sampler.Selected;
            bool selectedPresent = false;
            for (int i = 0; i < count; i++)
            {
                if ((int)_ids[i] == selected) { selectedPresent = true; break; }
            }
            adapters.DropDownItems.Add(Item("All adapters", true, selected == 0 || !selectedPresent, OnAdapterAll));
            for (int i = 0; i < count; i++)
            {
                uint id = _ids[i];
                string name = _names[i];
                ToolStripMenuItem mi = new ToolStripMenuItem(name);
                mi.Checked = (selected == (int)id);
                mi.Click += delegate { SetAdapter((int)id); };
                adapters.DropDownItems.Add(mi);
            }
            if (count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("(none active)");
                none.Enabled = false;
                adapters.DropDownItems.Add(none);
            }
            menu.Items.Add(adapters);

            // Click-through
            menu.Items.Add(Item("Click-through", true, _s.ClickThrough, OnClickThrough));

            // Fullscreen auto-hide
            menu.Items.Add(Item("Hide in fullscreen", true, _s.HideFullscreen, OnHideFullscreen));

            // Today's total in the pill
            menu.Items.Add(Item("Show today's total", true, _s.PillTotal, OnPillTotal));

            // Start with Windows
            menu.Items.Add(Item("Start with Windows", true, Settings.IsStartWithWindows(), OnStartWithWindows));

            // Opacity
            ToolStripMenuItem opacity = new ToolStripMenuItem("Opacity");
            opacity.DropDownItems.Add(Item("100%", true, _s.OpacityPercent == 100, OnOpacity100));
            opacity.DropDownItems.Add(Item("90%", true, _s.OpacityPercent == 90, OnOpacity90));
            opacity.DropDownItems.Add(Item("80%", true, _s.OpacityPercent == 80, OnOpacity80));
            opacity.DropDownItems.Add(Item("70%", true, _s.OpacityPercent == 70, OnOpacity70));
            menu.Items.Add(opacity);

            menu.Items.Add(Item("Exit", false, false, OnExit));

            if (fromTray)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(Item("Tray numbers only", true, _s.TrayNumbers, OnTrayNumbers));
            }
        }

        private void OnUnitsMb(object sender, EventArgs e) { SetUnits(false); }
        private void OnUnitsMbps(object sender, EventArgs e) { SetUnits(true); }
        private void OnAdapterAll(object sender, EventArgs e) { SetAdapter(0); }
        private void OnClickThrough(object sender, EventArgs e) { SetClickThrough(!_s.ClickThrough); }
        private void OnHideFullscreen(object sender, EventArgs e) { SetHideFullscreen(!_s.HideFullscreen); }
        private void OnPillTotal(object sender, EventArgs e) { SetPillTotal(!_s.PillTotal); }
        private void OnDataUsage(object sender, EventArgs e) { ShowUsage(); }
        private void OnStartWithWindows(object sender, EventArgs e) { Settings.SetStartWithWindows(!Settings.IsStartWithWindows()); }
        private void OnOpacity100(object sender, EventArgs e) { SetOpacity(100); }
        private void OnOpacity90(object sender, EventArgs e) { SetOpacity(90); }
        private void OnOpacity80(object sender, EventArgs e) { SetOpacity(80); }
        private void OnOpacity70(object sender, EventArgs e) { SetOpacity(70); }
        private void OnTrayNumbers(object sender, EventArgs e) { SetTrayNumbers(!_s.TrayNumbers); }
        private void OnExit(object sender, EventArgs e) { Close(); }

        private void SetUnits(bool mbps)
        {
            if (_s.UseMbps == mbps) return;
            _s.UseMbps = mbps;
            _s.Save();
            _txtDown = "";
            _txtUp = "";
            _traySig = "";
            OnUiTick(null, EventArgs.Empty);
            if (_s.TrayNumbers) UpdateTrayIcon();
        }

        private void SetAdapter(int index)
        {
            if (_s.AdapterIndex == index) return;
            _s.AdapterIndex = index;
            _s.Save();
            _sampler.Selected = index;
            _txtDown = "";
            _txtUp = "";
            OnUiTick(null, EventArgs.Empty);
        }

        private void SetClickThrough(bool on)
        {
            _s.ClickThrough = on;
            _s.Save();
            ApplyClickThrough();
        }

        private void SetPillTotal(bool on)
        {
            if (_s.PillTotal == on) return;
            _s.PillTotal = on;
            _s.Save();
            if (on)
            {
                long ti, to;
                _store.Today(out ti, out to);
                int n = FormatTotal(_bufTotal, 0, ti + to);
                _txtTotal = new string(_bufTotal, 0, n);
            }
            else
            {
                _txtTotal = "";
            }
            Relayout(); // the pill gets wider: metrics, position and surface all change
        }

        // ------------------------------------------------------------ usage window

        private void ShowUsage()
        {
            if (_usageForm != null && !_usageForm.IsDisposed)
            {
                if (_usageForm.Visible)
                {
                    _usageForm.Activate();
                    return;
                }
                _usageForm.Dispose();
                _usageForm = null;
            }
            _usageForm = new UsageForm(_store);
            _usageForm.ShowNear(new Rectangle(_x, _y, _w, _h));
        }

        private void ToggleUsage()
        {
            if (_usageForm != null && !_usageForm.IsDisposed && _usageForm.Visible)
            {
                _usageForm.Close();
                return;
            }
            ShowUsage();
        }

        private void SetOpacity(int percent)
        {
            if (_s.OpacityPercent == percent) return;
            _s.OpacityPercent = percent;
            _s.Save();
            BuildBrushes();
            Render();
        }

        private void SetHideFullscreen(bool on)
        {
            if (_s.HideFullscreen == on) return;
            _s.HideFullscreen = on;
            _s.Save();
            if (!on && _hiddenByFullscreen)
            {
                _hiddenByFullscreen = false;
                _fsHits = 0;
                ApplyVisibility();
                Render();
            }
        }

        private void SetTrayNumbers(bool on)
        {
            if (_s.TrayNumbers == on) return;
            _s.TrayNumbers = on;
            _s.Save();
            _traySig = "";
            _shortDown = ShortRate(_sampler.DownBps, _s.UseMbps);
            _shortUp = ShortRate(_sampler.UpBps, _s.UseMbps);
            UpdateTrayIcon();
            ApplyVisibility();
            if (!on) Render();
        }

        // ------------------------------------------------------------ input

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_LBUTTONDOWN:
                    BeginDrag();
                    return;
                case WM_MOUSEMOVE:
                    if (_dragging) ContinueDrag();
                    break;
                case WM_LBUTTONUP:
                    if (_dragging) EndDrag();
                    break;
                case WM_RBUTTONUP:
                    if (!_s.ClickThrough) _menuPill.Show(Cursor.Position);
                    return;
                case WM_DPICHANGED:
                    // We own our size and position: swallow the default handling.
                    if (IsHandleCreated && !_closing) BeginInvoke(new MethodInvoker(Relayout));
                    return;
                case WM_ERASEBKGND:
                    m.Result = (IntPtr)1;
                    return;
            }
            base.WndProc(ref m);
        }

        private void BeginDrag()
        {
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;
            _dragging = true;
            _dragCurX = p.X;
            _dragCurY = p.Y;
            _dragWinX = _x;
            _dragWinY = _y;
            Native.SetCapture(Handle);
        }

        private void ContinueDrag()
        {
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return;
            int nx = _dragWinX + (p.X - _dragCurX);
            int ny = _dragWinY + (p.Y - _dragCurY);
            int oldX = _x;
            int oldY = _y;
            _x = nx;
            _y = ny;
            ClampToScreen(); // live clamp: the pill stops at the taskbar edge instead of sliding under it
            if (_x == oldX && _y == oldY) return;
            // Move only: the layered surface travels with the window, so no repaint.
            Native.SetWindowPos(Handle, IntPtr.Zero, _x, _y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        private void EndDrag()
        {
            _dragging = false;
            Native.ReleaseCapture();

            // A click is a drag that never moved: open (or close) the history window.
            if (_x == _dragWinX && _y == _dragWinY)
            {
                Render();
                ToggleUsage();
                return;
            }

            ClampToScreen();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _x, _y, _w, _h,
                Native.SWP_NOACTIVATE);
            Render();
            _s.PositionSet = true;
            _s.PosX = _x;
            _s.PosY = _y;
            _s.Save();
        }

        private void RunOnUi(MethodInvoker action)
        {
            if (_closing || IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (InvalidOperationException)
            {
                // Also covers ObjectDisposedException (a subclass): the form can be torn down
                // while a SystemEvents callback is still in flight.
            }
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            RunOnUi(Relayout);
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General ||
                e.Category == UserPreferenceCategory.Desktop ||
                e.Category == UserPreferenceCategory.Window)
            {
                RunOnUi(Relayout);
            }
        }
    }
}
