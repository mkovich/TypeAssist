using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TypeAssist
{
    /// <summary>The small suggestion bar shown next to the caret. Never takes focus.</summary>
    internal sealed class PopupForm : Form
    {
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int cmd);

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_NOOWNERZORDER = 0x0200;
        const int SW_HIDE = 0, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;

        readonly InputTracker tracker;
        readonly Settings settings;
        readonly Timer delay = new Timer();
        PopupState pending, current;
        readonly List<Rectangle> itemRects = new List<Rectangle>();
        Font font, smallFont;
        bool dark;
        bool shown;

        public PopupForm(InputTracker tracker, Settings settings)
        {
            this.tracker = tracker;
            this.settings = settings;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            font = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
            smallFont = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
            Size = new Size(10, 10);
            delay.Tick += delegate { delay.Stop(); ShowPending(); };
            dark = IsDarkTheme();
            Opacity = 0.97;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */ | 0x00000008 /* TOPMOST */;
                cp.ClassStyle |= 0x00020000; /* CS_DROPSHADOW */
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = new IntPtr(MA_NOACTIVATE); return; }
            base.WndProc(ref m);
        }

        static bool IsDarkTheme()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        /// <summary>Called on the UI thread with each new state from the hook thread.</summary>
        public void Update(PopupState st)
        {
            if (st.Hide || !settings.Enabled)
            {
                pending = null;
                delay.Stop();
                HidePopup();
                return;
            }
            pending = st;
            if (shown || settings.PopupDelayMs == 0) { delay.Stop(); ShowPending(); }
            else { delay.Interval = Math.Max(1, settings.PopupDelayMs); delay.Stop(); delay.Start(); }
        }

        public void HidePopup()
        {
            if (!shown) return;
            shown = false;
            current = null;
            tracker.VisibleGeneration = -1;
            lock (tracker.RectLock) tracker.PopupRect = new Native.RECT();
            ShowWindow(Handle, SW_HIDE);
        }

        void ShowPending()
        {
            var st = pending;
            if (st == null) return;
            var fi = FocusInspector.Inspect(false);
            Native.RECT caret = fi.Caret;
            if (!fi.HasCaret)
            {
                Native.POINT p;
                Native.GetCursorPos(out p);
                caret = new Native.RECT { Left = p.X, Top = p.Y, Right = p.X + 1, Bottom = p.Y + 18 };
            }
            current = st;
            Size sz = LayoutItems(st);

            // Work area of the monitor that holds the caret
            var mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.GetMonitorInfo(Native.MonitorFromPoint(new Native.POINT { X = caret.Left, Y = caret.Top }, 2), ref mi);
            Native.RECT wa = mi.rcWork;

            int gap = 6;
            int x = st.Rtl ? caret.Right - sz.Width : caret.Left;
            int y = caret.Bottom + gap;
            if (y + sz.Height > wa.Bottom) y = caret.Top - gap - sz.Height;   // no room below: go above
            x = Math.Max(wa.Left + 2, Math.Min(x, wa.Right - sz.Width - 2));
            y = Math.Max(wa.Top + 2, y);

            SetWindowPos(Handle, HWND_TOPMOST, x, y, sz.Width, sz.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
            lock (tracker.RectLock) tracker.PopupRect = new Native.RECT { Left = x, Top = y, Right = x + sz.Width, Bottom = y + sz.Height };
            shown = true;
            tracker.VisibleGeneration = st.Items.Count > 0 ? st.Generation : -1;
            Invalidate();
        }

        // ------------------------------------------------------------------ drawing

        const int Pad = 6, ItemPadX = 10, ItemGap = 4;

        Size LayoutItems(PopupState st)
        {
            itemRects.Clear();
            int h;
            using (var g = CreateGraphics())
            {
                h = TextRenderer.MeasureText(g, "Ag گ", font).Height + 10;
                int x = Pad;
                foreach (var s in st.Items)
                {
                    int w = TextRenderer.MeasureText(g, s.Text, font, Size.Empty, TextFormatFlags.NoPadding).Width
                          + TextRenderer.MeasureText(g, "1", smallFont, Size.Empty, TextFormatFlags.NoPadding).Width + ItemPadX * 2 + 4;
                    itemRects.Add(new Rectangle(x, Pad, w, h));
                    x += w + ItemGap;
                }
                if (st.UndoOriginal != null)
                {
                    string undo = UndoText(st);
                    int w = TextRenderer.MeasureText(g, undo, smallFont, Size.Empty, TextFormatFlags.NoPadding).Width + ItemPadX * 2;
                    undoRect = new Rectangle(x, Pad, w, h);
                    x += w + ItemGap;
                }
                else undoRect = Rectangle.Empty;
                if (st.Items.Count > 0)
                {
                    int w = TextRenderer.MeasureText(g, HintText(), smallFont, Size.Empty, TextFormatFlags.NoPadding).Width + 8;
                    hintRect = new Rectangle(x, Pad, w, h);
                    x += w;
                }
                else hintRect = Rectangle.Empty;
                int total = x + Pad - ItemGap;
                // Persian: mirror so the first suggestion sits next to the caret on the right
                if (st.Rtl) Mirror(total);
                return new Size(Math.Max(total, 40), h + Pad * 2);
            }
        }

        Rectangle undoRect, hintRect;

        void Mirror(int total)
        {
            for (int i = 0; i < itemRects.Count; i++) itemRects[i] = Flip(itemRects[i], total);
            undoRect = Flip(undoRect, total);
            hintRect = Flip(hintRect, total);
        }

        static Rectangle Flip(Rectangle r, int total)
        {
            if (r.IsEmpty) return r;
            return new Rectangle(total - r.Right, r.Top, r.Width, r.Height);
        }

        string HintText() { return settings.AcceptWithTab ? "Tab  ·  Alt+1-3" : "Alt+1-3"; }

        static string UndoText(PopupState st) { return "⌫  " + st.UndoOriginal; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var st = current;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = dark ? Color.FromArgb(43, 43, 48) : Color.FromArgb(252, 252, 253);
            Color border = dark ? Color.FromArgb(70, 70, 78) : Color.FromArgb(210, 212, 218);
            Color fg = dark ? Color.FromArgb(236, 236, 240) : Color.FromArgb(30, 32, 38);
            Color dim = dark ? Color.FromArgb(150, 150, 160) : Color.FromArgb(120, 124, 134);
            Color accent = dark ? Color.FromArgb(56, 92, 150) : Color.FromArgb(225, 236, 252);
            Color accentText = dark ? Color.White : Color.FromArgb(20, 60, 140);
            g.Clear(bg);
            using (var pen = new Pen(border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            if (st == null) return;

            for (int i = 0; i < st.Items.Count && i < itemRects.Count; i++)
            {
                var r = itemRects[i];
                if (i == 0)
                    using (var path = Rounded(r, 6))
                    using (var b = new SolidBrush(accent))
                        g.FillPath(b, path);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                string num = (i + 1).ToString();
                Size ns = TextRenderer.MeasureText(g, num, smallFont, Size.Empty, TextFormatFlags.NoPadding);
                Rectangle numRect, textRect;
                if (st.Rtl)
                {
                    numRect = new Rectangle(r.Right - ItemPadX - ns.Width, r.Top, ns.Width, r.Height);
                    textRect = new Rectangle(r.Left + ItemPadX, r.Top, r.Width - ItemPadX * 2 - ns.Width - 4, r.Height);
                    flags |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;
                }
                else
                {
                    numRect = new Rectangle(r.Left + ItemPadX, r.Top, ns.Width, r.Height);
                    textRect = new Rectangle(numRect.Right + 4, r.Top, r.Width - ItemPadX * 2 - ns.Width - 4, r.Height);
                    flags |= TextFormatFlags.Left;
                }
                TextRenderer.DrawText(g, num, smallFont, numRect, dim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                Color c = i == 0 ? accentText : fg;
                if (st.Items[i].Kind == SuggestionKind.Correction) c = i == 0 ? accentText : dim;
                TextRenderer.DrawText(g, st.Items[i].Text, font, textRect, c, flags);
            }
            if (!undoRect.IsEmpty)
            {
                var f = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding;
                if (st.Rtl) f |= TextFormatFlags.RightToLeft;
                TextRenderer.DrawText(g, UndoText(st), smallFont, undoRect, dim, f);
            }
            if (!hintRect.IsEmpty)
                TextRenderer.DrawText(g, HintText(), smallFont, hintRect, dim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var st = current;
            if (st == null) return;
            for (int i = 0; i < itemRects.Count; i++)
                if (itemRects[i].Contains(e.Location)) { tracker.RequestAccept(i, st.Generation); return; }
        }
    }
}
