using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TypeAssist
{
    /// <summary>Plain key=value settings in %AppData%\TypeAssist\settings.ini (editable by hand).</summary>
    internal sealed class Settings
    {
        public bool Enabled = true;
        public bool AutoCorrect = true;
        public bool Suggestions = true;
        public bool ZwnjFix = true;
        public bool Learn = true;
        public bool AcceptWithTab = true;
        public bool SmartSpace = true;          // add a space after an accepted suggestion
        public int PopupDelayMs = 60;
        public HashSet<string> ExcludedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Terminals, code editors, remote desktops and password managers: autocorrect does more harm than good there.
        public static readonly string[] DefaultExcluded = {
            "cmd", "powershell", "pwsh", "windowsterminal", "conhost", "openconsole", "wsl", "bash", "mintty", "putty", "kitty",
            "code", "code - insiders", "cursor", "windsurf", "devenv", "idea64", "pycharm64", "webstorm64", "rider64", "clion64",
            "goland64", "phpstorm64", "datagrip64", "rustrover64", "studio64", "sublime_text", "notepad++", "atom", "zed",
            "mstsc", "vmconnect", "vmware", "virtualboxvm", "anydesk", "teamviewer", "rustdesk",
            "keepass", "keepassxc", "1password", "bitwarden", "lastpass", "dashlane", "enpass",
            "typeassist" };

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TypeAssist"); }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            foreach (string a in DefaultExcluded) s.ExcludedApps.Add(a);
            try
            {
                if (!File.Exists(FilePath)) { s.Save(); return s; }
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "enabled": s.Enabled = Bool(v, s.Enabled); break;
                        case "autocorrect": s.AutoCorrect = Bool(v, s.AutoCorrect); break;
                        case "suggestions": s.Suggestions = Bool(v, s.Suggestions); break;
                        case "zwnj_fix": s.ZwnjFix = Bool(v, s.ZwnjFix); break;
                        case "learn": s.Learn = Bool(v, s.Learn); break;
                        case "accept_with_tab": s.AcceptWithTab = Bool(v, s.AcceptWithTab); break;
                        case "smart_space": s.SmartSpace = Bool(v, s.SmartSpace); break;
                        case "popup_delay_ms": int d; if (int.TryParse(v, out d)) s.PopupDelayMs = Math.Max(0, Math.Min(1000, d)); break;
                        case "excluded_apps":
                            s.ExcludedApps.Clear();
                            foreach (string a in v.Split(','))
                                if (a.Trim().Length > 0) s.ExcludedApps.Add(a.Trim());
                            s.ExcludedApps.Add("typeassist");
                            break;
                    }
                }
            }
            catch { }
            return s;
        }

        static bool Bool(string v, bool def)
        {
            v = v.ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return def;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var apps = new List<string>(ExcludedApps);
                apps.Sort(StringComparer.OrdinalIgnoreCase);
                var sb = new StringBuilder();
                sb.AppendLine("# TypeAssist settings. Edit, then choose \"Reload settings\" from the tray menu.");
                sb.AppendLine("enabled=" + Yes(Enabled));
                sb.AppendLine("autocorrect=" + Yes(AutoCorrect));
                sb.AppendLine("suggestions=" + Yes(Suggestions));
                sb.AppendLine("# Insert the half-space in words like میخواهم -> می‌خواهم and کتابها -> کتاب‌ها");
                sb.AppendLine("zwnj_fix=" + Yes(ZwnjFix));
                sb.AppendLine("# Learn your words and word pairs (stored only on this PC, never in password fields)");
                sb.AppendLine("learn=" + Yes(Learn));
                sb.AppendLine("# Tab accepts the highlighted suggestion while the suggestion bar is visible (Alt+1..3 always works)");
                sb.AppendLine("accept_with_tab=" + Yes(AcceptWithTab));
                sb.AppendLine("smart_space=" + Yes(SmartSpace));
                sb.AppendLine("popup_delay_ms=" + PopupDelayMs);
                sb.AppendLine("# Process names (without .exe) where TypeAssist stays silent");
                sb.AppendLine("excluded_apps=" + string.Join(",", apps.ToArray()));
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        static string Yes(bool b) { return b ? "yes" : "no"; }
    }
}
