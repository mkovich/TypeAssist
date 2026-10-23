using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TypeAssist
{
    /// <summary>One key event to inject: a virtual key, or a Unicode character when Vk == 0.</summary>
    internal struct KeyAction
    {
        public int Vk;
        public char Ch;
        public bool Up;
    }

    /// <summary>Everything the input tracker needs from Windows (swapped for a fake in tests).</summary>
    internal interface IKeyPlatform
    {
        bool IsDown(int vk);
        IntPtr Foreground();
        string ProcessName(IntPtr hwnd);
        bool IsFullScreen(IntPtr hwnd);
        /// <summary>Character(s) the key produces with the given layout; null = dead key, "" = nothing.</summary>
        string Translate(uint vk, uint scan, bool shift, bool caps, IntPtr hkl);
        /// <summary>The layout Windows reports for the focused window (not always right, see InputTracker).</summary>
        IntPtr ReportedLayout();
        /// <summary>An installed Persian layout and an installed Latin (English) layout, or IntPtr.Zero.</summary>
        IntPtr PersianLayout { get; }
        IntPtr EnglishLayout { get; }
        IntPtr[] InstalledLayouts();
        void Send(List<KeyAction> actions);
    }

    internal sealed class WinPlatform : IKeyPlatform
    {
        static readonly IntPtr Magic = new IntPtr(0x54415353);
        readonly byte[] keyState = new byte[256];
        readonly StringBuilder buf = new StringBuilder(8);

        public bool IsDown(int vk) { return Native.IsDown(vk); }
        public IntPtr Foreground() { return Native.GetForegroundWindow(); }

        public string ProcessName(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == Native.GetCurrentProcessId()) return "typeassist";
            return Native.ProcessName(pid);
        }

        public bool IsFullScreen(IntPtr hwnd)
        {
            if (hwnd == Native.GetShellWindow() || hwnd == Native.GetDesktopWindow()) return false;
            string cls = Native.ClassName(hwnd);
            if (cls == "WorkerW" || cls == "Progman") return false;
            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return false;
            var mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, 2), ref mi)) return false;
            // Only true full screen (games, F11): exactly the monitor. Maximised frameless apps such as
            // Telegram, Discord or Chrome are slightly larger than the work area and must not match.
            return r.Left == mi.rcMonitor.Left && r.Top == mi.rcMonitor.Top
                && r.Right == mi.rcMonitor.Right && r.Bottom == mi.rcMonitor.Bottom
                && !(mi.rcWork.Bottom == mi.rcMonitor.Bottom && mi.rcWork.Right == mi.rcMonitor.Right
                     && mi.rcWork.Top == mi.rcMonitor.Top && mi.rcWork.Left == mi.rcMonitor.Left && Native.ClassName(hwnd).StartsWith("Chrome_"));
        }

        public string Translate(uint vk, uint scan, bool shift, bool caps, IntPtr hkl)
        {
            Array.Clear(keyState, 0, 256);
            if (shift) keyState[Native.VK_SHIFT] = 0x80;
            if (Native.IsDown(Native.VK_CONTROL)) keyState[Native.VK_CONTROL] = 0x80;
            if (Native.IsDown(Native.VK_MENU)) keyState[Native.VK_MENU] = 0x80;
            if (Native.IsDown(Native.VK_RMENU)) keyState[Native.VK_RMENU] = 0x80;
            if (caps) keyState[Native.VK_CAPITAL] = 0x01;
            buf.Length = 0;
            // flag 4: do not change the keyboard state (keeps dead keys working in the target app)
            int n = Native.ToUnicodeEx(vk, scan, keyState, buf, buf.Capacity, 4, hkl);
            if (n < 0) return null;
            if (n == 0) return "";
            return buf.ToString(0, Math.Min(n, buf.Length));
        }

        IntPtr lastHkl;

        /// <summary>
        /// Keyboard layout of the control that has the focus. The foreground window's thread is not always the
        /// thread that receives the keys, so ask for the focused window.
        /// </summary>
        public IntPtr ReportedLayout()
        {
            uint pid;
            IntPtr fg = Native.GetForegroundWindow();
            uint fgThread = Native.GetWindowThreadProcessId(fg, out pid);
            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            uint thread = fgThread;
            if (Native.GetGUIThreadInfo(fgThread, ref gti) && gti.hwndFocus != IntPtr.Zero)
                thread = Native.GetWindowThreadProcessId(gti.hwndFocus, out pid);
            IntPtr hkl = Native.GetKeyboardLayout(thread);
            if (hkl == IntPtr.Zero) hkl = Native.GetKeyboardLayout(fgThread);
            if (hkl == IntPtr.Zero) hkl = Native.GetKeyboardLayout(0);
            if (hkl != lastHkl)
            {
                lastHkl = hkl;
                Log.W("reported keyboard layout: " + Describe(hkl));
            }
            return hkl;
        }

        public static string Describe(IntPtr hkl)
        {
            long v = (long)hkl & 0xFFFFFFFF;
            string lang = (v & 0xFFFF) == 0x0429 ? "Persian" : (v & 0xFF) == 0x09 ? "English" : "other";
            return "0x" + v.ToString("X8") + " (" + lang + ")";
        }

        IntPtr fa, en;
        IntPtr[] all = new IntPtr[0];
        public IntPtr[] InstalledLayouts() { RefreshLayouts(); return all; }
        int layoutsTick = int.MinValue;

        void RefreshLayouts()
        {
            if (Environment.TickCount - layoutsTick < 30000 && layoutsTick != int.MinValue) return;
            layoutsTick = Environment.TickCount;
            int n = Native.GetKeyboardLayoutList(0, null);
            if (n <= 0) return;
            var list = new IntPtr[n];
            Native.GetKeyboardLayoutList(n, list);
            IntPtr f = IntPtr.Zero, e = IntPtr.Zero, latin = IntPtr.Zero;
            foreach (IntPtr h in list)
            {
                long v = (long)h & 0xFFFF;
                if (v == 0x0429 && f == IntPtr.Zero) f = h;
                else if ((v & 0xFF) == 0x09 && e == IntPtr.Zero) e = h;
                else if (latin == IntPtr.Zero && v != 0x0429) latin = h;
            }
            if (e == IntPtr.Zero) e = latin;
            if (f != fa || e != en || all.Length != n)
            {
                var names = new List<string>();
                foreach (IntPtr h in list) names.Add(Describe(h));
                Log.W("installed layouts: " + string.Join(", ", names.ToArray()));
            }
            fa = f; en = e; all = list;
        }

        public IntPtr PersianLayout { get { RefreshLayouts(); return fa; } }
        public IntPtr EnglishLayout { get { RefreshLayouts(); return en; } }

        public void Send(List<KeyAction> actions)
        {
            var arr = new Native.INPUT[actions.Count];
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                var inp = new Native.INPUT { type = Native.INPUT_KEYBOARD };
                inp.u.ki.dwExtraInfo = Magic;
                if (a.Vk != 0)
                {
                    inp.u.ki.wVk = (ushort)a.Vk;
                    inp.u.ki.dwFlags = a.Up ? Native.KEYEVENTF_KEYUP : 0;
                    if (a.Vk == Native.VK_RMENU) inp.u.ki.dwFlags |= Native.KEYEVENTF_EXTENDEDKEY;
                }
                else
                {
                    inp.u.ki.wScan = a.Ch;
                    inp.u.ki.dwFlags = Native.KEYEVENTF_UNICODE | (a.Up ? Native.KEYEVENTF_KEYUP : 0);
                }
                arr[i] = inp;
            }
            Native.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(Native.INPUT)));
        }
    }
}
