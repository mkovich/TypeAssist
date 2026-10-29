using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TypeAssist
{
    public enum SuggestionKind { Completion, NextWord, Correction }

    public sealed class Suggestion
    {
        public string Text;          // full word to end up with
        public SuggestionKind Kind;
        public double Score;
        public override string ToString() { return Kind + ":" + Text + "(" + Score.ToString("0.00") + ")"; }
    }

    public sealed class Correction
    {
        public string Original;
        public string Replacement;
        public string Reason;
    }

    /// <summary>Settings that change how bold the engine is.</summary>
    public sealed class EngineOptions
    {
        public bool ZwnjFix = true;          // میخواهم -> می‌خواهم
        public bool Learn = true;            // learn words and word pairs from typing
        public int LearnThreshold = 3;       // unknown word typed N times becomes known
        public double Margin = 1.0;          // required lead of the best candidate (natural log units)
    }

    /// <summary>
    /// Conservative autocorrect + next-word prediction for Persian and English.
    /// Not thread safe: callers lock on the instance.
    /// </summary>
    public sealed class Engine
    {
        public readonly LanguageModel Fa = new LanguageModel(Lang.Fa);
        public readonly LanguageModel En = new LanguageModel(Lang.En);
        public EngineOptions Options = new EngineOptions();

        readonly Dictionary<string, int> userWords = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, int[]> userCase = new Dictionary<string, int[]>(StringComparer.Ordinal);  // [capitalised, lower]
        readonly Dictionary<string, Dictionary<string, int>> userNext = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        readonly HashSet<string> rejected = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> finglish = new HashSet<string>(StringComparer.Ordinal);
        public bool Dirty;

        static readonly string[] ZwnjPrefixes = { "نمی", "می" };
        static readonly string[] ZwnjSuffixes = { "هایشان", "هایتان", "هایمان", "هایش", "هایت", "هایم", "هایی", "های", "ها" };

        public Engine()
        {
            foreach (string w in FinglishWords.Split(' '))
                if (w.Length > 0) finglish.Add(w);
        }

        public LanguageModel Model(Lang l) { return l == Lang.Fa ? Fa : l == Lang.En ? En : null; }

        public void LoadData(Func<string, Stream> open)
        {
            using (var s = open("words_fa.tsv.gz")) Fa.LoadWords(s, 60000);
            using (var s = open("next_fa.tsv.gz")) Fa.LoadNext(s);
            using (var s = open("words_en.tsv.gz")) En.LoadWords(s, 60000);
            using (var s = open("next_en.tsv.gz")) En.LoadNext(s);
            using (var s = open("fix_en.tsv.gz"))
                foreach (string line in LanguageModel.ReadLines(s))
                {
                    int t = line.IndexOf('\t');
                    if (t > 0) knownFixes[line.Substring(0, t)] = line.Substring(t + 1);
                }
        }

        readonly Dictionary<string, string> knownFixes = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Frequency of key in the word list, or -1 when it must be treated as a real word regardless
        /// (learned from the user, Finglish, or a word the user insisted on by undoing a fix).
        /// </summary>
        public int WordWeight(string key, Lang lang)
        {
            if (UserCount(key) >= Options.LearnThreshold || rejected.Contains(key)) return -1;
            if (lang == Lang.En && finglish.Contains(key)) return -1;
            var m = Model(lang);
            return m == null ? 0 : m.Count(key);
        }

        /// <summary>How confidently key (already normalised) is a real, commonly used word.</summary>
        public bool IsCommonWord(string key, Lang lang)
        {
            if (UserCount(key) >= Options.LearnThreshold && key.Length >= 3) return true;
            var m = Model(lang);
            if (m == null) return false;
            int c = m.Count(key);
            if (key.Length <= 1) return false;
            if (key.Length == 2) return c >= 1000000;     // only the most common two-letter words (به، از، in, is)
            if (key.Length == 3) return c >= 20000;
            return c >= 2000;
        }

        int UserCount(string key)
        {
            int c;
            return userWords.TryGetValue(key, out c) ? c : 0;
        }

        public bool IsKnown(string key, Lang lang)
        {
            if (lang == Lang.En && finglish.Contains(key)) return true;
            if (UserCount(key) >= Options.LearnThreshold) return true;
            if (rejected.Contains(key)) return true;
            var m = Model(lang);
            return m != null && m.Contains(key);
        }

        // ------------------------------------------------------------------ correction

        /// <summary>Returns a correction only when it is very likely what the user meant.</summary>
        public Correction TryCorrect(string typed, string prevKey)
        {
            return TryCorrect(typed, prevKey, false);
        }

        /// <param name="sentenceStart">true when the word begins a sentence (capital letters are then expected).</param>
        public string Why;
        public Correction TryCorrect(string typed, string prevKey, bool sentenceStart)
        {
            Why = "other";
            if (string.IsNullOrEmpty(typed) || typed.Length > 24) return null;
            Lang lang = TextUtil.DetectLang(typed);
            if (lang == Lang.None || TextUtil.HasDigit(typed)) return null;
            if (lang == Lang.En)
            {
                if (TextUtil.HasInnerUpper(typed)) return null;           // iPhone, McDonald, camelCase
                if (typed.Length > 1 && TextUtil.IsAllUpper(typed)) return null; // acronyms
                if (typed.IndexOf('\'') == 0) return null;
            }
            string key = TextUtil.Normalize(typed, lang);
            if (rejected.Contains(key)) return null;
            if (lang == Lang.En && typed == "i")
                return new Correction { Original = typed, Replacement = "I", Reason = "case" };
            if (UserCount(key) >= Options.LearnThreshold) return null;

            // A capital letter in the middle of a sentence usually means a name: leave it alone.
            bool capital = lang == Lang.En && char.IsUpper(typed[0]);
            if (capital && !sentenceStart) return null;

            string fixedWord;
            if (lang == Lang.En && knownFixes.TryGetValue(key, out fixedWord))
            {
                string rep = fixedWord.StartsWith("I'", StringComparison.Ordinal) ? fixedWord : TextUtil.MatchCase(typed, fixedWord);
                return new Correction { Original = typed, Replacement = rep, Reason = "known" };
            }
            if (key.Length < 3) return null;

            var model = Model(lang);
            if (capital) return null;    // sentence-initial capitals only get the curated fixes above

            // A word in the Persian list can still be a frequent typo of a far more common word
            // (the lists are built from real-world text, which contains typos).
            bool rareKnown = false;
            if (IsKnown(key, lang))
            {
                int own = model.Count(key);
                bool rare = own > 0 && UserCount(key) == 0 && (lang == Lang.Fa ? own < 300
                            : own < 700 && key.Length >= 4 && !finglish.Contains(key));
                if (!rare)
                {
                    if (lang == Lang.Fa && Options.ZwnjFix) return ZwnjFix(typed, key, model);
                    return null;
                }
                rareKnown = true;
            }
            // e.g. "کتابش", "می‌ترکند", "commonplaces": a known word plus an affix. Such words are only
            // corrected when a far more common word is one small slip away.
            double morph = rareKnown ? double.MinValue : MorphologyScore(key, lang);

            double maxCost, minCount;
            if (lang == Lang.Fa)
            {
                maxCost = rareKnown ? 0.6 : key.Length <= 3 ? 0.6 : key.Length == 4 ? 0.8 : key.Length <= 6 ? 1.0 : 1.4;
                minCount = rareKnown ? Math.Max(30000, model.Count(key) * 1000.0) : 1500;
            }
            else
            {
                maxCost = rareKnown ? 0.55 : key.Length <= 3 ? 0.7 : key.Length <= 5 ? 1.0 : 1.6;
                minCount = rareKnown ? Math.Max(20000, model.Count(key) * 120.0) : 800;
            }

            string best = null, second = null;
            double bestScore = double.MinValue, secondScore = double.MinValue;
            foreach (string cand in model.Candidates(key))
            {
                if (cand == key) continue;
                double cost = EditDistance.Distance(key, cand, maxCost);
                if (cost > maxCost) continue;
                // A rare English word is only treated as a typo for swapped letters or a missed double letter
                // (helo, thier); other one-letter differences are usually two real words (cluck/click).
                if (rareKnown && lang == Lang.En && !SwapOrMissedDouble(key, cand)) continue;
                int c = model.Count(cand);
                double score = Math.Log(c + 1) - 4.0 * cost;
                if (prevKey != null)
                {
                    int total;
                    foreach (var s in model.Next(prevKey, out total))
                        if (s.Word == cand) { score += 0.5 * Math.Log(1 + s.Count); break; }
                    score += 0.5 * Math.Log(1 + UserPair(prevKey, cand));
                }
                if (score > bestScore)
                {
                    second = best; secondScore = bestScore;
                    best = cand; bestScore = score;
                }
                else if (score > secondScore)
                {
                    second = cand; secondScore = score;
                }
            }
            if (best == null) { Why = "nocand"; return null; }
            if (model.Count(best) < minCount) { Why = "mincount"; return null; }
            if (bestScore - morph < Options.Margin + 0.5) { Why = "morph"; return null; }
            if (key.Length == 3 && model.Rank(best) > 6000) { Why = "short"; return null; }
            if (second != null && bestScore - secondScore < Options.Margin) { Why = "margin:" + best + "|" + second; return null; }
            return new Correction { Original = typed, Replacement = TextUtil.MatchCase(typed, best), Reason = "typo" };
        }

        static readonly string[] FaPrefixes = { "نمی\u200c", "می\u200c", "نمی", "می", "بی\u200c", "بی", "نا", "ب", "ن" };
        static readonly string[] FaSuffixes = {
            "هایشان", "هایتان", "هایمان", "ترین", "هایش", "هایت", "هایم", "هایی", "های", "ها",
            "شان", "تان", "مان", "اند", "اید", "ایم", "ای", "ام", "اش", "ات", "یم", "ید", "ند", "ان",
            "ش", "ت", "م", "ی", "ن", "تر", "نده", "ه" };
        static readonly string[] EnSuffixes = {
            "'s", "s", "es", "ed", "d", "ing", "ly", "er", "ers", "est", "ness", "ful", "less", "able", "ment", "ments" };
        static readonly string[] EnPrefixes = { "un", "re", "non", "pre", "over", "under", "mis", "anti", "co" };

        bool StemKnown(string stem, Lang lang)
        {
            if (stem.Length < 3) return false;
            if (UserCount(stem) >= Options.LearnThreshold) return true;
            var m = Model(lang);
            return m.Count(stem) >= (lang == Lang.Fa ? 200 : 800);
        }

        public string DebugStem(string w) { Lang l = TextUtil.DetectLang(w); string k = TextUtil.Normalize(w, l); return MorphologyScore(k, l).ToString("0.0"); }

        /// <summary>
        /// How believable it is that key is a real word built from a known stem plus affixes
        /// (log frequency of the stem minus a penalty per affix). double.MinValue when there is no such reading.
        /// </summary>
        double MorphologyScore(string key, Lang lang)
        {
            double best = double.MinValue;
            var m = Model(lang);
            if (lang == Lang.Fa)
            {
                bool first = true;
                foreach (string w in FaPrefixVariants(key))
                {
                    double pre = first ? 0 : 1.5;
                    first = false;
                    if (pre > 0 && StemKnown(w, lang)) best = Math.Max(best, Math.Log(m.Count(w) + 1) - pre);
                    foreach (string s1 in FaSuffixes)
                    {
                        string a = StripSuffix(w, s1);
                        if (a == null) continue;
                        double p1 = pre + 1.5 + (s1.Length == 1 ? 1.0 : 0);
                        if (StemKnown(a, lang)) best = Math.Max(best, Math.Log(m.Count(a) + 1) - p1);
                        foreach (string s2 in FaSuffixes)
                        {
                            string b = StripSuffix(a, s2);
                            if (b == null) continue;
                            double p2 = p1 + 1.5 + (s2.Length == 1 ? 1.0 : 0);
                            if (StemKnown(b, lang)) best = Math.Max(best, Math.Log(m.Count(b) + 1) - p2);
                        }
                    }
                }
                return best;
            }
            bool firstEn = true;
            foreach (string w in EnPrefixVariants(key))
            {
                double pre = firstEn ? 0 : 1.5;
                firstEn = false;
                if (pre > 0 && StemKnown(w, lang)) best = Math.Max(best, Math.Log(m.Count(w) + 1) - pre);
                foreach (string sfx in EnSuffixes)
                {
                    if (w.Length <= sfx.Length + 2 || !w.EndsWith(sfx, StringComparison.Ordinal)) continue;
                    string stem = w.Substring(0, w.Length - sfx.Length);
                    char last = stem[stem.Length - 1];
                    if (sfx == "es" && !(stem.EndsWith("s") || stem.EndsWith("x") || stem.EndsWith("z") || stem.EndsWith("ch") || stem.EndsWith("sh") || stem.EndsWith("o"))) continue;
                    if (sfx[0] == 'e' && last == 'e') continue;            // "tireed" is not tire+ed
                    double p = pre + 1.5;
                    var cands = new List<string> { stem };
                    if (sfx[0] == 'e' || sfx[0] == 'i' || sfx[0] == 'a') cands.Add(stem + "e");        // tired, making
                    if (last == 'i') cands.Add(stem.Substring(0, stem.Length - 1) + "y");                  // tries, happily
                    if (stem.Length > 3 && last == stem[stem.Length - 2] && "bdglmnprt".IndexOf(last) >= 0
                        && (sfx == "ed" || sfx == "ing" || sfx == "er" || sfx == "est" || sfx == "ers"))
                        cands.Add(stem.Substring(0, stem.Length - 1));                                    // stopped
                    foreach (string c in cands)
                        if (StemKnown(c, lang)) best = Math.Max(best, Math.Log(m.Count(c) + 1) - p);
                }
            }
            return best;
        }

        static IEnumerable<string> FaPrefixVariants(string key)
        {
            yield return key;
            foreach (string p in FaPrefixes)
                if (key.Length > p.Length + 1 && key.StartsWith(p, StringComparison.Ordinal))
                    yield return key.Substring(p.Length);
        }

        static IEnumerable<string> EnPrefixVariants(string key)
        {
            yield return key;
            foreach (string p in EnPrefixes)
                if (key.Length > p.Length + 3 && key.StartsWith(p, StringComparison.Ordinal))
                    yield return key.Substring(p.Length);
        }

        static string StripSuffix(string w, string sfx)
        {
            if (w.Length < sfx.Length + 2 || !w.EndsWith(sfx, StringComparison.Ordinal)) return null;
            string r = w.Substring(0, w.Length - sfx.Length);
            if (r.Length > 0 && r[r.Length - 1] == TextUtil.ZWNJ) r = r.Substring(0, r.Length - 1);
            return r.Length >= 2 ? r : null;
        }

        static bool SwapOrMissedDouble(string typed, string cand)
        {
            if (typed.Length == cand.Length)
            {
                int i = 0;
                while (i < typed.Length && typed[i] == cand[i]) i++;
                return i + 1 < typed.Length && typed[i] == cand[i + 1] && typed[i + 1] == cand[i]
                    && typed.Substring(i + 2) == cand.Substring(i + 2);
            }
            if (cand.Length == typed.Length + 1)
            {
                int i = 0;
                while (i < typed.Length && typed[i] == cand[i]) i++;
                return i > 0 && cand[i] == cand[i - 1] && typed.Substring(i) == cand.Substring(i + 1);
            }
            return false;
        }

        Correction ZwnjFix(string typed, string key, LanguageModel model)
        {
            if (key.IndexOf(TextUtil.ZWNJ) >= 0) return null;
            int baseCount = model.Count(key) + UserCount(key) * 50;
            string bestV = null;
            int bestC = 0;
            foreach (string p in ZwnjPrefixes)
            {
                if (key.Length > p.Length + 1 && key.StartsWith(p, StringComparison.Ordinal))
                {
                    string v = p + TextUtil.ZWNJ + key.Substring(p.Length);
                    int c = model.Count(v);
                    if (c > bestC) { bestC = c; bestV = v; }
                    break;
                }
            }
            foreach (string sfx in ZwnjSuffixes)
            {
                if (key.Length > sfx.Length + 1 && key.EndsWith(sfx, StringComparison.Ordinal))
                {
                    string v = key.Substring(0, key.Length - sfx.Length) + TextUtil.ZWNJ + sfx;
                    int c = model.Count(v);
                    if (c > bestC) { bestC = c; bestV = v; }
                    break;
                }
            }
            if (bestV == null || bestC < 300 || bestC < baseCount * 1.5) return null;
            return new Correction { Original = typed, Replacement = bestV, Reason = "zwnj" };
        }

        /// <summary>Spelling alternatives for an unknown word (shown, never applied automatically).</summary>
        public List<Suggestion> CorrectionCandidates(string typed, int max)
        {
            var res = new List<Suggestion>();
            Lang lang = TextUtil.DetectLang(typed);
            if (lang == Lang.None) return res;
            string key = TextUtil.Normalize(typed, lang);
            if (key.Length < 3 || IsKnown(key, lang)) return res;
            var model = Model(lang);
            foreach (string cand in model.Candidates(key))
            {
                if (cand == key) continue;
                double cost = EditDistance.Distance(key, cand, 1.6);
                if (cost > 1.6) continue;
                res.Add(new Suggestion { Text = TextUtil.MatchCase(typed, cand), Kind = SuggestionKind.Correction, Score = Math.Log(model.Count(cand) + 1) - 4.0 * cost });
            }
            res.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (res.Count > max) res.RemoveRange(max, res.Count - max);
            return res;
        }

        // ------------------------------------------------------------------ prediction

        int UserPair(string a, string b)
        {
            Dictionary<string, int> d;
            int c;
            if (userNext.TryGetValue(a, out d) && d.TryGetValue(b, out c)) return c;
            return 0;
        }

        /// <summary>
        /// partial = letters typed so far of the current word ("" right after a space).
        /// prevWord = the previous word as typed (null when unknown).
        /// </summary>
        public List<Suggestion> Suggest(string partial, string prevWord, int max)
        {
            return Suggest(partial, prevWord, max, null);
        }

        /// <summary>Key under which "what the user types after these two words" is learned.</summary>
        static string PairKey(string w1, string w2) { return w1 + " " + w2; }

        /// <param name="prevWord2">the word before prevWord, for the user's own two-word habits (may be null).</param>
        public List<Suggestion> Suggest(string partial, string prevWord, int max, string prevWord2)
        {
            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            Lang lang;
            string prevKey = null;
            if (!string.IsNullOrEmpty(prevWord))
            {
                Lang pl = TextUtil.DetectLang(prevWord);
                if (pl != Lang.None) prevKey = TextUtil.Normalize(prevWord, pl);
            }
            // What the user has typed after these exact two words: the strongest personal signal.
            Dictionary<string, int> tri = null;
            int triTotal = 0;
            if (prevKey != null && !string.IsNullOrEmpty(prevWord2) && TextUtil.DetectLang(prevWord2) == TextUtil.DetectLang(prevWord))
            {
                string k2 = TextUtil.Normalize(prevWord2, TextUtil.DetectLang(prevWord2));
                if (userNext.TryGetValue(PairKey(k2, prevKey), out tri))
                    foreach (var kv in tri) triTotal += kv.Value;
            }

            if (string.IsNullOrEmpty(partial))
            {
                if (prevKey == null) return new List<Suggestion>();
                lang = TextUtil.DetectLang(prevWord);
                var model = Model(lang);
                int total;
                var succ = model.Next(prevKey, out total);
                Dictionary<string, int> u;
                userNext.TryGetValue(prevKey, out u);
                int utotal = 0;
                if (u != null) foreach (var kv in u) utotal += kv.Value;
                double denom = total + 20.0 * utotal + 3.0;
                foreach (var s in succ) scores[s.Word] = s.Count / denom;
                if (u != null)
                    foreach (var kv in u)
                    {
                        double prev;
                        scores.TryGetValue(kv.Key, out prev);
                        scores[kv.Key] = prev + 20.0 * kv.Value / denom;
                    }
                if (tri != null)
                    foreach (var kv in tri)
                    {
                        double prev;
                        scores.TryGetValue(kv.Key, out prev);
                        scores[kv.Key] = prev + 0.6 * kv.Value / (triTotal + 1.0);
                    }
                var list = Top(scores, max, SuggestionKind.NextWord, null);
                list.RemoveAll(s => s.Score < 0.06 || s.Text.Length < 2);
                if (list.Count == 0 || list[0].Score < 0.12) return new List<Suggestion>();
                return list;
            }

            lang = TextUtil.DetectLang(partial);
            if (lang == Lang.None || TextUtil.HasDigit(partial)) return new List<Suggestion>();
            string key = TextUtil.Normalize(partial, lang);
            if (key.Length < 2) return new List<Suggestion>();
            var m = Model(lang);

            // Unigram completions
            var uni = m.Complete(key, 8, 40000);
            double uniSum = 1;
            foreach (var kv in uni) uniSum += kv.Value;
            foreach (var kv in uni) scores[kv.Key] = 0.4 * kv.Value / uniSum;

            // User vocabulary
            int userSum = 1;
            var userMatches = new List<KeyValuePair<string, int>>();
            foreach (var kv in userWords)
            {
                if (kv.Value >= 2 && kv.Key.Length > key.Length && kv.Key.StartsWith(key, StringComparison.Ordinal))
                {
                    userMatches.Add(kv);
                    userSum += kv.Value;
                }
            }
            foreach (var kv in userMatches)
            {
                double prev;
                scores.TryGetValue(kv.Key, out prev);
                scores[kv.Key] = prev + 0.3 * kv.Value / userSum;
            }

            // Context: what usually follows the previous word
            if (prevKey != null && TextUtil.DetectLang(prevWord) == lang)
            {
                int total;
                var succ = m.Next(prevKey, out total);
                double bsum = 1;
                foreach (var s in succ) if (s.Word.StartsWith(key, StringComparison.Ordinal)) bsum += s.Count;
                foreach (var s in succ)
                {
                    if (s.Word.Length <= key.Length || !s.Word.StartsWith(key, StringComparison.Ordinal)) continue;
                    double prev;
                    scores.TryGetValue(s.Word, out prev);
                    scores[s.Word] = prev + 0.6 * s.Count / bsum;
                }
                Dictionary<string, int> u;
                if (userNext.TryGetValue(prevKey, out u))
                    foreach (var kv in u)
                    {
                        if (kv.Key.Length <= key.Length || !kv.Key.StartsWith(key, StringComparison.Ordinal)) continue;
                        double prev;
                        scores.TryGetValue(kv.Key, out prev);
                        scores[kv.Key] = prev + 0.6 * kv.Value / (kv.Value + 1.0);
                    }
                if (tri != null)
                    foreach (var kv in tri)
                    {
                        if (kv.Key.Length <= key.Length || !kv.Key.StartsWith(key, StringComparison.Ordinal)) continue;
                        double prev;
                        scores.TryGetValue(kv.Key, out prev);
                        scores[kv.Key] = prev + 1.0 * kv.Value / (kv.Value + 1.0);
                    }
            }

            // Only words that save the user at least two keystrokes
            var keys = new List<string>(scores.Keys);
            foreach (string k in keys) if (k.Length - key.Length < 2) scores.Remove(k);
            var res = Top(scores, max, SuggestionKind.Completion, partial);
            return res;
        }

        /// <summary>Names the user writes with a capital (Hormuz, Dubai) are suggested that way.</summary>
        string Display(string key)
        {
            int[] cc;
            if (key.Length > 1 && userCase.TryGetValue(key, out cc) && cc[0] > cc[1] && En.Count(key) < 50000)
                return char.ToUpperInvariant(key[0]) + key.Substring(1);
            return key;
        }

        List<Suggestion> Top(Dictionary<string, double> scores, int max, SuggestionKind kind, string typed)
        {
            var list = new List<Suggestion>();
            foreach (var kv in scores)
            {
                string shown = Display(kv.Key);
                list.Add(new Suggestion { Text = typed == null ? shown : (shown != kv.Key ? TextUtil.MatchCase(typed, shown) : TextUtil.MatchCase(typed, kv.Key)), Kind = kind, Score = kv.Value });
            }
            list.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        // ------------------------------------------------------------------ learning

        public void Learn(string prevWord, string word) { Learn(null, prevWord, word); }

        /// <summary>
        /// Remember that the user typed word after prevWord (and after "prevWord2 prevWord").
        /// Over time this makes suggestions follow the user's own phrases.
        /// </summary>
        public void Learn(string prevWord2, string prevWord, string word)
        {
            if (!Options.Learn || string.IsNullOrEmpty(word)) return;
            Lang lang = TextUtil.DetectLang(word);
            if (lang == Lang.None || TextUtil.HasDigit(word) || word.Length > 30) return;
            string key = TextUtil.Normalize(word, lang);
            if (key.Length < 1) return;
            int c;
            userWords.TryGetValue(key, out c);
            userWords[key] = c + 1;
            if (lang == Lang.En && word.Length > 1 && !TextUtil.IsAllUpper(word))
            {
                int[] cc;
                if (!userCase.TryGetValue(key, out cc)) { cc = new int[2]; userCase[key] = cc; }
                cc[char.IsUpper(word[0]) ? 0 : 1]++;
            }
            if (!string.IsNullOrEmpty(prevWord) && TextUtil.DetectLang(prevWord) == lang)
            {
                string pk = TextUtil.Normalize(prevWord, lang);
                Dictionary<string, int> d;
                if (!userNext.TryGetValue(pk, out d)) { d = new Dictionary<string, int>(StringComparer.Ordinal); userNext[pk] = d; }
                d.TryGetValue(key, out c);
                d[key] = c + 1;
                if (!string.IsNullOrEmpty(prevWord2) && TextUtil.DetectLang(prevWord2) == lang)
                {
                    string pk2 = PairKey(TextUtil.Normalize(prevWord2, lang), pk);
                    if (!userNext.TryGetValue(pk2, out d)) { d = new Dictionary<string, int>(StringComparer.Ordinal); userNext[pk2] = d; }
                    d.TryGetValue(key, out c);
                    d[key] = c + 1;
                }
                if (userNext.Count > 200000) PruneUserNext();
            }
            Dirty = true;
        }

        /// <summary>Keep the learned data small: drop word pairs seen only once.</summary>
        void PruneUserNext()
        {
            var empty = new List<string>();
            foreach (var a in userNext)
            {
                var once = new List<string>();
                foreach (var b in a.Value) if (b.Value <= 1) once.Add(b.Key);
                foreach (string k in once) a.Value.Remove(k);
                if (a.Value.Count == 0) empty.Add(a.Key);
            }
            foreach (string k in empty) userNext.Remove(k);
        }

        /// <summary>The user undid a correction: never correct this word again.</summary>
        public void Reject(string original)
        {
            Lang lang = TextUtil.DetectLang(original);
            if (lang == Lang.None) return;
            rejected.Add(TextUtil.Normalize(original, lang));
            Dirty = true;
        }

        public void ForgetLearned()
        {
            userWords.Clear(); userNext.Clear(); userCase.Clear(); rejected.Clear(); Dirty = true;
        }

        // ------------------------------------------------------------------ persistence

        public void SaveUser(string dir)
        {
            WriteUser(dir, SnapshotUser());
        }

        /// <summary>Serialise the learned data (fast; call under the engine lock).</summary>
        public string[] SnapshotUser()
        {
            var sb = new StringBuilder();
            foreach (var kv in userWords)
            {
                sb.Append(kv.Key).Append('\t').Append(kv.Value);
                int[] cc;
                if (userCase.TryGetValue(kv.Key, out cc)) sb.Append('\t').Append(cc[0]).Append('\t').Append(cc[1]);
                sb.Append('\n');
            }
            string words = sb.ToString();
            sb.Clear();
            foreach (var a in userNext)
                foreach (var b in a.Value)
                    sb.Append(a.Key).Append('\t').Append(b.Key).Append('\t').Append(b.Value).Append('\n');
            string next = sb.ToString();
            sb.Clear();
            foreach (string w in rejected) sb.Append(w).Append('\n');
            Dirty = false;
            return new[] { words, next, sb.ToString() };
        }

        /// <summary>Write a snapshot to disk (slow; call outside the lock).</summary>
        public static void WriteUser(string dir, string[] snap)
        {
            Directory.CreateDirectory(dir);
            WriteAtomic(Path.Combine(dir, "user_words.tsv"), snap[0]);
            WriteAtomic(Path.Combine(dir, "user_next.tsv"), snap[1]);
            WriteAtomic(Path.Combine(dir, "never_correct.txt"), snap[2]);
        }

        static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public void LoadUser(string dir)
        {
            string p = Path.Combine(dir, "user_words.tsv");
            if (File.Exists(p))
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    int c;
                    if (f.Length >= 2 && int.TryParse(f[1], out c)) userWords[f[0]] = c;
                    int up, low;
                    if (f.Length == 4 && int.TryParse(f[2], out up) && int.TryParse(f[3], out low)) userCase[f[0]] = new[] { up, low };
                }
            p = Path.Combine(dir, "user_next.tsv");
            if (File.Exists(p))
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    int c;
                    if (f.Length != 3 || !int.TryParse(f[2], out c)) continue;
                    Dictionary<string, int> d;
                    if (!userNext.TryGetValue(f[0], out d)) { d = new Dictionary<string, int>(StringComparer.Ordinal); userNext[f[0]] = d; }
                    d[f[1]] = c;
                }
            p = Path.Combine(dir, "never_correct.txt");
            if (File.Exists(p))
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                    if (line.Trim().Length > 0) rejected.Add(line.Trim());
        }

        // Common Persian words written in Latin letters: never "correct" these to English.
        const string FinglishWords =
            "salam slm salaam chetori chetory khobi khoobi khubi khubam khobam khoobam merci mersi mamnoon mamnun mamnoun " +
            "mrc mrsi bale baleh are areh na nah chera chra chi chie chiye koja kojai kojaei kojaii key kei kay keyi " +
            "inja unja onja hala hamin hamon hamun man to shoma ma oona ona unha inha in un oon ye yek do se char panj " +
            "kheili khaili kheyli khili kheyly kam ziad ziyad zyad dige digeh bad baad ghabl ghablan alan alaan farda " +
            "dirooz diruz emrooz emruz emshab shab rooz ruz saat dari daram dare darim darid daran nadaram nadari " +
            "mikham mikhay mikhad mikhaym mikhayn nemikham bia biya boro beram berim miam miay miad mirim miram mire " +
            "bashe bashi basham bashim kon konam koni kone konim mikonam mikoni mikone nakon begoo begu bego migam " +
            "migi mige migan nemidonam nemidunam midonam midunam midoni midooni bebakhshid bebakhshin bebin bebinam " +
            "ghorbanet ghorboonet azizam aziz joon jan jun dadash abji maman baba khoda khodaya inshallah enshallah " +
            "eshgh eshq delam tang dooset doset doostet dooste dust rafigh refigh agha khanoom khanum khanom " +
            "chikar chekar chikara karet kar kari kare dars daneshgah madrese khone khoone khune ghaza ghaza'i " +
            "pas pasfarda ham hamash vali amma age ageh chon choon chun ya ba az be ke ro ra rooye zir bala " +
            "payin paein khob khub khoob ok okey oki bashe vay vaay ey ay aha akhe ahan ahh haha hahaha khande " +
            "ghashang ghashange bahal bahale ajib baramoon baram barat barash baratoon hamechi hamechiz " +
            "chizi kasi hichi hich hichkas kojast chiye chie ine une";
    }
}
