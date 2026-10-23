using System;
using System.Runtime.InteropServices;
using Accessibility;

namespace TypeAssist
{
    /// <summary>Looks at the focused control of the foreground app: where the caret is, and whether it is a password box.</summary>
    internal static class FocusInspector
    {
        public struct FocusInfo
        {
            public bool Password;
            public bool Empty;
            public bool HasCaret;
            public Native.RECT Caret;     // screen coordinates, physical pixels
        }

        static IntPtr FocusWindow(out uint threadId)
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint pid;
            threadId = Native.GetWindowThreadProcessId(fg, out pid);
            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            if (Native.GetGUIThreadInfo(threadId, ref gti) && gti.hwndFocus != IntPtr.Zero) return gti.hwndFocus;
            return fg;
        }

        public static FocusInfo Inspect(bool wantFieldInfo)
        {
            var info = new FocusInfo();
            uint tid;
            IntPtr hwnd = FocusWindow(out tid);
            if (hwnd == IntPtr.Zero) return info;

            // 1) Classic Win32 caret (Notepad, WordPad, most native apps)
            var gti = new Native.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            if (Native.GetGUIThreadInfo(tid, ref gti) && gti.hwndCaret != IntPtr.Zero
                && (gti.rcCaret.Bottom - gti.rcCaret.Top) > 0)
            {
                var tl = new Native.POINT { X = gti.rcCaret.Left, Y = gti.rcCaret.Top };
                var br = new Native.POINT { X = gti.rcCaret.Right, Y = gti.rcCaret.Bottom };
                Native.ClientToScreen(gti.hwndCaret, ref tl);
                Native.ClientToScreen(gti.hwndCaret, ref br);
                info.Caret = new Native.RECT { Left = tl.X, Top = tl.Y, Right = Math.Max(br.X, tl.X + 1), Bottom = br.Y };
                info.HasCaret = true;
            }

            // 2) Accessibility caret (Chrome, Edge, Firefox, Electron apps, Office)
            if (!info.HasCaret)
            {
                try
                {
                    object o;
                    Guid iid = Native.IID_IAccessible;
                    if (Native.AccessibleObjectFromWindow(hwnd, Native.OBJID_CARET, ref iid, out o) == 0 && o is IAccessible)
                    {
                        var acc = (IAccessible)o;
                        int l, t, w, h;
                        acc.accLocation(out l, out t, out w, out h, 0);
                        if (h > 0 && !(l == 0 && t == 0))
                        {
                            info.Caret = new Native.RECT { Left = l, Top = t, Right = l + Math.Max(w, 1), Bottom = t + h };
                            info.HasCaret = true;
                        }
                        Marshal.ReleaseComObject(o);
                    }
                }
                catch { }
            }

            if (!wantFieldInfo) return info;

            // Password field?  Win32 edit with ES_PASSWORD, or an accessible object marked "protected".
            string cls = Native.ClassName(hwnd);
            if (cls.IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0
                && (Native.GetWindowLong(hwnd, Native.GWL_STYLE) & Native.ES_PASSWORD) != 0)
            {
                info.Password = true;
                return info;
            }
            try
            {
                object o;
                Guid iid = Native.IID_IAccessible;
                if (Native.AccessibleObjectFromWindow(hwnd, Native.OBJID_CLIENT, ref iid, out o) == 0 && o is IAccessible)
                {
                    var root = (IAccessible)o;
                    object focus = null;
                    try { focus = root.accFocus; } catch { }
                    IAccessible target = root;
                    object child = 0;
                    if (focus is IAccessible) target = (IAccessible)focus;
                    else if (focus is int) child = focus;
                    object stateObj = null;
                    try { stateObj = target.get_accState(child); } catch { }
                    int state = stateObj is int ? (int)stateObj : 0;
                    info.Password = (state & Native.STATE_SYSTEM_PROTECTED) != 0;
                    try
                    {
                        string value = target.get_accValue(child);
                        info.Empty = value != null && value.Length == 0;
                    }
                    catch { }
                    if (focus is IAccessible && !ReferenceEquals(focus, o)) Marshal.ReleaseComObject(focus);
                    Marshal.ReleaseComObject(o);
                }
            }
            catch { }
            return info;
        }
    }
}
