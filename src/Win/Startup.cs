using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TypeAssist
{
    /// <summary>
    /// "Start with Windows". The exe is copied to a fixed place (%LocalAppData%\Programs\TypeAssist) and that copy is
    /// registered, so it keeps working when the original was run from Downloads, a zip, or a folder that later moves.
    /// </summary>
    internal static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string Name = "TypeAssist";
        public const string Arg = "--autostart";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFile(string path);

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TypeAssist"); }
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, "TypeAssist.exe"); } }

        static string Command { get { return "\"" + InstalledExe + "\" " + Arg; } }

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(Name) != null;
            }
            catch { return false; }
        }

        /// <summary>True when Windows' own "Startup apps" switch (Task Manager / Settings) has turned it off.</summary>
        public static bool DisabledByWindows()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                {
                    var v = k == null ? null : k.GetValue(Name) as byte[];
                    return v != null && v.Length > 0 && (v[0] & 1) == 1;   // 02 = enabled, 03 = disabled
                }
            }
            catch { return false; }
        }

        /// <summary>Remove the "downloaded from the internet" mark, which can block silent start at sign-in.</summary>
        public static void Unblock(string path)
        {
            try { DeleteFile(path + ":Zone.Identifier"); } catch { }
        }

        public static bool Enable(out string error)
        {
            error = null;
            try
            {
                CopySelf();
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    k.SetValue(Name, Command);
                // Clear an earlier "disabled" choice in Task Manager so the new entry is really active.
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                    if (k != null && k.GetValue(Name) != null) k.DeleteValue(Name);
                Log.W("startup enabled: " + Command);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.W("startup enable failed: " + ex);
                return false;
            }
        }

        public static void Disable()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (k != null && k.GetValue(Name) != null) k.DeleteValue(Name);
                Log.W("startup disabled");
            }
            catch (Exception ex) { Log.W("startup disable failed: " + ex); }
        }

        /// <summary>
        /// Called on every launch: keeps the installed copy up to date with the version the user runs,
        /// and repairs the registry entry if it points somewhere else.
        /// </summary>
        public static void Maintain()
        {
            Unblock(Application.ExecutablePath);
            if (!IsEnabled()) return;
            try
            {
                CopySelf();
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    string cur = k.GetValue(Name) as string;
                    if (cur != Command) { k.SetValue(Name, Command); Log.W("startup entry repaired (was: " + cur + ")"); }
                }
            }
            catch (Exception ex) { Log.W("startup maintain failed: " + ex.Message); }
        }

        static void CopySelf()
        {
            string me = Path.GetFullPath(Application.ExecutablePath);
            string target = Path.GetFullPath(InstalledExe);
            if (string.Equals(me, target, StringComparison.OrdinalIgnoreCase)) { Unblock(target); return; }
            Directory.CreateDirectory(InstallDir);
            bool same = File.Exists(target) && new FileInfo(target).Length == new FileInfo(me).Length
                        && SameBytes(me, target);
            if (!same)
            {
                File.Copy(me, target, true);
                Log.W("installed copy updated: " + target);
            }
            Unblock(target);
        }

        static bool SameBytes(string a, string b)
        {
            byte[] x = File.ReadAllBytes(a), y = File.ReadAllBytes(b);
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }
    }
}
