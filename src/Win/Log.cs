using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace TypeAssist
{
    /// <summary>
    /// Small diagnostic log in %AppData%\TypeAssist\log.txt. Never contains what you type.
    /// Writing happens on a background thread so the keyboard hook never waits for the disk.
    /// </summary>
    internal static class Log
    {
        static readonly object gate = new object();
        static readonly Queue<string> pending = new Queue<string>();
        static Thread writer;
        public static string FilePath { get { return Path.Combine(Settings.Dir, "log.txt"); } }

        public static void W(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n";
            lock (gate)
            {
                if (pending.Count > 2000) return;
                pending.Enqueue(line);
                if (writer == null)
                {
                    writer = new Thread(Run) { IsBackground = true, Name = "TypeAssist log" };
                    writer.Start();
                }
                Monitor.Pulse(gate);
            }
        }

        static void Run()
        {
            while (true)
            {
                var sb = new StringBuilder();
                lock (gate)
                {
                    while (pending.Count == 0) Monitor.Wait(gate);
                    while (pending.Count > 0) sb.Append(pending.Dequeue());
                }
                try
                {
                    Directory.CreateDirectory(Settings.Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        /// <summary>Write what is queued before the app exits.</summary>
        public static void Flush()
        {
            for (int i = 0; i < 20; i++)
            {
                lock (gate) if (pending.Count == 0) return;
                Thread.Sleep(25);
            }
        }
    }
}
