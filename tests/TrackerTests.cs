using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TypeAssist.Tests
{
    /// <summary>Simulates a text box and a keyboard to check what actually ends up on screen.</summary>
    sealed class FakePlatform : IKeyPlatform
    {
        public readonly HashSet<int> Down = new HashSet<int>();
        public string NextChar;
        public string Process = "notepad";
        public StringBuilder Text = new StringBuilder();
        public int Injections;

        // Two fake layouts: 1 = English, 2 = Persian. Keys are identified by their QWERTY character.
        public static readonly IntPtr En = new IntPtr(0x04090409), Fa = new IntPtr(0x04290429);
        public IntPtr Reported = En;      // what "Windows" says
        public IntPtr Real = En;          // what the app really uses
        const string Qw = "qwertyuiop[]asdfghjkl;'zxcvbnm,";
        const string FaKeys = "ضصثقفغعهخحجچشسیبلاتنمکگظطزرذدپو";
        public static string Map(char qwerty, IntPtr hkl)
        {
            if (hkl == Fa) { int i = Qw.IndexOf(qwerty); if (i >= 0) return FaKeys[i].ToString(); }
            return qwerty.ToString();
        }
        public static uint VkFor(char q)
        {
            if (q >= 'a' && q <= 'z') return (uint)char.ToUpperInvariant(q);
            switch (q) { case ';': return 0xBA; case '\'': return 0xDE; case ',': return 0xBC; case '[': return 0xDB; case ']': return 0xDD; case '.': return 0xBE; }
            return q;
        }
        public static char QwertyFromVk(uint vk)
        {
            if (vk >= 'A' && vk <= 'Z') return char.ToLowerInvariant((char)vk);
            switch (vk) { case 0xBA: return ';'; case 0xDE: return '\''; case 0xBC: return ','; case 0xDB: return '['; case 0xDD: return ']'; case 0xBE: return '.'; }
            return (char)vk;
        }
        public static uint ScanFor(char q)
        {
            string r1 = "qwertyuiop[]", r2 = "asdfghjkl;'", r3 = "zxcvbnm,./";
            int i;
            if ((i = r1.IndexOf(q)) >= 0) return (uint)(0x10 + i);
            if ((i = r2.IndexOf(q)) >= 0) return (uint)(0x1E + i);
            if ((i = r3.IndexOf(q)) >= 0) return (uint)(0x2C + i);
            return 0;
        }
        public static char QwertyFor(char c)
        {
            int i = FaKeys.IndexOf(c);
            return i >= 0 ? Qw[i] : c;
        }
        public IntPtr ReportedLayout() { return Reported; }
        public IntPtr PersianLayout { get { return Fa; } }
        public IntPtr EnglishLayout { get { return En; } }
        public IntPtr[] Layouts = { En, Fa };
        public IntPtr[] InstalledLayouts() { return Layouts; }
        public bool IsDown(int vk) { return Down.Contains(vk); }
        public IntPtr Foreground() { return new IntPtr(Process.GetHashCode()); }
        public string ProcessName(IntPtr h) { return Process; }
        public bool IsFullScreen(IntPtr h) { return false; }
        public string Translate(uint vk, uint scan, bool shift, bool caps, IntPtr hkl)
        {
            if (vk == 0x41) return NextChar;                       // old-style test key: the char is given
            if (vk == 0x20) return " ";
            return Map(QwertyFromVk(vk), hkl);
        }

        public void Send(List<KeyAction> actions)
        {
            Injections++;
            foreach (var a in actions)
            {
                if (a.Up) continue;
                if (a.Vk == Native.VK_BACK) { if (Text.Length > 0) Text.Length--; }
                else if (a.Vk == Native.VK_RETURN) Text.Append('\n');
                else if (a.Vk == 0) Text.Append(a.Ch);
            }
        }
    }

    static class TrackerTests
    {
        static int fails;
        static FakePlatform pf;
        static InputTracker tr;
        static PopupState last;

        static void Key(int vk, string ch)
        {
            var k = new Native.KBDLLHOOKSTRUCT { vkCode = (uint)vk };
            pf.NextChar = ch;
            bool swallowed = tr.OnKeyDown(k);
            if (swallowed) return;
            if (vk == Native.VK_BACK) { if (pf.Text.Length > 0) pf.Text.Length--; }
            else if (vk == Native.VK_RETURN) pf.Text.Append('\n');
            else if (vk == Native.VK_TAB) pf.Text.Append('\t');
            else if (ch != null && !pf.Down.Contains(Native.VK_MENU) && !pf.Down.Contains(Native.VK_CONTROL)) pf.Text.Append(ch);
        }

        static void Type(string s)
        {
            foreach (char c in s)
            {
                if (c == '\n') Key(Native.VK_RETURN, "\r");
                else if (c == '\b') Key(Native.VK_BACK, "\b");
                else if (c == '\t') Key(Native.VK_TAB, "\t");
                else Key(0x41, c.ToString());   // vk does not matter: the fake layout returns the char
            }
        }

        /// <summary>Type text on a physical keyboard whose real layout is pf.Real.</summary>
        static void Keys(string text)
        {
            foreach (char ch in text)
            {
                if (ch == ' ') { var k0 = new Native.KBDLLHOOKSTRUCT { vkCode = 0x20 }; if (!tr.OnKeyDown(k0)) pf.Text.Append(' '); continue; }
                if (ch == '\b') { Key(Native.VK_BACK, "\b"); continue; }
                char q = FakePlatform.QwertyFor(ch);
                var k = new Native.KBDLLHOOKSTRUCT { vkCode = FakePlatform.VkFor(q), scanCode = FakePlatform.ScanFor(q) };
                bool swallowed = tr.OnKeyDown(k);
                if (!swallowed) pf.Text.Append(FakePlatform.Map(q, pf.Real));
            }
        }

        static void AltDigit(int n)
        {
            pf.Down.Add(Native.VK_MENU);
            Key(0x30 + n, n.ToString());
            pf.Down.Remove(Native.VK_MENU);
        }

        static string pickText;
        static string Pick() { return pickText; }

        static void Click() { tr.SimulateClick(); }

        static void Fresh(string process)
        {
            pf.Process = process;
            pf.Text.Length = 0;
            Click();
            // Typing starts in an empty field
            tr.EmptyFieldGeneration = tr.FocusGeneration;
        }

        static void Check(string label, string expected)
        {
            string actual = pf.Text.ToString();
            bool ok = actual == expected;
            if (!ok) fails++;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + label + "  [" + actual.Replace("\n", "⏎") + "]" + (ok ? "" : "  expected [" + expected.Replace("\n", "⏎") + "]"));
        }

        public static int Run(Engine engine)
        {
            var settings = new Settings();
            foreach (string a in Settings.DefaultExcluded) settings.ExcludedApps.Add(a);
            settings.PopupDelayMs = 0;
            pf = new FakePlatform();
            tr = null;
            tr = new InputTracker(pf, engine, settings,
                st => { last = st; if (st.Items.Count > 0) pickText = st.Items[0].Text; tr.VisibleGeneration = st.Items.Count > 0 ? st.Generation : -1; },
                s => { },
                () => { tr.CheckedGeneration = tr.FocusGeneration; tr.InPasswordField = false; });

            Fresh("notepad"); Type("I think teh weather is nice ");
            Check("typo fixed mid sentence", "I think the weather is nice ");

            Fresh("notepad"); Type("recieve the ");
            Check("first word in empty field", "receive the ");

            Fresh("notepad"); Type("hello teh"); Type(" "); Type("\b");
            Check("backspace undoes the fix", "hello teh ");
            Type("teh ");
            Check("undone word is never fixed again", "hello teh teh ");

            Fresh("notepad"); Type("سلام وجدود دارد ");
            Check("persian typo", "سلام وجود دارد ");

            Fresh("notepad"); Type("من میخواهم بروم ");
            Check("persian half-space", "من می‌خواهم بروم ");

            Fresh("notepad"); Type("it is dif"); Type("\t");
            Check("tab accepts completion", "it is different ");
            Type(".");
            Check("smart punctuation after accept", "it is different. ");
            Type("Thank ");
            AltDigit(1);
            Check("alt+1 accepts next word", "it is different. Thank you ");
            Type(" ");
            Check("no double space after accept", "it is different. Thank you ");

            Fresh("notepad"); Type("pls dont do it\n");
            Check("enter triggers fix", "pls don't do it\n");

            Fresh("notepad"); Type("visit www.gogle.com today ");
            Check("url part left alone", "visit www.gogle.com today ");

            Fresh("notepad"); Type("say salam to Kovich ");
            Check("finglish and names left alone", "say salam to Kovich ");

            Fresh("code"); Type("int teh = 1; ");
            Check("excluded app untouched", "int teh = 1; ");

            pf.Process = "notepad"; pf.Text.Length = 0;
            Click();
            pf.Text.Append("already here ");   // existing text we never saw
            Type("wierd ");
            Check("first word after click: clear fix of a longer word", "already here weird ");
            Type("wierd ");
            Check("…and the next word too", "already here weird weird ");
            Click(); Type("ing ");
            Check("short fragment after click untouched", "already here weird weird ing ");

            Fresh("notepad");
            tr.InPasswordField = true;
            Type("wierd wierd ");
            Check("password field untouched", "wierd wierd ");
            tr.InPasswordField = false;

            Fresh("notepad"); Type("abc"); Type("\b\b\b"); Type("wierd ");
            Check("backspacing to empty keeps context", "weird ");

            Fresh("notepad"); Type("wierd. wierd! ");
            Check("punctuation triggers", "weird. weird! ");

            Fresh("notepad"); Type("i am here ");
            Check("lonely i capitalised", "I am here ");

            Fresh("notepad"); Type("Becuase it ");
            Check("capital at sentence start fixed", "Because it ");
            Type("and Becuase ");
            Check("capital mid-sentence left alone (name?)", "Because it and Becuase ");

            Fresh("notepad"); Type("see the setp ");
            bool offered = last != null && last.Items.Count > 0 && last.Items[0].Kind == SuggestionKind.Correction;
            string pick = Pick();
            Type("\t");
            Check("unsure word: alternatives offered (" + offered + ") and Tab replaces it", "see the " + pick + " ");

            // Windows reports English but the app really types Persian (seen with Telegram)
            Fresh("telegram"); pf.Reported = FakePlatform.En; pf.Real = FakePlatform.Fa;
            Keys("سلام وجدود دارد ");
            Check("wrong layout reported: persian still fixed", "سلام وجود دارد ");
            Keys("کتابها ");
            Check("…and keeps working", "سلام وجود دارد کتاب‌ها ");
            // User switches back to English; Windows still says English, our override says Persian
            pf.Real = FakePlatform.En;
            Keys("hello wierd ");
            Check("switching back to english is noticed", "سلام وجود دارد کتاب‌ها hello weird ");
            // Genuine English with punctuation must never be read as Persian
            Fresh("notepad"); pf.Reported = FakePlatform.En; pf.Real = FakePlatform.En;
            Keys("hello,world is, it wierd ");
            Check("english punctuation untouched", "hello,world is, it weird ");
            // Correct Persian layout reported: normal behaviour
            Fresh("notepad"); pf.Reported = FakePlatform.Fa; pf.Real = FakePlatform.Fa;
            Keys("سلام وجدود ");
            Check("persian layout reported correctly", "سلام وجود ");
            // Persian layout not even listed by Windows: the built-in table must take over
            Fresh("claude"); pf.Reported = FakePlatform.En; pf.Real = FakePlatform.Fa; pf.Layouts = new[] { FakePlatform.En };
            Keys("سلام وجدود دارد ");
            Check("no persian layout listed: built-in table", "سلام وجود دارد ");
            pf.Real = FakePlatform.En;
            Keys("helo there ");
            Check("…and back to english", "سلام وجود دارد hello there ");
            pf.Layouts = new[] { FakePlatform.En, FakePlatform.Fa };
            pf.Reported = FakePlatform.En; pf.Real = FakePlatform.En;

            Console.WriteLine("injections: " + pf.Injections + (fails == 0 ? "   TRACKER OK" : "   " + fails + " TRACKER FAILED"));
            return fails;
        }
    }
}
