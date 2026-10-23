using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace TypeAssist
{
    /// <summary>Shows, live, what TypeAssist sees while you type. Nothing here is saved.</summary>
    internal sealed class DiagnosticsForm : Form
    {
        readonly InputTracker tracker;
        readonly TextBox box = new TextBox();
        readonly Label state = new Label();
        readonly Timer timer = new Timer();

        public DiagnosticsForm(InputTracker tracker)
        {
            this.tracker = tracker;
            Text = "TypeAssist — Diagnostics";
            Width = 760; Height = 460;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            var help = new Label
            {
                Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 0),
                Text = "Keep this window open, click into Notepad and type a few words (Persian and English) ending with a space.\r\n"
                     + "This shows what TypeAssist sees. Nothing is saved. Take a screenshot if something looks wrong."
            };
            state.Dock = DockStyle.Top; state.Height = 46; state.AutoSize = false; state.Padding = new Padding(8, 6, 8, 0);
            state.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            box.Dock = DockStyle.Fill; box.Multiline = true; box.ReadOnly = true; box.ScrollBars = ScrollBars.Vertical;
            box.Font = new Font("Segoe UI", 10.5f);
            Controls.Add(box); Controls.Add(state); Controls.Add(help);
            timer.Interval = 300;
            timer.Tick += delegate { Refresh2(); };
            Shown += delegate { tracker.DiagnosticsOn = true; timer.Start(); };
            FormClosed += delegate { tracker.DiagnosticsOn = false; timer.Stop(); lock (tracker.DiagLines) tracker.DiagLines.Clear(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        void Refresh2()
        {
            state.Text = tracker.DiagState;
            var sb = new StringBuilder();
            lock (tracker.DiagLines)
                for (int i = tracker.DiagLines.Count - 1; i >= 0; i--) sb.Append(tracker.DiagLines[i]).Append("\r\n");
            string t = sb.ToString();
            if (box.Text != t) box.Text = t;
        }
    }
}
