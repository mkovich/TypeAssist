using System;
using System.Collections.Generic;
using System.Text;

namespace TypeAssist
{
    public enum Lang { None = 0, Fa = 1, En = 2 }

    public static class TextUtil
    {
        public const char ZWNJ = '‌';

        public static bool IsPersianLetter(char c)
        {
            if (c == ZWNJ) return true;
            if (c >= 'ء' && c <= 'ي') return true;   // Arabic letters
            if (c >= 'پ' && c <= 'ۓ')                   // Persian extensions (پ چ ژ ک گ ی ...)
            {
                return true;
            }
            return false;
        }

        public static bool IsLatinLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        /// <summary>Characters that may be part of a word.</summary>
        public static bool IsWordChar(char c)
        {
            return IsPersianLetter(c) || IsLatinLetter(c) || c == '\'' || char.IsDigit(c)
                || (c >= 'ً' && c <= 'ْ') || c == 'ـ' || c == 'ٰ';
        }

        public static Lang DetectLang(string w)
        {
            bool fa = false, en = false;
            foreach (char c in w)
            {
                if (IsLatinLetter(c)) en = true;
                else if (IsPersianLetter(c) && c != ZWNJ) fa = true;
            }
            if (fa && !en) return Lang.Fa;
            if (en && !fa) return Lang.En;
            return Lang.None;
        }

        public static bool HasDigit(string w)
        {
            foreach (char c in w) if (char.IsDigit(c)) return true;
            return false;
        }

        /// <summary>Lookup key: Persian orthographic normalisation, English lower case.</summary>
        public static string Normalize(string w, Lang lang)
        {
            if (lang == Lang.En) return w.ToLowerInvariant();
            var sb = new StringBuilder(w.Length);
            foreach (char ch in w)
            {
                char c = ch;
                if (c == 'ي' || c == 'ى') c = 'ی';      // ي ى -> ی
                else if (c == 'ك') c = 'ک';                  // ك -> ک
                else if ((c >= 'ً' && c <= 'ْ') || c == 'ـ' || c == 'ٰ') continue;
                if (c == ZWNJ && (sb.Length == 0 || sb[sb.Length - 1] == ZWNJ)) continue;
                sb.Append(c);
            }
            while (sb.Length > 0 && sb[sb.Length - 1] == ZWNJ) sb.Length--;
            return sb.ToString();
        }

        /// <summary>Apply the casing pattern of the typed word to a dictionary word.</summary>
        public static string MatchCase(string typed, string word)
        {
            if (string.IsNullOrEmpty(typed) || string.IsNullOrEmpty(word)) return word;
            if (!IsLatinLetter(typed[0])) return word;
            if (char.IsUpper(typed[0]))
            {
                if (typed.Length > 1 && IsAllUpper(typed)) return word.ToUpperInvariant();
                return char.ToUpperInvariant(word[0]) + word.Substring(1);
            }
            return word;
        }

        public static bool IsAllUpper(string w)
        {
            int letters = 0;
            foreach (char c in w)
            {
                if (IsLatinLetter(c)) { letters++; if (!char.IsUpper(c)) return false; }
            }
            return letters > 0;
        }

        /// <summary>Upper case letters after the first one, e.g. iPhone, McDonald, camelCase.</summary>
        public static bool HasInnerUpper(string w)
        {
            for (int i = 1; i < w.Length; i++) if (char.IsUpper(w[i])) return true;
            return false;
        }
    }

    /// <summary>Physical keyboard geometry, used to make fat-finger typos cheaper.</summary>
    public static class Keyboard
    {
        static readonly Dictionary<char, int[]> pos = new Dictionary<char, int[]>();

        static Keyboard()
        {
            string[] en = { "qwertyuiop", "asdfghjkl", "zxcvbnm" };
            // Standard Persian layout (Windows "Persian"/ISIRI 9147) on the same physical keys.
            string[] fa = { "ضصثقفغعهخحجچ", "شسیبلاتنمکگ", "ظطزرذدپو" };
            Add(en);
            Add(fa);
            // Shifted letters on the Persian layout share a key with an unshifted one.
            Alias('آ', 'ا'); Alias('ژ', 'ز'); Alias('ئ', 'م'); Alias('ء', 'ی');
            Alias('أ', 'ا'); Alias('ؤ', 'و'); Alias('ۀ', 'ه'); Alias('ي', 'ی'); Alias('ك', 'ک');
        }

        static void Add(string[] rows)
        {
            for (int r = 0; r < rows.Length; r++)
                for (int c = 0; c < rows[r].Length; c++)
                {
                    pos[rows[r][c]] = new[] { r, c * 2 + r };   // rows are staggered by half a key
                }
        }

        static void Alias(char a, char b)
        {
            int[] p;
            if (pos.TryGetValue(b, out p)) pos[a] = p;
        }

        public static bool Adjacent(char a, char b)
        {
            a = char.ToLowerInvariant(a); b = char.ToLowerInvariant(b);
            if (a == b) return true;
            int[] pa, pb;
            if (!pos.TryGetValue(a, out pa) || !pos.TryGetValue(b, out pb)) return false;
            int dr = Math.Abs(pa[0] - pb[0]);
            int dc = Math.Abs(pa[1] - pb[1]);
            return dr <= 1 && dc <= 2 && !(dr == 0 && dc == 0);
        }

        public static bool SameKey(char a, char b)
        {
            int[] pa, pb;
            if (!pos.TryGetValue(a, out pa) || !pos.TryGetValue(b, out pb)) return false;
            return pa[0] == pb[0] && pa[1] == pb[1];
        }
    }

    /// <summary>Weighted optimal-string-alignment distance tuned for typing errors.</summary>
    public static class EditDistance
    {
        // Persian letters that sound alike and are commonly confused when spelling.
        static readonly string[] SoundAlike = { "زذضظ", "سصث", "تط", "هح", "قغ", "اعآ", "یئ" };

        static bool SoundsAlike(char a, char b)
        {
            foreach (string g in SoundAlike)
                if (g.IndexOf(a) >= 0 && g.IndexOf(b) >= 0) return true;
            return false;
        }

        static double SubCost(char a, char b)
        {
            if (a == b) return 0;
            if (char.ToLowerInvariant(a) == char.ToLowerInvariant(b)) return 0.1;
            if (Keyboard.SameKey(a, b)) return 0.4;
            if (Keyboard.Adjacent(a, b)) return 0.5;
            if (SoundsAlike(a, b)) return 0.7;
            return 1.0;
        }

        /// <param name="extra">true: the user typed this char by mistake; false: the user left it out.</param>
        static double IndelCost(string s, int i, bool extra)
        {
            char c = s[i];
            if (c == TextUtil.ZWNJ) return 0.3;
            bool doubled = (i > 0 && s[i - 1] == c) || (i + 1 < s.Length && s[i + 1] == c);
            if (extra)
            {
                if (doubled) return 0.4;                                             // key bounced
                if ((i > 0 && Keyboard.Adjacent(s[i - 1], c)) || (i + 1 < s.Length && Keyboard.Adjacent(s[i + 1], c)))
                    return 0.6;                                                      // brushed a neighbouring key
                return 1.0;
            }
            if (doubled) return 0.5;                                                 // missed one of a double letter
            return 0.8;                                                              // skipped a key
        }

        /// <summary>Cost to turn typed into target. Returns a value &gt; max when it exceeds max.</summary>
        public static double Distance(string typed, string target, double max)
        {
            int n = typed.Length, m = target.Length;
            if (Math.Abs(n - m) > 2) return max + 1;
            var d = new double[n + 1, m + 1];
            for (int i = 0; i <= n; i++) d[i, 0] = i == 0 ? 0 : d[i - 1, 0] + IndelCost(typed, i - 1, true);
            for (int j = 0; j <= m; j++) d[0, j] = j == 0 ? 0 : d[0, j - 1] + IndelCost(target, j - 1, false);
            for (int i = 1; i <= n; i++)
            {
                double rowMin = double.MaxValue;
                for (int j = 1; j <= m; j++)
                {
                    double v = d[i - 1, j - 1] + SubCost(typed[i - 1], target[j - 1]);
                    v = Math.Min(v, d[i - 1, j] + IndelCost(typed, i - 1, true));   // extra char typed
                    v = Math.Min(v, d[i, j - 1] + IndelCost(target, j - 1, false));  // char missed
                    if (i > 1 && j > 1 && typed[i - 1] == target[j - 2] && typed[i - 2] == target[j - 1]
                        && typed[i - 1] != typed[i - 2])
                    {
                        v = Math.Min(v, d[i - 2, j - 2] + 0.5);               // swapped letters
                    }
                    d[i, j] = v;
                    if (v < rowMin) rowMin = v;
                }
                if (rowMin > max) return max + 1;
            }
            return d[n, m];
        }
    }
}
