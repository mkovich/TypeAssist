using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("TypeAssist")]
[assembly: AssemblyDescription("Autocorrect and word suggestions for Persian and English, in every Windows app")]
[assembly: AssemblyProduct("TypeAssist")]
[assembly: AssemblyCompany("mkovich.com")]
[assembly: AssemblyCopyright("© 2026 MH.Kovich · mkovich.com")]
[assembly: AssemblyVersion("1.6.2.0")]
[assembly: AssemblyFileVersion("1.6.2.0")]

namespace TypeAssist
{
    internal sealed class App : ApplicationContext
    {
        public const string Website = "https://mkovich.com";
        static bool autostart;

        readonly Engine engine = new Engine();
        Settings settings;
        InputTracker tracker;
        PopupForm popup;
        NotifyIcon tray;
        ToolStripMenuItem miEnabled, miAutocorrect, miSuggest, miLearn, miStartup, miPauseApp, miLayout;
        readonly System.Windows.Forms.Timer focusTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();
        Icon iconOn, iconOff;
        int lastCheckedGen = -2;

        [STAThread]
        static void Main(string[] args)
        {
            autostart = Array.IndexOf(args, Startup.Arg) >= 0;
            bool created;
            using (var mutex = new Mutex(true, "TypeAssist.SingleInstance", out created))
            {
                if (!created)
                {
                    if (autostart) return;
                    MessageBox.Show("TypeAssist is already running (see the icon next to the clock).", "TypeAssist", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Log.W("---- start " + Application.ProductVersion + (autostart ? " (at sign-in)" : "") + "  exe " + Application.ExecutablePath + "  OS " + Environment.OSVersion + "  CLR " + Environment.Version + "  64bit=" + Environment.Is64BitProcess);
                Application.ThreadException += (s, e) => Fatal(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Fatal(e.ExceptionObject as Exception);
                Native.MakeDpiAware();
                Startup.Maintain();
                // At sign-in, give the taskbar a moment to appear so the tray icon shows up reliably.
                if (autostart) Thread.Sleep(4000);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                App app;
                try { app = new App(); }
                catch (Exception ex) { Fatal(ex); return; }
                Application.Run(app);
                GC.KeepAlive(mutex);
            }
        }

        App()
        {
            settings = Settings.Load();
            engine.Options.ZwnjFix = settings.ZwnjFix;
            engine.Options.Learn = settings.Learn;

            iconOn = MakeIcon(true);
            iconOff = MakeIcon(false);

            tracker = new InputTracker(new WinPlatform(), engine, settings, OnState, OnHotkey, OnFocusChanged);
            tracker.Paused = true;   // until the dictionaries are loaded
            popup = new PopupForm(tracker, settings);
            var h = popup.Handle;   // create the window so BeginInvoke works from other threads

            BuildTray();
            tracker.Start();

            focusTimer.Interval = 150;
            focusTimer.Tick += delegate { CheckFocus(); };
            focusTimer.Start();
            saveTimer.Interval = 90 * 1000;
            saveTimer.Tick += delegate { SaveUser(); };
            saveTimer.Start();

            var loader = new Thread(LoadData);
            loader.IsBackground = true;
            loader.Start();
        }

        void LoadData()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                lock (engine)
                {
                    engine.LoadData(name =>
                    {
                        Stream s = asm.GetManifestResourceStream(name);
                        if (s == null)
                        {
                            string p = Path.Combine(Path.GetDirectoryName(asm.Location), "data", name);
                            s = File.OpenRead(p);
                        }
                        return s;
                    });
                    engine.LoadUser(Settings.Dir);
                }
                tracker.Paused = false;
                Log.W("dictionaries loaded: fa=" + engine.Fa.WordCount + " en=" + engine.En.WordCount);
                popup.BeginInvoke((MethodInvoker)delegate
                {
                    UpdateTray();
                    if (!autostart) tray.ShowBalloonTip(4000, "TypeAssist is ready",
                        "Type in any app. The icon is next to the clock (click ^ if you do not see it).", ToolTipIcon.Info);
                });
            }
            catch (Exception ex)
            {
                Log.W("load failed: " + ex);
                popup.BeginInvoke((MethodInvoker)delegate
                {
                    MessageBox.Show("Could not load the dictionaries:\n" + ex.Message, "TypeAssist", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            }
        }

        // ------------------------------------------------------------------ callbacks from the hook thread

        void OnState(PopupState st)
        {
            try { popup.BeginInvoke((MethodInvoker)delegate { popup.Update(st); }); } catch { }
        }

        void OnHotkey(string what)
        {
            try { popup.BeginInvoke((MethodInvoker)delegate { ToggleEnabled(true); }); } catch { }
        }

        void OnFocusChanged()
        {
            try { popup.BeginInvoke((MethodInvoker)delegate { CheckFocus(); }); } catch { }
        }

        void CheckFocus()
        {
            int gen = tracker.FocusGeneration;
            if (gen == lastCheckedGen && tracker.CheckedGeneration == gen) return;
            var fi = FocusInspector.Inspect(true);
            tracker.InPasswordField = fi.Password;
            if (fi.Empty) tracker.EmptyFieldGeneration = gen;
            tracker.CheckedGeneration = gen;
            Log.W("  focus: password=" + fi.Password + " empty=" + fi.Empty + " caret=" + fi.HasCaret);
            lastCheckedGen = gen;
            if (fi.Password) popup.HidePopup();
        }

        // ------------------------------------------------------------------ tray

        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            miEnabled = new ToolStripMenuItem("Enabled  (Ctrl+Alt+F12)", null, delegate { ToggleEnabled(false); });
            miAutocorrect = new ToolStripMenuItem("Autocorrect typos", null, delegate
            {
                settings.AutoCorrect = !settings.AutoCorrect; settings.Save(); UpdateTray();
            });
            miSuggest = new ToolStripMenuItem("Word suggestions", null, delegate
            {
                settings.Suggestions = !settings.Suggestions; settings.Save(); UpdateTray(); popup.HidePopup();
            });
            miLayout = new ToolStripMenuItem("Fix wrong keyboard language", null, delegate
            {
                settings.AutoSwitchLayout = !settings.AutoSwitchLayout; settings.Save(); UpdateTray();
            });
            miLearn = new ToolStripMenuItem("Learn from my typing", null, delegate
            {
                settings.Learn = !settings.Learn; engine.Options.Learn = settings.Learn; settings.Save(); UpdateTray();
            });
            miPauseApp = new ToolStripMenuItem("Pause in this app", null, delegate { TogglePauseApp(); });
            miStartup = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
            var miForget = new ToolStripMenuItem("Forget learned words…", null, delegate
            {
                var r = MessageBox.Show("Forget all words and word pairs learned from your typing?", "TypeAssist",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r == DialogResult.Yes) { lock (engine) engine.ForgetLearned(); SaveUser(); }
            });
            var miFolder = new ToolStripMenuItem("Open settings folder", null, delegate
            {
                Directory.CreateDirectory(Settings.Dir);
                Process.Start("explorer.exe", "\"" + Settings.Dir + "\"");
            });
            var miReload = new ToolStripMenuItem("Reload settings", null, delegate { ReloadSettings(); });
            var miLog = new ToolStripMenuItem("Show log", null, delegate
            {
                try { Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); } catch { }
            });
            var miDiag = new ToolStripMenuItem("Diagnostics…", null, delegate { ShowDiagnostics(); });
            var miAbout = new ToolStripMenuItem("About TypeAssist…", null, delegate { ShowAbout(); });
            var miSite = new ToolStripMenuItem("Developer: mkovich.com", null, delegate { OpenWebsite(); });
            var miExit = new ToolStripMenuItem("Exit", null, delegate { ExitThread(); });

            menu.Items.AddRange(new ToolStripItem[] {
                miEnabled, new ToolStripSeparator(), miAutocorrect, miSuggest, miLayout, miLearn, new ToolStripSeparator(),
                miPauseApp, miStartup, new ToolStripSeparator(), miForget, miFolder, miReload, miLog, miDiag, new ToolStripSeparator(), miAbout, miSite, new ToolStripSeparator(), miExit });
            menu.Opening += delegate { UpdateTray(); };

            tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleEnabled(true); };
            UpdateTray();
        }

        void UpdateTray()
        {
            bool loading = tracker != null && tracker.Paused;
            miEnabled.Checked = settings.Enabled;
            miAutocorrect.Checked = settings.AutoCorrect;
            miSuggest.Checked = settings.Suggestions;
            miLearn.Checked = settings.Learn;
            miLayout.Checked = settings.AutoSwitchLayout;
            miStartup.Checked = Startup.IsEnabled() && !Startup.DisabledByWindows();
            string app = tracker == null ? "" : tracker.LastApp;
            miPauseApp.Visible = app.Length > 0;
            miPauseApp.Text = "Pause in " + app;
            miPauseApp.Checked = app.Length > 0 && settings.ExcludedApps.Contains(app);
            tray.Icon = settings.Enabled && !loading ? iconOn : iconOff;
            tray.Text = loading ? "TypeAssist — loading…" : settings.Enabled ? "TypeAssist — on" : "TypeAssist — off";
        }

        void ToggleEnabled(bool notify)
        {
            settings.Enabled = !settings.Enabled;
            settings.Save();
            if (!settings.Enabled) popup.HidePopup();
            tracker.RequestReset();
            UpdateTray();
            if (notify)
                tray.ShowBalloonTip(1200, "TypeAssist", settings.Enabled ? "On" : "Off", ToolTipIcon.None);
        }

        void TogglePauseApp()
        {
            string app = tracker.LastApp;
            if (app.Length == 0) return;
            if (settings.ExcludedApps.Contains(app)) settings.ExcludedApps.Remove(app);
            else settings.ExcludedApps.Add(app);
            settings.Save();
            ReloadSettings();
        }

        void ReloadSettings()
        {
            var s = Settings.Load();
            settings.Enabled = s.Enabled;
            settings.AutoCorrect = s.AutoCorrect;
            settings.Suggestions = s.Suggestions;
            settings.ZwnjFix = s.ZwnjFix;
            settings.Learn = s.Learn;
            settings.AcceptWithTab = s.AcceptWithTab;
            settings.AutoSwitchLayout = s.AutoSwitchLayout;
            settings.SmartSpace = s.SmartSpace;
            settings.PopupDelayMs = s.PopupDelayMs;
            settings.ExcludedApps = s.ExcludedApps;
            lock (engine) { engine.Options.ZwnjFix = s.ZwnjFix; engine.Options.Learn = s.Learn; }
            tracker.RequestReset();
            popup.HidePopup();
            UpdateTray();
        }

        void ToggleStartup()
        {
            if (Startup.IsEnabled() && !Startup.DisabledByWindows())
            {
                Startup.Disable();
            }
            else
            {
                string err;
                if (Startup.Enable(out err))
                    tray.ShowBalloonTip(2500, "TypeAssist", "Will start automatically when you sign in to Windows.", ToolTipIcon.Info);
                else
                    MessageBox.Show("Could not turn on start with Windows:\n" + err, "TypeAssist", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            UpdateTray();
        }

        static void OpenWebsite()
        {
            try { Process.Start(Website); } catch { }
        }

        static void ShowAbout()
        {
            var r = MessageBox.Show(
                "TypeAssist " + Application.ProductVersion + "\n" +
                "Autocorrect and word suggestions for Persian and English, in every Windows app.\n\n" +
                "Developer: mkovich.com\n\n" +
                "Open the website?",
                "About TypeAssist", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
            if (r == DialogResult.Yes) OpenWebsite();
        }

        void SaveUser()
        {
            string[] snap = null;
            lock (engine) { if (engine.Dirty) snap = engine.SnapshotUser(); }
            if (snap == null) return;
            var t = new Thread(() => { try { Engine.WriteUser(Settings.Dir, snap); } catch { } });
            t.IsBackground = false;
            t.Start();
        }

        protected override void ExitThreadCore()
        {
            tracker.Stop();
            string[] snap = null;
            lock (engine) { if (engine.Dirty) snap = engine.SnapshotUser(); }
            if (snap != null) try { Engine.WriteUser(Settings.Dir, snap); } catch { }
            tray.Visible = false;
            tray.Dispose();
            popup.Close();
            base.ExitThreadCore();
        }

        DiagnosticsForm diag;

        void ShowDiagnostics()
        {
            if (diag == null || diag.IsDisposed) diag = new DiagnosticsForm(tracker);
            diag.Show();
            diag.Activate();
        }

        static void Fatal(Exception ex)
        {
            Log.W("FATAL: " + ex);
            try
            {
                MessageBox.Show("TypeAssist stopped because of an error:\n\n" + (ex == null ? "?" : ex.Message) + "\n\nDetails:\n" + Log.FilePath,
                    "TypeAssist", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        static Icon MakeIcon(bool on)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                Color bg = on ? Color.FromArgb(22, 128, 110) : Color.FromArgb(130, 134, 140);
                using (var path = new GraphicsPath())
                {
                    path.AddArc(1, 1, 12, 12, 180, 90);
                    path.AddArc(19, 1, 12, 12, 270, 90);
                    path.AddArc(19, 19, 12, 12, 0, 90);
                    path.AddArc(1, 19, 12, 12, 90, 90);
                    path.CloseFigure();
                    using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                }
                using (var f = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("Aب", f, Brushes.White, new RectangleF(0, 0, 32, 31), sf);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
