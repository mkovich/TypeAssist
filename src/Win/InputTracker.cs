using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TypeAssist
{
    /// <summary>What the suggestion bar should show. Immutable; handed from the hook thread to the UI.</summary>
    internal sealed class PopupState
    {
        public int Generation;
        public List<Suggestion> Items = new List<Suggestion>();
        public string UndoOriginal;     // set right after an autocorrection: "Backspace to undo"
        public string UndoReplacement;
        public bool Rtl;
        public bool Hide { get { return Items.Count == 0 && UndoOriginal == null; } }
    }

    /// <summary>
    /// Runs on its own thread: owns the low-level keyboard/mouse hooks, keeps track of the word being
    /// typed, applies corrections and accepts suggestions by injecting input. Everything here must be fast.
    /// </summary>
    internal sealed class InputTracker
    {
        static readonly IntPtr Magic = new IntPtr(0x54415353);   // marks our own injected input ("TASS")
        const uint MsgAccept = Native.WM_APP + 1;
        const uint MsgReset = Native.WM_APP + 2;

        readonly Engine engine;
        readonly Settings settings;
        readonly IKeyPlatform platform;
        readonly Action<PopupState> publish;
        readonly Action<string> onToggleHotkey;
        readonly Action onFocusChanged;

        Thread thread;
        uint threadId;
        IntPtr kbHook, mouseHook;
        Native.HookProc kbProc, mouseProc;   // keep delegates alive

        // ---- typing state (hook thread only)
        readonly StringBuilder word = new StringBuilder();
        bool wordTrusted;       // we saw the boundary before this word, so we know its full text
        bool atBoundary;        // the last thing typed was a delimiter we saw
        string prevWord, prevWord2;        // previous complete word in this run of typing (null = unknown)
        bool sentenceStart = true;
        bool justAccepted;      // smart space was added after an accepted suggestion
        Pending lastFix;        // most recent autocorrection, undoable with Backspace
        List<Suggestion> shown = new List<Suggestion>();
        int generation;

        IntPtr lastForeground = IntPtr.Zero;
        bool appExcluded;
        public volatile string LastApp = "";

        // ---- shared with the UI thread
        public volatile int FocusGeneration;      // bumped whenever focus may have moved
        public volatile int CheckedGeneration = -1;
        public volatile bool InPasswordField;
        public volatile int EmptyFieldGeneration = -1;   // UI saw an empty text field for this focus generation
        int charsSinceFocus;
        public volatile int VisibleGeneration = -1; // generation of the suggestions currently on screen
        public volatile bool Paused;               // loading, or disabled from the tray
        public Native.RECT PopupRect;              // screen rect of the suggestion bar (UI writes, hook reads)
        public readonly object RectLock = new object();

        sealed class Pending
        {
            public string Original, Replacement, Delimiter;
            public int Tick;
            public IntPtr PrevLayout;   // set when the fix was a keyboard-language switch
            public List<string> RejectWords;   // words to never touch again if this fix is undone
        }

        // The last few words typed, each followed by a single space, with nothing else typed since.
        // Used to fix earlier words once it becomes clear they were typed with the wrong keyboard.
        sealed class TypedWord
        {
            public List<KeyRec> Keys;
            public IntPtr Layout;    // layout that produced Screen
            public string Screen;    // what is on screen for this word
            public bool Changed;     // corrected or retyped: leave it alone
        }
        readonly List<TypedWord> history = new List<TypedWord>();

        public InputTracker(IKeyPlatform platform, Engine engine, Settings settings, Action<PopupState> publish, Action<string> onToggleHotkey, Action onFocusChanged)
        {
            this.platform = platform;
            this.onFocusChanged = onFocusChanged;
            this.engine = engine;
            this.settings = settings;
            this.publish = publish;
            this.onToggleHotkey = onToggleHotkey;
        }

        public void Start()
        {
            var ready = new ManualResetEvent(false);
            thread = new Thread(() =>
            {
                threadId = Native.GetCurrentThreadId();
                capsOn = (Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0;
                kbProc = KeyboardProc;
                mouseProc = MouseProc;
                IntPtr mod = Native.GetModuleHandle(null);
                kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, kbProc, mod, 0);
                int err1 = kbHook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
                Log.W("hooks: keyboard=" + (kbHook != IntPtr.Zero ? "ok" : "FAILED err " + err1) + " mouse=" + (mouseHook != IntPtr.Zero ? "ok" : "FAILED"));
                ready.Set();
                Native.MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.hwnd == IntPtr.Zero && msg.message == MsgAccept) { Accept(msg.wParam.ToInt32(), msg.lParam.ToInt32(), false); continue; }
                    if (msg.hwnd == IntPtr.Zero && msg.message == MsgReset) { lastForeground = IntPtr.Zero; Reset(true); continue; }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                Native.UnhookWindowsHookEx(kbHook);
                Native.UnhookWindowsHookEx(mouseHook);
            });
            thread.IsBackground = true;
            thread.Name = "TypeAssist hooks";
            thread.Start();
            ready.WaitOne();
        }

        public void Stop()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Called from the UI thread when a suggestion is clicked.</summary>
        public void RequestAccept(int index, int gen)
        {
            Native.PostThreadMessage(threadId, MsgAccept, new IntPtr(index), new IntPtr(gen));
        }

        public void RequestReset()
        {
            Native.PostThreadMessage(threadId, MsgReset, IntPtr.Zero, IntPtr.Zero);
        }

        // ------------------------------------------------------------------ hooks

        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int m = wParam.ToInt32();
                if (m == Native.WM_LBUTTONDOWN || m == Native.WM_RBUTTONDOWN || m == Native.WM_MBUTTONDOWN || m == Native.WM_XBUTTONDOWN)
                {
                    var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    bool onPopup;
                    lock (RectLock)
                    {
                        onPopup = info.pt.X >= PopupRect.Left && info.pt.X < PopupRect.Right
                               && info.pt.Y >= PopupRect.Top && info.pt.Y < PopupRect.Bottom;
                    }
                    if (!onPopup)
                    {
                        Reset(false);
                        BumpFocus();
                    }
                }
                else if (m == Native.WM_MOUSEWHEEL || m == Native.WM_MOUSEHWHEEL)
                {
                    if (shown.Count > 0 || lastFix != null) { shown = new List<Suggestion>(); lastFix = null; Publish(); }
                }
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int m = wParam.ToInt32();
                if (m == Native.WM_KEYDOWN || m == Native.WM_SYSKEYDOWN)
                {
                    var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    bool swallow = false;
                    try { swallow = OnKeyDown(k); }
                    catch (Exception ex) { Log.W("key error: " + ex); Reset(false); }
                    if (swallow) return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(kbHook, nCode, wParam, lParam);
        }

        // ------------------------------------------------------------------ key handling

        internal bool OnKeyDown(Native.KBDLLHOOKSTRUCT k)
        {
            if (k.dwExtraInfo == Magic) return false;                    // our own typing
            int vk = (int)k.vkCode;
            if (vk == Native.VK_CAPITAL) capsOn = !capsOn;
            bool ctrl = platform.IsDown(Native.VK_CONTROL);
            bool alt = platform.IsDown(Native.VK_MENU);
            bool shift = platform.IsDown(Native.VK_SHIFT);
            bool win = platform.IsDown(Native.VK_LWIN) || platform.IsDown(Native.VK_RWIN);

            // Global on/off: Ctrl+Alt+F12
            if (vk == Native.VK_F12 && ctrl && alt && !shift)
            {
                onToggleHotkey("toggle");
                Reset(false);
                return true;
            }
            if (!settings.Enabled || Paused) return false;
            if ((k.flags & Native.LLKHF_INJECTED) != 0) { Reset(false); return false; }   // other software typing

            CheckForeground();
            if (appExcluded) return false;

            if (IsModifier(vk)) return false;

            // Alt+1..3 picks a suggestion
            if (alt && !ctrl && !win && vk >= 0x31 && vk <= 0x33 && SuggestionsOnScreen())
            {
                int idx = vk - 0x31;
                if (idx < shown.Count) { Accept(idx, generation, true); return true; }
            }

            if (ctrl || alt || win)
            {
                // AltGr (Ctrl+Alt) can type characters on some layouts; anything else is a shortcut.
                if (!(ctrl && alt && !win)) { Reset(false); return false; }
            }

            switch (vk)
            {
                case Native.VK_TAB:
                    if (settings.AcceptWithTab && !shift && SuggestionsOnScreen() && shown.Count > 0)
                    {
                        Accept(0, generation, false);
                        return true;
                    }
                    Reset(false);
                    BumpFocus();
                    return false;
                case Native.VK_ESCAPE:
                    if (shown.Count > 0 || lastFix != null) { shown = new List<Suggestion>(); lastFix = null; Publish(); }
                    return false;
                case Native.VK_BACK:
                    return OnBackspace();
                case Native.VK_LEFT: case Native.VK_RIGHT: case Native.VK_UP: case Native.VK_DOWN:
                case Native.VK_HOME: case Native.VK_END: case Native.VK_PRIOR: case Native.VK_NEXT:
                case Native.VK_DELETE: case Native.VK_INSERT: case Native.VK_APPS:
                    Reset(false);
                    return false;
                case Native.VK_RETURN:
                    return OnDelimiter("\n", true);
            }

            IntPtr reported = platform.ReportedLayout();
            if (reported != lastReported)
            {
                // Windows reports keyboard changes for this app: it is not one of the misreporting ones.
                lastReported = reported; overrideHkl = IntPtr.Zero; pendingSwitch = IntPtr.Zero;
            }
            effective = overrideHkl != IntPtr.Zero ? overrideHkl : reported;
            if (pendingSwitch != IntPtr.Zero)
            {
                // Did the app follow our keyboard switch? If not, Windows can't tell us its real keyboard and we
                // must not retype anything in it: our idea of what is on screen may be wrong.
                if (reported == pendingSwitch) pendingSwitch = IntPtr.Zero;
                else if (++keysSinceSwitch >= 3)
                {
                    pendingSwitch = IntPtr.Zero;
                    if (LastApp.Length > 0 && unreliableApps.Add(LastApp))
                        Log.W("keyboard switching not followed in " + LastApp + ": no more retyping there");
                }
            }
            string text;
            bool packet = k.vkCode == VK_PACKET;
            if (packet)
            {
                // Some keyboards and apps deliver the finished character instead of a key. The character is
                // exact, but there are no keys to read in another language, so no keyboard-language guessing.
                text = k.scanCode > 0 && k.scanCode < 0xFFFF ? ((char)k.scanCode).ToString() : null;
                segmentBroken = true;
                if (LastApp.Length > 0 && unreliableApps.Add(LastApp))
                    Log.W("app delivers characters, not keys: " + LastApp + ": no keyboard-language fixes there");
            }
            else text = TranslateWith(effective, k.vkCode, k.scanCode, shift);
            if (!packet && text != null && text.Length == 1 && text[0] > ' ' && text[0] != '\u00A0')
            {
                if (segment.Count == 0)
                {
                    segAtBoundary = word.Length == 0 ? atBoundary : wordTrusted;
                    segAfterReset = word.Length == 0 ? afterReset : semiTrusted;
                    segFirstInField = word.Length == 0 && charsSinceFocus == 0 && EmptyFieldGeneration == FocusGeneration;
                    segPrev = prevWord; segPrev2 = prevWord2;
                    segSentence = sentenceStart;
                }
                if (segment.Count < 48) segment.Add(new KeyRec { Vk = k.vkCode, Scan = k.scanCode, Shift = shift });
            }
            if (DiagnosticsOn)
                DiagState = "app: " + LastApp + (appExcluded ? " (paused)" : "") + "    layout: " + WinPlatform.Describe(reported)
                    + (overrideHkl != IntPtr.Zero ? " → using " + Name(overrideHkl) : "")
                    + "    installed: " + InstalledNames()
                    + "    last key → " + (text == null ? "dead key" : text.Length == 0 ? "(none)" : "'" + text + "' U+" + ((int)text[0]).ToString("X4"))
                    + "    word so far: [" + word + "]";
            if (text == null) { Reset(false); return false; }      // dead key or unknown
            if (text.Length == 0) return false;                      // F-keys, media keys...
            char c = text[0];
            if (text.Length == 1 && TextUtil.IsWordChar(c)) return OnWordChar(c);
            if (c < ' ' && c != '\t') { Reset(false); return false; }
            return OnDelimiter(text, false);
        }

        static bool IsModifier(int vk)
        {
            return vk == Native.VK_SHIFT || vk == Native.VK_CONTROL || vk == Native.VK_MENU || vk == Native.VK_LSHIFT
                || vk == Native.VK_RSHIFT || vk == Native.VK_LCONTROL || vk == Native.VK_RCONTROL || vk == Native.VK_LMENU
                || vk == Native.VK_RMENU || vk == Native.VK_LWIN || vk == Native.VK_RWIN || vk == Native.VK_CAPITAL
                || vk == Native.VK_NUMLOCK || vk == Native.VK_SCROLL;
        }

        /// <summary>Test hook: behave as if the user clicked somewhere.</summary>
        internal void SimulateClick() { Reset(false); BumpFocus(); }

        void BumpFocus()
        {
            FocusGeneration++;
            charsSinceFocus = 0;
            onFocusChanged();
        }

        bool SuggestionsOnScreen()
        {
            return shown.Count > 0 && VisibleGeneration == generation;
        }

        bool Safe()
        {
            return CheckedGeneration == FocusGeneration && !InPasswordField;
        }

        void CheckForeground()
        {
            IntPtr fg = platform.Foreground();
            if (fg == lastForeground) return;
            lastForeground = fg;
            Reset(false);
            BumpFocus();
            string name = platform.ProcessName(fg);
            bool excl = settings.ExcludedApps.Contains(name);
            bool full = !excl && platform.IsFullScreen(fg);
            appExcluded = excl || full;
            if (name != lastLoggedApp) { lastLoggedApp = name; Log.W("app: " + name + (excl ? "  (excluded by list)" : full ? "  (full screen, paused)" : "  (active)")); }
            keysLogged = 0;
            if (name != "typeassist" && name != "explorer" && name != "shellexperiencehost" && name.Length > 0) LastApp = name;
        }

        bool capsOn;
        string lastLoggedApp;
        int keysLogged;
        int wordsLogged;

        // A word just finished that looks misspelt but has no single sure fix: its alternatives are offered
        // in the bar, and picking one replaces that word.
        sealed class Alternatives { public string Typed, Delimiter; public List<Suggestion> Items; }
        Alternatives alt;

        // ---- keyboard layout cross-check
        // Windows sometimes reports the wrong layout for the focused app (seen with Telegram: English reported
        // while typing Persian). We remember the keys of the current word and, at each space, read them with
        // both the Persian and the English layout; if the other reading is clearly a real word, we switch.
        struct KeyRec { public uint Vk, Scan; public bool Shift; }
        const uint VK_PACKET = 0xE7;
        bool segmentBroken;                 // a key of this word could not be recorded
        IntPtr pendingSwitch;               // keyboard we asked the app to switch to
        int keysSinceSwitch;
        readonly HashSet<string> unreliableApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool RetypeAllowed()
        {
            return settings.AutoSwitchLayout && !unreliableApps.Contains(LastApp);
        }
        readonly List<KeyRec> segment = new List<KeyRec>();
        bool segAtBoundary, segAfterReset, segFirstInField, segSentence;
        string segPrev, segPrev2;
        IntPtr overrideHkl, lastReported, effective;

        // Built-in Persian layout (standard Windows positions), read from physical scan codes. Used when
        // Windows gives us nothing reliable to decode the keys with.
        static readonly IntPtr BuiltInPersian = new IntPtr(-2);
        static readonly Dictionary<uint, char> FaByScan = BuildFaByScan();

        static Dictionary<uint, char> BuildFaByScan()
        {
            var d = new Dictionary<uint, char>();
            string r1 = "ضصثقفغعهخحجچ", r2 = "شسیبلاتنمکگ", r3 = "ظطزرذدپو";
            for (int i = 0; i < r1.Length; i++) d[(uint)(0x10 + i)] = r1[i];
            for (int i = 0; i < r2.Length; i++) d[(uint)(0x1E + i)] = r2[i];
            for (int i = 0; i < r3.Length; i++) d[(uint)(0x2C + i)] = r3[i];
            return d;
        }

        string InstalledNames()
        {
            var n = new List<string>();
            foreach (IntPtr h in platform.InstalledLayouts()) n.Add(Name(h));
            return string.Join(", ", n.ToArray());
        }

        static string Name(IntPtr hkl)
        {
            return hkl == BuiltInPersian ? "built-in Persian" : WinPlatform.Describe(hkl);
        }

        static bool IsPersian(IntPtr hkl)
        {
            return hkl == BuiltInPersian || ((long)hkl & 0xFFFF) == 0x0429;
        }

        string TranslateWith(IntPtr hkl, uint vk, uint scan, bool shift)
        {
            if (hkl == BuiltInPersian)
            {
                if (vk == Native.VK_SPACE) return shift ? "\u200C" : " ";
                char c;
                if (shift)
                {
                    if (scan == 0x23) return "آ";
                    if (scan == 0x2E) return "ژ";
                    if (scan == 0x32) return "ء";
                }
                else if (FaByScan.TryGetValue(scan, out c)) return c.ToString();
                return platform.Translate(vk, scan, shift, capsOn, lastReported);
            }
            return platform.Translate(vk, scan, shift, capsOn, hkl);
        }

        string Render(IntPtr hkl) { return segmentBroken ? null : RenderKeys(segment, hkl); }

        string RenderKeys(List<KeyRec> keys, IntPtr hkl)
        {
            var sb = new StringBuilder();
            foreach (var r in keys)
            {
                string t = TranslateWith(hkl, r.Vk, r.Scan, r.Shift);
                if (t == null || t.Length != 1) return null;
                sb.Append(t);
            }
            return sb.ToString();
        }

        static bool AllWordChars(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            foreach (char c in t) if (!TextUtil.IsWordChar(c)) return false;
            return true;
        }

        /// <summary>0 = not a word, 1 = letters, 2 = a fixable typo, 3 = a known word.</summary>
        int Plausibility(string t)
        {
            if (!AllWordChars(t) || TextUtil.HasDigit(t)) return 0;
            Lang l = TextUtil.DetectLang(t);
            if (l == Lang.None) return 0;
            lock (engine)
            {
                if (engine.IsKnown(TextUtil.Normalize(t, l), l)) return 3;
                if (t.Length >= 3 && engine.TryCorrect(t, null, false) != null) return 2;
            }
            return 1;
        }

        /// <summary>Every other way to read the keys: the installed layouts, plus our own Persian table.</summary>
        List<IntPtr> OtherLayouts()
        {
            var list = new List<IntPtr>();
            foreach (IntPtr h in platform.InstalledLayouts())
                if (h != effective && !list.Contains(h)) list.Add(h);
            if (effective != BuiltInPersian) list.Add(BuiltInPersian);
            return list;
        }

        /// <summary>Score of what we think was typed: the weakest of its words (punctuation splits words).</summary>
        int TokensPlausibility(string t)
        {
            int min = int.MaxValue, count = 0;
            var tok = new StringBuilder();
            for (int i = 0; i <= t.Length; i++)
            {
                if (i < t.Length && TextUtil.IsWordChar(t[i])) { tok.Append(t[i]); continue; }
                if (tok.Length > 0)
                {
                    int p = tok.Length == 1 ? 1 : Plausibility(tok.ToString());
                    if (p < min) min = p;
                    count++;
                    tok.Length = 0;
                }
            }
            return count == 0 ? 0 : min;
        }

        /// <summary>At a space: if the keys of this word spell a known word in another layout, switch to it.
        /// Returns 0 = no change, 1 = switched.</summary>
        IntPtr switchFrom, switchTo;
        string switchScreen, switchText;
        int switchMineScore;

        /// <summary>
        /// After CheckLayout switched: should the word on screen be retyped in the other language and the keyboard
        /// switched? Only when the evidence is strong: what we saw is not a word at all, and the other reading is a
        /// common word. Retyping is safe even when Windows misreported the layout and the screen already shows the
        /// other reading, because the same characters are written back.
        /// </summary>
        /// <summary>
        /// True when the keys also spell a word (or a near-word) in any other keyboard layout, including the one
        /// we think is active. Then the keys mean something in both languages and are never switched, whatever
        /// Windows reports about the current keyboard (بعد and fun are the same three keys).
        /// </summary>
        bool MeaningfulElsewhere(List<KeyRec> keys, IntPtr target, string targetText)
        {
            var layouts = new List<IntPtr>(platform.InstalledLayouts());
            if (!layouts.Contains(effective)) layouts.Add(effective);
            layouts.Add(BuiltInPersian);
            foreach (IntPtr h in layouts)
            {
                if (h == target) continue;
                string r = RenderKeys(keys, h);
                if (r == null || r == targetText || r.Length < 2) continue;
                if (TokensPlausibility(r) >= 2)
                {
                    Diag("not switched: [" + r + "] is also a word (" + Name(h) + ")");
                    return true;
                }
            }
            return false;
        }

        static string LastWordOf(string t)
        {
            int i = t.LastIndexOf(' ');
            return i < 0 ? t : t.Substring(i + 1);
        }

        /// <summary>
        /// The current word was typed with the keyboard it is now on (because we just switched it, or the user did).
        /// Words right before it that are gibberish in the layout they were typed with, but real words in this
        /// layout, were typed with the wrong keyboard too: retype them. Example: "sghl خوبی " → "سلام خوبی ".
        /// </summary>
        bool FixEarlierWords(string cur, string d, bool curTyped, bool curWasSwitched)
        {
            // The current word must be a real word in the current layout: proof the user now types correctly.
            if (TokensPlausibility(cur) < 3) return false;
            var fix = new List<TypedWord>();
            var newTexts = new List<string>();
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var h = history[i];
                if (h.Changed || h.Layout == effective) break;
                if (h.Screen.Length < 2) break;   // single letters (a, I, و) are words in either language
                if (h.Screen.Length <= 3) { bool caps = false; foreach (char ch in h.Screen) if (char.IsUpper(ch)) caps = true; if (caps) break; }
                string again = RenderKeys(h.Keys, effective);
                if (again == null || again == h.Screen || Plausibility(again) != 3) break;
                if (MeaningfulElsewhere(h.Keys, effective, again)) break;
                if (TokensPlausibility(h.Screen) > 1) break;   // only clear gibberish is retyped
                if (again.Length <= 3)
                {
                    // Short words are easily something else: only very common ones count.
                    Lang al = TextUtil.DetectLang(again);
                    bool common;
                    lock (engine) common = al != Lang.None && engine.IsCommonWord(TextUtil.Normalize(again, al), al);
                    if (!common) break;
                }
                fix.Insert(0, h);
                newTexts.Insert(0, again);
            }
            if (fix.Count == 0) return false;

            var oldJoined = new System.Text.StringBuilder();
            foreach (var h in fix) oldJoined.Append(h.Screen).Append(' ');
            string newJoined = string.Join(" ", newTexts.ToArray()) + " ";
            // What is on screen after those words: the current word, plus the space if we already typed it.
            int tail = cur.Length + (curTyped ? d.Length : 0);
            Type(oldJoined.Length + tail, newJoined + cur + d, false);

            string curOriginal = curTyped && lastFix != null ? lastFix.Original : cur;
            var reject = new List<string>();
            foreach (var h in fix) reject.Add(h.Screen);
            reject.AddRange(newTexts);
            if (curWasSwitched && lastFix != null)
            {
                if (lastFix.RejectWords != null) reject.AddRange(lastFix.RejectWords);
                else reject.Add(lastFix.Original);
            }
            lastFix = new Pending
            {
                Original = oldJoined + curOriginal,
                Replacement = newJoined + cur,
                Delimiter = d,
                Tick = Environment.TickCount,
                PrevLayout = curWasSwitched && lastFix != null ? lastFix.PrevLayout : IntPtr.Zero,
                RejectWords = reject
            };
            foreach (var h in fix) history.Remove(h);
            Log.W("wrong keyboard language: " + fix.Count + " earlier word(s) retyped");
            Diag("earlier words typed with the wrong keyboard: [" + oldJoined.ToString().Trim() + "] → [" + newJoined.Trim() + "]");
            return true;
        }

        /// <summary>Ask the app to switch keyboards and read the keys that follow with the new layout.</summary>
        void SwitchKeyboard(IntPtr target)
        {
            platform.RequestLayout(target);
            pendingSwitch = target;
            keysSinceSwitch = 0;
            effective = target;
            IntPtr now = platform.ReportedLayout();
            lastReported = now;
            // Windows may apply the request a moment later (or never, in apps that misreport the layout):
            // until it reports the new layout, keep reading keys with it ourselves.
            overrideHkl = now == target ? IntPtr.Zero : target;
        }

        IntPtr LayoutFixTarget()
        {
            if (!RetypeAllowed() || switchText == null) return IntPtr.Zero;
            return switchTo == BuiltInPersian ? platform.PersianLayout : switchTo;
        }

        int CheckLayout()
        {
            switchText = null;
            if (segment.Count == 0) return 0;
            string mine = Render(effective);
            if (mine == null) return 0;
            int a = TokensPlausibility(mine);
            // Only clear gibberish counts: anything that is a word, a rare word or a likely typo in the language
            // we read it in is left alone, even if the other reading is a word too (بعد / fun, مثلا / legh).
            if (a >= 2) return 0;
            if (mine.Length <= 3)
                foreach (char ch in mine)
                    if (char.IsUpper(ch)) return 0;   // a capital letter was typed on purpose (Di, OK, US): leave it
            IntPtr bestH = IntPtr.Zero;
            string bestText = null;
            foreach (IntPtr h in OtherLayouts())
            {
                string theirs = Render(h);
                if (theirs == null || theirs == mine) continue;
                if (Plausibility(theirs) != 3) continue;
                // Only a common word counts as evidence: a wrong guess here changes how every following
                // key is read, so rare words, names and slang never trigger a switch.
                Lang tl = TextUtil.DetectLang(theirs);
                bool common;
                lock (engine) common = engine.IsCommonWord(TextUtil.Normalize(theirs, tl), tl);
                if (!common) continue;
                bestH = h; bestText = theirs;
                if (h != BuiltInPersian) break;
            }
            if (bestText == null) return 0;
            if (MeaningfulElsewhere(segment, bestH, bestText)) return 0;
            switchFrom = effective;
            switchScreen = mine;
            switchMineScore = a;
            switchTo = bestH;
            switchText = bestText;
            overrideHkl = bestH == lastReported ? IntPtr.Zero : bestH;
            effective = bestH;
            Log.W("layout cross-check: switched to " + Name(bestH) + " (Windows reports " + Name(lastReported) + ")");
            Diag("keys read as [" + mine + "] but they spell [" + bestText + "] → using " + Name(bestH));
            // What is on screen is the other reading: rebuild the word from the start of these keys.
            word.Length = 0;
            word.Append(bestText);
            prevWord = segPrev; prevWord2 = segPrev2;
            sentenceStart = segSentence;
            atBoundary = false;
            alt = null;
            wordTrusted = segAtBoundary || segFirstInField;
            semiTrusted = !wordTrusted && segAfterReset;
            return 1;
        }

        /// <summary>At punctuation inside a word: true if the keys are more likely a word of another layout.</summary>
        bool OtherLayoutMidWord()
        {
            if (segment.Count < 2) return false;
            foreach (IntPtr h in OtherLayouts())
            {
                string theirs = Render(h);
                if (!AllWordChars(theirs)) continue;
                Lang l = TextUtil.DetectLang(theirs);
                if (l == Lang.None) continue;
                lock (engine)
                {
                    var m = engine.Model(l);
                    string key = TextUtil.Normalize(theirs, l);
                    if (m.Contains(key) || m.Complete(key, 1, 20000).Count > 0) return true;
                }
            }
            return false;
        }

        // Live diagnostics (only while the Diagnostics window is open; kept in memory, never written to disk)
        public volatile bool DiagnosticsOn;
        public readonly List<string> DiagLines = new List<string>();
        public volatile string DiagState = "";
        void Diag(string line)
        {
            if (!DiagnosticsOn) return;
            lock (DiagLines)
            {
                DiagLines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
                if (DiagLines.Count > 30) DiagLines.RemoveAt(0);
            }
        }
        bool afterReset = true; // nothing typed since a click / focus change / cursor move
        bool semiTrusted;       // first word after such a reset: we may have missed letters before it

        bool OnWordChar(char c)
        {
            if (keysLogged < 3) { keysLogged++; Log.W("  letter seen: script=" + (TextUtil.IsPersianLetter(c) ? "fa" : TextUtil.IsLatinLetter(c) ? "en" : "other U+" + ((int)c).ToString("X4")) + " trusted=" + (word.Length == 0 ? (atBoundary || (charsSinceFocus == 0 && EmptyFieldGeneration == FocusGeneration)) : wordTrusted) + " focusChecked=" + (CheckedGeneration == FocusGeneration) + " password=" + InPasswordField); }
            lastFix = null;
            alt = null;
            justAccepted = false;
            if (word.Length == 0)
            {
                // Trusted if we saw the boundary, or this is the first thing typed into an empty field.
                wordTrusted = atBoundary || (charsSinceFocus == 0 && EmptyFieldGeneration == FocusGeneration);
                semiTrusted = !wordTrusted && afterReset;
                if (wordTrusted && charsSinceFocus == 0 && !atBoundary) sentenceStart = true;
            }
            charsSinceFocus++;
            atBoundary = false;
            word.Append(c);
            UpdateSuggestions();
            return false;
        }

        bool OnBackspace()
        {
            if (lastFix != null && Environment.TickCount - lastFix.Tick < 8000 && Safe())
            {
                // Undo the autocorrection: put back exactly what was typed.
                var f = lastFix;
                lastFix = null;
                segment.Clear();
                Type(f.Replacement.Length + f.Delimiter.Length, f.Original + f.Delimiter, false);
                lock (engine)
                {
                    if (f.RejectWords != null) foreach (string w in f.RejectWords) engine.Reject(w);
                    else engine.Reject(f.Original);
                }
                history.Clear();
                if (f.PrevLayout != IntPtr.Zero)
                {
                    // Undo of a keyboard-language fix: also switch the keyboard back.
                    SwitchKeyboard(f.PrevLayout);
                }
                prevWord = f.Delimiter == " " ? LastWordOf(f.Original) : null; if (prevWord == null) prevWord2 = null;
                word.Length = 0;
                atBoundary = true;
                shown = new List<Suggestion>();
                Publish();
                return true;
            }
            lastFix = null;
            alt = null;
            history.Clear();
            if (justAccepted)
            {
                justAccepted = false;
                // Removing the automatic space: the user is still on that word.
                Reset(false);
                return false;
            }
            if (segment.Count > 0) segment.RemoveAt(segment.Count - 1);
            if (word.Length > 0)
            {
                word.Length--;
                if (word.Length == 0) { atBoundary = wordTrusted; }
                UpdateSuggestions();
            }
            else
            {
                Reset(false);   // deleting into text we have not seen
            }
            return false;
        }

        /// <summary>After these, the next word is a fresh word (not part of a URL, path, e-mail...).</summary>
        static bool StartsFreshWord(string d)
        {
            return d == " " || d == "\n" || d == "\t" || d == "(" || d == "[" || d == "\"" || d == "«" || d == "\u00A0";
        }

        static bool IsSentenceEnd(string d)
        {
            return d == "." || d == "!" || d == "?" || d == "؟" || d == "\n";
        }

        static bool IsCorrectionTrigger(string d)
        {
            if (d == " " || d == "\n") return true;
            return d.Length == 1 && ".,!?;:)]}\"،؛؟»".IndexOf(d[0]) >= 0;
        }

        bool OnDelimiter(string d, bool isEnter)
        {
            bool swallow = false;
            string finished = null;

            // Smart space: "word␣" + "." becomes "word." and a second space is not doubled.
            if (justAccepted && settings.SmartSpace)
            {
                justAccepted = false;
                if (d == " ") { Publish(); return true; }
                if (d.Length == 1 && ".,!?;:،؛؟".IndexOf(d[0]) >= 0)
                {
                    Type(1, d + " ", false);
                    bool end = IsSentenceEnd(d);
                    prevWord = null; prevWord2 = null;
                    word.Length = 0;
                    atBoundary = true;
                    wordTrusted = true;
                    sentenceStart = end;
                    UpdateSuggestions();
                    return true;
                }
            }
            justAccepted = false;
            lastFix = null;
            alt = null;

            bool whitespace = d == " " || d == "\n" || d == "\t" || d == "\u00A0";
            bool otherLanguage = false;
            int segLen = segment.Count;
            IntPtr layoutFix = IntPtr.Zero;
            if (whitespace)
            {
                if (CheckLayout() == 1 && d == " " && Safe())
                {
                    layoutFix = LayoutFixTarget();
                    if (layoutFix != IntPtr.Zero) Log.W("wrong keyboard language: " + switchScreen.Length + " keys retyped as " + Name(switchTo));
                }
            }
            else if (word.Length > 0 || segment.Count > 1) otherLanguage = OtherLayoutMidWord();
            if (otherLanguage)
            {
                // e.g. "و" typed while we think the layout is English (we see ","): leave everything alone.
                // (The key stays part of this word's keys, so the earlier-words history stays valid.)
                word.Length = 0;
                wordTrusted = false;
                semiTrusted = false;
                atBoundary = false;
                prevWord = null; prevWord2 = null;
                UpdateSuggestions();
                return false;
            }

            if (word.Length > 0 && wordTrusted && Safe())
            {
                string typed = word.ToString();
                finished = typed;
                Correction fix = null;
                if (settings.AutoCorrect && IsCorrectionTrigger(d))
                {
                    lock (engine) { fix = engine.TryCorrect(typed, PrevKey(), sentenceStart); }
                }
                Diag("word [" + typed + "] lang=" + TextUtil.DetectLang(typed) + " → " + (fix == null ? "no fix (" + engine.Why + ")" : "fixed to [" + fix.Replacement + "]"));
                if (wordsLogged < 40)
                {
                    wordsLogged++;
                    Lang wl = TextUtil.DetectLang(typed);
                    Log.W("  word done: lang=" + wl + " len=" + typed.Length + " fix=" + (fix == null ? "none" : fix.Reason)
                          + " autocorrect=" + settings.AutoCorrect + " trigger=" + IsCorrectionTrigger(d));
                }
                if (fix != null && fix.Replacement != typed)
                {
                    Log.W("  corrected a word (" + fix.Reason + ")");
                    if (isEnter)
                    {
                        Type(typed.Length, fix.Replacement, false);
                        SendEnter();
                    }
                    else
                    {
                        Type(typed.Length, fix.Replacement + d, false);
                        lastFix = new Pending { Original = typed, Replacement = fix.Replacement, Delimiter = d, Tick = Environment.TickCount };
                    }
                    swallow = true;
                    finished = fix.Replacement;
                }
                if (fix == null && !isEnter && (d == " " || d == "\u00A0") && settings.AutoCorrect)
                {
                    List<Suggestion> c;
                    lock (engine) { c = engine.CorrectionCandidates(typed, 3); }
                    if (c.Count > 0) alt = new Alternatives { Typed = typed, Delimiter = d, Items = c };
                }
                if (settings.Learn) lock (engine) { engine.Learn(prevWord2, prevWord, finished); }
            }
            else if (word.Length > 0 && semiTrusted && Safe())
            {
                // First word after a click: usually a whole new word, but we did not see what precedes it.
                // Only clear-cut fixes of longer words, never learned from.
                string typed = word.ToString();
                Correction fix = null;
                if (settings.AutoCorrect && IsCorrectionTrigger(d) && typed.Length >= 4)
                    lock (engine) { fix = engine.TryCorrect(typed, null, false); }
                if (fix != null && (fix.Replacement.Length < 1 || char.ToLowerInvariant(fix.Replacement[0]) != char.ToLowerInvariant(typed[0])))
                    fix = null;
                Diag("first word after click [" + typed + "] → " + (fix == null ? "no fix" : "fixed to [" + fix.Replacement + "]"));
                if (wordsLogged < 40) { wordsLogged++; Log.W("  first word after click: lang=" + TextUtil.DetectLang(typed) + " len=" + typed.Length + " fix=" + (fix == null ? "none" : fix.Reason)); }
                string result = typed;
                if (fix != null && fix.Replacement != typed)
                {
                    if (isEnter) { Type(typed.Length, fix.Replacement, false); SendEnter(); }
                    else
                    {
                        Type(typed.Length, fix.Replacement + d, false);
                        lastFix = new Pending { Original = typed, Replacement = fix.Replacement, Delimiter = d, Tick = Environment.TickCount };
                    }
                    swallow = true;
                    result = fix.Replacement;
                }
                Lang rl = TextUtil.DetectLang(result);
                bool known;
                lock (engine) { known = rl != Lang.None && engine.IsKnown(TextUtil.Normalize(result, rl), rl); }
                finished = known ? result : null;   // usable as context for the next word, not learned
            }
            else if (word.Length > 0)
            {
                finished = null;   // untrusted or unsafe: do not learn, do not use as context
                Diag("word [" + word + "] skipped: trusted=" + wordTrusted + " focusChecked=" + (CheckedGeneration == FocusGeneration) + " password=" + InPasswordField);
                if (wordsLogged < 40)
                {
                    wordsLogged++;
                    Log.W("  word skipped: lang=" + TextUtil.DetectLang(word.ToString()) + " trusted=" + wordTrusted
                          + " focusChecked=" + (CheckedGeneration == FocusGeneration) + " password=" + InPasswordField);
                }
            }

            if (layoutFix != IntPtr.Zero)
            {
                // The word was typed with the wrong keyboard language: put the intended word on screen
                // (a typo fix above may already have done so) and switch the keyboard for what follows.
                if (!swallow)
                {
                    Type(segLen, switchText + d, false);
                    swallow = true;
                    lastFix = new Pending { Original = switchScreen, Replacement = switchText, Delimiter = d, Tick = Environment.TickCount,
                                            RejectWords = new List<string> { switchScreen, switchText } };
                    if (finished == null && (wordTrusted || semiTrusted)) finished = switchText;
                }
                else if (lastFix != null)
                {
                    lastFix.Original = switchScreen;
                    lastFix.RejectWords = new List<string> { switchScreen, switchText, lastFix.Replacement };
                }
                if (lastFix != null)
                    lastFix.PrevLayout = switchFrom == BuiltInPersian ? platform.PersianLayout : switchFrom;
                SwitchKeyboard(layoutFix);
                Diag("typed with the wrong keyboard: [" + switchScreen + "] → [" + (lastFix != null ? lastFix.Replacement : switchText) + "], keyboard switched to " + Name(layoutFix));
            }

            // ---- earlier words typed with the wrong keyboard
            if (d == " " && segLen > 0 && Safe())
            {
                bool beliefOnly = switchText != null && layoutFix == IntPtr.Zero;
                string cur = swallow ? (lastFix != null ? lastFix.Replacement : null) : Render(effective);
                if (cur != null && !beliefOnly && RetypeAllowed() && history.Count > 0)
                {
                    if (FixEarlierWords(cur, d, swallow, layoutFix != IntPtr.Zero)) swallow = true;
                    cur = lastFix != null && swallow ? LastWordOf(lastFix.Replacement) : cur;
                }
                if (cur != null && !beliefOnly)
                {
                    history.Add(new TypedWord { Keys = new List<KeyRec>(segment), Layout = effective, Screen = cur, Changed = swallow });
                    if (history.Count > 4) history.RemoveAt(0);
                }
                else history.Clear();
            }
            else history.Clear();

            if (word.Length > 0 && finished == null) { prevWord = null; prevWord2 = null; }
            charsSinceFocus++;
            AfterBoundary(d, finished);
            if (isEnter) lastFix = null;   // the line may already be sent; never "undo" into a new field
            return swallow;
        }

        void AfterBoundary(string d, string finished)
        {
            if (d == " " || d == "\n" || d == "\t" || d == "\u00A0") { segment.Clear(); segmentBroken = false; }
            afterReset = false;
            semiTrusted = false;
            word.Length = 0;
            atBoundary = StartsFreshWord(d);
            wordTrusted = atBoundary;
            if (IsSentenceEnd(d)) { sentenceStart = true; prevWord = null; prevWord2 = null; }
            else if (d == " ") { if (finished != null) { prevWord2 = prevWord; prevWord = finished; sentenceStart = false; } }
            else { prevWord = null; prevWord2 = null; if (d != "\"" && d != "(" && d != "«") sentenceStart = false; }
            UpdateSuggestions();
        }

        string PrevKey()
        {
            if (prevWord == null) return null;
            Lang l = TextUtil.DetectLang(prevWord);
            return l == Lang.None ? null : TextUtil.Normalize(prevWord, l);
        }

        void Reset(bool keepPopup)
        {
            word.Length = 0;
            wordTrusted = false;
            atBoundary = false;
            prevWord = null; prevWord2 = null;
            sentenceStart = false;
            justAccepted = false;
            lastFix = null;
            alt = null;
            afterReset = true;
            semiTrusted = false;
            segment.Clear();
            segmentBroken = false;
            pendingSwitch = IntPtr.Zero;
            history.Clear();
            if (!keepPopup && shown.Count > 0) { shown = new List<Suggestion>(); Publish(); }
            else if (!keepPopup) Publish();
        }

        // ------------------------------------------------------------------ suggestions

        void UpdateSuggestions()
        {
            var items = new List<Suggestion>();
            if (settings.Suggestions && Safe())
            {
                lock (engine)
                {
                    if (word.Length > 0 && semiTrusted && !wordTrusted)
                    {
                        items = engine.Suggest(word.ToString(), null, 3);
                        string w0 = word.ToString();
                        items.RemoveAll(x => !x.Text.StartsWith(w0, StringComparison.Ordinal));
                    }
                    else if (word.Length > 0 && wordTrusted)
                    {
                        items = engine.Suggest(word.ToString(), prevWord, 3, prevWord2);
                        if (items.Count == 0 && word.Length >= 3)
                            items = engine.CorrectionCandidates(word.ToString(), 3);   // unknown word, unsure fix
                    }
                    else if (word.Length == 0 && alt != null)
                    {
                        items = alt.Items;
                    }
                    else if (word.Length == 0 && atBoundary && prevWord != null)
                    {
                        items = engine.Suggest("", prevWord, 3, prevWord2);
                        if (sentenceStart) foreach (var s in items) s.Text = TextUtil.MatchCase("A", s.Text);
                    }
                }
            }
            shown = items;
            Publish();
        }

        void Publish()
        {
            generation++;
            var st = new PopupState { Generation = generation, Items = shown };
            if (lastFix != null)
            {
                st.UndoOriginal = lastFix.Original;
                st.UndoReplacement = lastFix.Replacement;
            }
            string sample = word.Length > 0 ? word.ToString() : prevWord;
            if (sample == null && lastFix != null) sample = lastFix.Replacement;
            st.Rtl = sample != null && TextUtil.DetectLang(sample) == Lang.Fa;
            if (sample == null && shown.Count > 0) st.Rtl = TextUtil.DetectLang(shown[0].Text) == Lang.Fa;
            publish(st);
        }

        void Accept(int index, int gen, bool releaseAlt)
        {
            if (gen != generation || index < 0 || index >= shown.Count || !Safe()) return;
            var s = shown[index];
            segment.Clear();
            history.Clear();
            if (alt != null && word.Length == 0 && s.Kind == SuggestionKind.Correction)
            {
                // Replace the word just typed (and its space) with the chosen spelling.
                var a = alt;
                alt = null;
                Type(a.Typed.Length + a.Delimiter.Length, s.Text + a.Delimiter, releaseAlt);
                if (settings.Learn) lock (engine) { engine.Learn(null, null, s.Text); }
                prevWord = s.Text;
                atBoundary = true;
                wordTrusted = true;
                UpdateSuggestions();
                return;
            }
            string current = word.ToString();
            string text = s.Text;
            string space = settings.SmartSpace ? " " : "";
            if (current.Length > 0 && text.StartsWith(current, StringComparison.Ordinal))
                Type(0, text.Substring(current.Length) + space, releaseAlt);
            else
                Type(current.Length, text + space, releaseAlt);
            if (settings.Learn) lock (engine) { engine.Learn(prevWord2, prevWord, text); }
            word.Length = 0;
            atBoundary = true;
            wordTrusted = true;
            lastFix = null;
            if (settings.SmartSpace)
            {
                prevWord2 = prevWord; prevWord = text;
                sentenceStart = false;
                justAccepted = true;
                UpdateSuggestions();
            }
            else
            {
                word.Append(text);
                atBoundary = false;
                shown = new List<Suggestion>();
                Publish();
            }
        }

        // ------------------------------------------------------------------ injection

        void Type(int backspaces, string text, bool releaseAlt)
        {
            var list = new List<KeyAction>();
            int altVk = 0;
            if (releaseAlt)
            {
                altVk = platform.IsDown(Native.VK_RMENU) ? Native.VK_RMENU : Native.VK_LMENU;
                AddVk(list, Native.VK_MENU_MASK, false); AddVk(list, Native.VK_MENU_MASK, true);
                AddVk(list, altVk, true);
            }
            for (int i = 0; i < backspaces; i++) { AddVk(list, Native.VK_BACK, false); AddVk(list, Native.VK_BACK, true); }
            foreach (char c in text)
            {
                if (c == '\n') { AddVk(list, Native.VK_RETURN, false); AddVk(list, Native.VK_RETURN, true); continue; }
                list.Add(new KeyAction { Ch = c }); list.Add(new KeyAction { Ch = c, Up = true });
            }
            if (releaseAlt)
            {
                AddVk(list, altVk, false);
                AddVk(list, Native.VK_MENU_MASK, false); AddVk(list, Native.VK_MENU_MASK, true);
            }
            platform.Send(list);
        }

        void SendEnter()
        {
            var list = new List<KeyAction>();
            AddVk(list, Native.VK_RETURN, false); AddVk(list, Native.VK_RETURN, true);
            platform.Send(list);
        }

        static void AddVk(List<KeyAction> list, int vk, bool up)
        {
            list.Add(new KeyAction { Vk = vk, Up = up });
        }
    }
}
