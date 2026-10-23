"""Build the language data files used by TypeAssist.

Outputs (gzip, UTF-8, tab separated):
  words_fa.tsv.gz / words_en.tsv.gz : word \t count   (sorted by count desc)
  next_fa.tsv.gz  / next_en.tsv.gz  : w1 \t w2 \t count (top successors per w1)

Usage: python3 -I build_data.py <persian_ner_dir> <nltk_zip_dir> <out_dir>
"""
import collections
import gzip
import io
import re
import sys
import zipfile

import wordfreq

PNER, NLTK, OUT = sys.argv[1], sys.argv[2], sys.argv[3]

ZWNJ = "‌"
FA_LETTERS = "آابپتثجچحخدذرزژسشصضطظعغفقکگلمنوهیئءأؤۀة"
FA_WORD = re.compile("^[" + FA_LETTERS + ZWNJ + "]+$")
EN_WORD = re.compile(r"^[a-z]+(?:'[a-z]+)?$")
DIACRITICS = re.compile("[ً-ْٰـ]")


def norm_fa(w):
    w = w.replace("ي", "ی").replace("ك", "ک").replace("ى", "ی")
    w = DIACRITICS.sub("", w)
    w = re.sub(ZWNJ + "+", ZWNJ, w).strip(ZWNJ)
    return w


def write_gz(path, rows):
    with gzip.open(path, "wt", encoding="utf-8", compresslevel=9) as f:
        for r in rows:
            f.write("\t".join(str(x) for x in r) + "\n")


def build_bigrams(sentences, valid, top_k=12, min_count=2):
    big = collections.Counter()
    for sent in sentences:
        prev = None
        for w in sent:
            if w is None:
                prev = None
                continue
            if prev is not None and prev in valid and w in valid:
                big[(prev, w)] += 1
            prev = w
    by_first = collections.defaultdict(list)
    for (a, b), c in big.items():
        if c >= min_count:
            by_first[a].append((c, b))
    rows = []
    for a, lst in by_first.items():
        lst.sort(reverse=True)
        for c, b in lst[:top_k]:
            rows.append((a, b, c))
    rows.sort(key=lambda r: (r[0], -r[2]))
    return rows


# ---------------- Persian ----------------
def fa_sentences():
    for i in range(1, 6):
        path = f"{PNER}/Persian-NER-part{i}.txt"
        sent = []
        with open(path, encoding="utf-8", errors="ignore") as f:
            for line in f:
                line = line.rstrip("\n")
                if not line:
                    if sent:
                        yield sent
                    sent = []
                    continue
                tok = line.split("\t")[0]
                tok = norm_fa(tok)
                sent.append(tok if FA_WORD.match(tok) else None)
        if sent:
            yield sent


print("persian corpus ...", flush=True)
fa_corpus = collections.Counter()
fa_sents = []
for s in fa_sentences():
    fa_sents.append(s)
    for w in s:
        if w:
            fa_corpus[w] += 1

fa_counts = collections.Counter()
for w, p in wordfreq.get_frequency_dict("fa").items():
    w = norm_fa(w)
    if FA_WORD.match(w) and len(w) >= 1:
        fa_counts[w] = max(fa_counts[w], int(p * 1e9))
# Long tail from the corpus: only words seen often enough to not be typos.
scale = 1e9 / max(1, sum(fa_corpus.values()))
for w, c in fa_corpus.items():
    if c >= 6 and w not in fa_counts:
        fa_counts[w] = max(1, int(c * scale * 0.5))
fa_rows = sorted(fa_counts.items(), key=lambda x: -x[1])[:180000]
fa_valid = {w for w, _ in fa_rows}
write_gz(f"{OUT}/words_fa.tsv.gz", fa_rows)
print("fa words", len(fa_rows), flush=True)
fa_big = build_bigrams(fa_sents, fa_valid, top_k=12, min_count=3)
write_gz(f"{OUT}/next_fa.tsv.gz", fa_big)
print("fa bigrams", len(fa_big), flush=True)
del fa_sents

# ---------------- English ----------------
print("english ...", flush=True)
en_counts = collections.Counter()
for w, p in wordfreq.get_frequency_dict("en").items():
    if EN_WORD.match(w):
        en_counts[w] = int(p * 1e9)
en_rows = sorted(en_counts.items(), key=lambda x: -x[1])[:150000]
en_valid = {w for w, _ in en_rows}
write_gz(f"{OUT}/words_en.tsv.gz", en_rows)
print("en words", len(en_rows), flush=True)

TOKEN = re.compile(r"[A-Za-z]+(?:'[A-Za-z]+)?|[.!?;:,()\"]")


def en_sentences():
    for name in ["brown", "gutenberg", "reuters", "webtext", "nps_chat"]:
        z = zipfile.ZipFile(f"{NLTK}/{name}.zip")
        for info in z.infolist():
            if info.is_dir():
                continue
            fn = info.filename.lower()
            if name == "brown":
                if not re.search(r"/c[a-r]\d\d$", fn):
                    continue
                raw = z.read(info).decode("latin-1")
                raw = re.sub(r"/[^\s]+", "", raw)  # strip POS tags
            elif name == "nps_chat":
                if not fn.endswith(".xml"):
                    continue
                raw = z.read(info).decode("latin-1")
                raw = " . ".join(re.findall(r'<Post [^>]*>(.*?)</Post>', raw))
            elif name == "reuters":
                if "/training/" not in fn and "/test/" not in fn:
                    continue
                raw = z.read(info).decode("latin-1")
            else:
                if not fn.endswith(".txt") or "readme" in fn:
                    continue
                raw = z.read(info).decode("latin-1")
            sent = []
            for t in TOKEN.findall(raw):
                if t in ".!?;:,()\"":
                    if sent:
                        yield sent
                    sent = []
                    continue
                sent.append(t.lower())
            if sent:
                yield sent


en_big = build_bigrams(en_sentences(), en_valid, top_k=12, min_count=2)
write_gz(f"{OUT}/next_en.tsv.gz", en_big)
print("en bigrams", len(en_big), flush=True)

# ---------------- English misspellings (codespell main dictionary) ----------------
# Usage of this section: set CODESPELL env var to codespell's dictionary.txt
import os
cs = os.environ.get("CODESPELL")
if cs:
    rows = []
    for line in open(cs, encoding="utf-8"):
        line = line.strip()
        if "->" not in line:
            continue
        bad, good = line.split("->", 1)
        good = good.strip().rstrip(",")
        if "," in good or " " in good:
            continue            # ambiguous or multi-word fix: leave to the user
        if not EN_WORD.match(bad) or not EN_WORD.match(good) or len(bad) < 3:
            continue
        if en_counts.get(good, 0) < 3000 or en_counts.get(bad, 0) * 30 >= en_counts.get(good, 0):
            continue
        rows.append((bad, good))
    extra = {"dont": "don't", "doesnt": "doesn't", "didnt": "didn't", "isnt": "isn't", "wasnt": "wasn't",
             "arent": "aren't", "werent": "weren't", "havent": "haven't", "hasnt": "hasn't",
             "couldnt": "couldn't", "wouldnt": "wouldn't", "shouldnt": "shouldn't", "im": "I'm",
             "ive": "I've", "youre": "you're", "theyre": "they're", "thats": "that's", "whats": "what's",
             "doesnt": "doesn't", "wouldve": "would've", "couldve": "could've", "shouldve": "should've"}
    have = {b for b, _ in rows}
    for b, g in extra.items():
        if b not in have:
            rows.append((b, g))
    rows.sort()
    write_gz(f"{OUT}/fix_en.tsv.gz", rows)
    print("en fixes", len(rows), flush=True)
