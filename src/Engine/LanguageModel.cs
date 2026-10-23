using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace TypeAssist
{
    public struct Successor
    {
        public string Word;
        public int Count;
        public Successor(string w, int c) { Word = w; Count = c; }
    }

    /// <summary>Static dictionary + bigram data for one language.</summary>
    public sealed class LanguageModel
    {
        public readonly Lang Lang;
        readonly Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, int> rank = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, Successor[]> next = new Dictionary<string, Successor[]>(StringComparer.Ordinal);
        readonly Dictionary<string, int> nextTotal = new Dictionary<string, int>(StringComparer.Ordinal);

        // Prefix search: words sorted ordinally.
        string[] sorted;
        int[] sortedCounts;

        // Symmetric-delete index (SymSpell, max edit 1) over the correction targets.
        string[] targets;
        Dictionary<int, int> head;
        int[] linkNext, linkWord;
        int linkCount;

        public LanguageModel(Lang lang) { Lang = lang; }

        public int WordCount { get { return counts.Count; } }

        public static IEnumerable<string> ReadLines(Stream s)
        {
            using (var gz = new GZipStream(s, CompressionMode.Decompress))
            using (var r = new StreamReader(gz, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null) yield return line;
            }
        }

        public void LoadWords(Stream s, int targetLimit)
        {
            var list = new List<KeyValuePair<string, int>>();
            foreach (string line in ReadLines(s))
            {
                int t = line.IndexOf('\t');
                if (t <= 0) continue;
                string w = line.Substring(0, t);
                int c = int.Parse(line.Substring(t + 1));
                if (counts.ContainsKey(w)) continue;
                counts[w] = c;
                rank[w] = list.Count;
                list.Add(new KeyValuePair<string, int>(w, c));
            }
            // Prefix arrays
            var keys = new string[list.Count];
            for (int i = 0; i < list.Count; i++) keys[i] = list[i].Key;
            Array.Sort(keys, StringComparer.Ordinal);
            sorted = keys;
            sortedCounts = new int[keys.Length];
            for (int i = 0; i < keys.Length; i++) sortedCounts[i] = counts[keys[i]];

            // Correction targets: the most frequent words of length >= 2
            var tg = new List<string>();
            foreach (var kv in list)
            {
                if (tg.Count >= targetLimit) break;
                if (kv.Key.Length >= 2) tg.Add(kv.Key);
            }
            targets = tg.ToArray();
            int est = 0;
            foreach (string w in targets) est += w.Length + 1;
            head = new Dictionary<int, int>(est);
            linkNext = new int[est];
            linkWord = new int[est];
            linkCount = 0;
            for (int id = 0; id < targets.Length; id++)
            {
                string w = targets[id];
                AddKey(Hash(w, -1), id);
                for (int i = 0; i < w.Length; i++)
                {
                    if (i > 0 && w[i] == w[i - 1]) continue; // same delete string
                    AddKey(Hash(w, i), id);
                }
            }
        }

        void AddKey(int key, int id)
        {
            int h;
            if (!head.TryGetValue(key, out h)) h = -1;
            linkNext[linkCount] = h;
            linkWord[linkCount] = id;
            head[key] = linkCount;
            linkCount++;
        }

        /// <summary>FNV-1a of w with the character at skip removed (-1 = none).</summary>
        static int Hash(string w, int skip)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < w.Length; i++)
                {
                    if (i == skip) continue;
                    h ^= w[i];
                    h *= 16777619;
                }
                h ^= (uint)(w.Length - (skip >= 0 ? 1 : 0));
                return (int)h;
            }
        }

        public void LoadNext(Stream s)
        {
            string cur = null;
            var buf = new List<Successor>();
            int total = 0;
            foreach (string line in ReadLines(s))
            {
                string[] p = line.Split('\t');
                if (p.Length != 3) continue;
                if (p[0] != cur)
                {
                    if (cur != null) { next[cur] = buf.ToArray(); nextTotal[cur] = total; }
                    cur = p[0]; buf.Clear(); total = 0;
                }
                int c = int.Parse(p[2]);
                buf.Add(new Successor(p[1], c));
                total += c;
            }
            if (cur != null) { next[cur] = buf.ToArray(); nextTotal[cur] = total; }
        }

        public bool Contains(string w) { return counts.ContainsKey(w); }

        public int Count(string w)
        {
            int c;
            return counts.TryGetValue(w, out c) ? c : 0;
        }

        public int Rank(string w)
        {
            int r;
            return rank.TryGetValue(w, out r) ? r : int.MaxValue;
        }

        public Successor[] Next(string w, out int total)
        {
            Successor[] s;
            if (next.TryGetValue(w, out s)) { total = nextTotal[w]; return s; }
            total = 0;
            return new Successor[0];
        }

        /// <summary>Dictionary words within one symmetric delete of the typed word.</summary>
        public HashSet<string> Candidates(string typed)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            Collect(Hash(typed, -1), result);
            for (int i = 0; i < typed.Length; i++)
            {
                if (i > 0 && typed[i] == typed[i - 1]) continue;
                Collect(Hash(typed, i), result);
            }
            return result;
        }

        void Collect(int key, HashSet<string> into)
        {
            int h;
            if (!head.TryGetValue(key, out h)) return;
            while (h >= 0)
            {
                into.Add(targets[linkWord[h]]);
                h = linkNext[h];
            }
        }

        /// <summary>Most frequent words starting with prefix (excluding the prefix itself).</summary>
        public List<KeyValuePair<string, int>> Complete(string prefix, int max, int scanLimit)
        {
            var res = new List<KeyValuePair<string, int>>();
            if (string.IsNullOrEmpty(prefix) || sorted == null) return res;
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (string.CompareOrdinal(sorted[mid], prefix) < 0) lo = mid + 1; else hi = mid;
            }
            int scanned = 0;
            for (int i = lo; i < sorted.Length && scanned < scanLimit; i++, scanned++)
            {
                string w = sorted[i];
                if (!w.StartsWith(prefix, StringComparison.Ordinal)) break;
                if (w.Length == prefix.Length) continue;
                int c = sortedCounts[i];
                if (res.Count < max) { res.Add(new KeyValuePair<string, int>(w, c)); SortDesc(res); }
                else if (c > res[res.Count - 1].Value) { res[res.Count - 1] = new KeyValuePair<string, int>(w, c); SortDesc(res); }
            }
            return res;
        }

        static void SortDesc(List<KeyValuePair<string, int>> l)
        {
            for (int i = l.Count - 1; i > 0 && l[i].Value > l[i - 1].Value; i--)
            {
                var t = l[i]; l[i] = l[i - 1]; l[i - 1] = t;
            }
        }
    }
}
