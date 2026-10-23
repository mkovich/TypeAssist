using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;

namespace TypeAssist.Tests
{
    static class Program
    {
        static int fails = 0;

        static void Expect(string label, string actual, string expected)
        {
            bool ok = actual == expected;
            if (!ok) fails++;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + label + " -> " + (actual ?? "(none)") + (ok ? "" : "   expected " + (expected ?? "(none)")));
        }

        static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            string dataDir = args[0];
            var sw = Stopwatch.StartNew();
            var e = new Engine();
            e.LoadData(name => File.OpenRead(Path.Combine(dataDir, name)));
            Console.WriteLine("load ms: " + sw.ElapsedMilliseconds + "  memory MB: " + (GC.GetTotalMemory(true) / 1048576));

            Func<string, string> fixS = w => { var c = e.TryCorrect(w, null, true); return c == null ? null : c.Replacement; };
            Func<string, string, string> fix = (w, prev) => { var c = e.TryCorrect(w, prev); return c == null ? null : c.Replacement; };

            // Clear typos are fixed
            Expect("وجدود", fix("وجدود", null), "وجود");
            Expect("teh", fix("teh", null), "the");
            Expect("recieve", fix("recieve", null), "receive");
            Expect("Becuase (sentence start)", fixS("Becuase"), "Because");
            Expect("Kitti (name mid-sentence)", fix("Kitti", null), null);
            Expect("monochromes (plural)", fix("monochromes", null), null);
            Expect("کتابش (affix)", fix("کتابش", null), null);
            Expect("می‌ترکند (verb form)", fix("می‌ترکند", null), null);
            Expect("definately", fix("definately", null), "definitely");
            Expect("accomodate", fix("accomodate", null), "accommodate");
            Expect("سلاک", fix("سلاک", null), "سلام");
            Expect("کتابخانع", fix("کتابخانع", null), "کتابخانه");
            Expect("اسنت", fix("اسنت", null), "است");
            Expect("ضرف (ambiguous ظرف/صرف)", fix("ضرف", null), null);
            Expect("دانشکاه", fix("دانشکاه", null), "دانشگاه");
            Expect("i", fix("i", null), "I");
            Expect("dont", fix("dont", null), "don't");
            Expect("im", fix("im", null), "I'm");
            Expect("Wierd (sentence start)", fixS("Wierd"), "Weird");
            Expect("lite (valid)", fix("lite", null), null);
            Expect("helo", fix("helo", null), "hello");
            Expect("realy", fix("realy", null), "really");
            Expect("thier", fix("thier", null), "their");
            Expect("alot (ambiguous)", fix("alot", null), null);
            Expect("seperate", fix("seperate", null), "separate");
            Expect("میخواهم zwnj", fix("میخواهم", null), "می‌خواهم");
            Expect("کتابها zwnj", fix("کتابها", null), "کتاب‌ها");

            // Things that must be left alone
            Expect("hello (valid)", fix("hello", null), null);
            Expect("salam (finglish)", fix("salam", null), null);
            Expect("chetori (finglish)", fix("chetori", null), null);
            Expect("iPhone", fix("iPhone", null), null);
            Expect("NASA", fix("NASA", null), null);
            Expect("abc123", fix("abc123", null), null);
            Expect("تنها (valid)", fix("تنها", null), null);
            Expect("بهتر (valid)", fix("بهتر", null), null);
            Expect("میز (valid)", fix("میز", null), null);
            Expect("Kovich (name)", fix("Kovich", null), null);
            Expect("Hormuz (name)", fix("Hormuz", null), null);
            Expect("ok", fix("ok", null), null);

            // Rejected corrections stick
            e.Reject("teh");
            Expect("teh after reject", fix("teh", null), null);

            // Learning makes a word known
            for (int i = 0; i < 3; i++) e.Learn(null, "Figence");
            Expect("Figence learned", fix("Figence", null), null);

            Console.WriteLine();
            foreach (var t in new[] { new[] { "", "thank" }, new[] { "", "I" }, new[] { "", "وجود" }, new[] { "", "به" },
                                      new[] { "yo", "thank" }, new[] { "dif", null }, new[] { "می‌خو", null }, new[] { "دا", "وجود" },
                                      new[] { "انت", null }, new[] { "", "سلام" } })
            {
                var s = e.Suggest(t[0], t[1], 3);
                Console.WriteLine("suggest [" + t[1] + "] + '" + t[0] + "' => " + string.Join(", ", s));
            }

            // User habits win over the corpus
            for (int i = 0; i < 4; i++) e.Learn("Hormuz", "Group");
            Console.WriteLine("after learning: " + string.Join(", ", e.Suggest("", "Hormuz", 3)));

            // Speed
            sw.Restart();
            int n = 0;
            foreach (string w in new[] { "wierd", "seperate", "occured", "untill", "beleive", "تشکرر", "دانشکاه", "خانواذه" })
                for (int i = 0; i < 200; i++) { e.TryCorrect(w, null); n++; }
            Console.WriteLine("correct avg us: " + (sw.Elapsed.TotalMilliseconds * 1000 / n).ToString("0"));
            sw.Restart();
            for (int i = 0; i < 500; i++) e.Suggest("co", "the", 3);
            Console.WriteLine("suggest avg us: " + (sw.Elapsed.TotalMilliseconds * 1000 / 500).ToString("0"));
            foreach (string w in new[] { "wierd", "seperate", "occured", "untill", "beleive", "تشکرر", "دانشکاه", "خانواذه" })
                Console.WriteLine("  " + w + " -> " + (fix(w, null) ?? "(none)") + "   alts: " + string.Join(", ", e.CorrectionCandidates(w, 3)));

            string tmp = Path.Combine(Path.GetTempPath(), "ta_user_test");
            e.SaveUser(tmp);
            var e2 = new Engine();
            e2.LoadData(name => File.OpenRead(Path.Combine(dataDir, name)));
            e2.LoadUser(tmp);
            Expect("persisted reject", e2.TryCorrect("teh", null) == null ? null : "corrected", null);

            var e3 = new Engine();
            e3.LoadData(name => File.OpenRead(Path.Combine(dataDir, name)));
            fails += TrackerTests.Run(e3);
            Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
            return fails == 0 ? 0 : 1;
        }
    }
}
