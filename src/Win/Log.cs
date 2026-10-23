using System;
using System.IO;
using System.Text;

namespace TypeAssist
{
    /// <summary>Small diagnostic log in %AppData%\TypeAssist\log.txt. Never contains what you type.</summary>
    internal static class Log
    {
        static readonly object gate = new object();
        public static string FilePath { get { return Path.Combine(Settings.Dir, "log.txt"); } }

        public static void W(string msg)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Settings.Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n", new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
