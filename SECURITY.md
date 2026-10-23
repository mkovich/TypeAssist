# Security & privacy

## What TypeAssist does with your keystrokes

TypeAssist uses a low-level keyboard hook to see what you type in other apps.
A system-wide autocorrect can't work any other way, and that hook is also why
some antivirus products flag it. Here is what actually happens:

- **No network access.** The source has no networking code at all (no
  `System.Net`, no sockets, no HTTP). Nothing you type ever leaves your PC.
- **No full text is stored.** It saves only single words and word pairs with
  a count, to personalise suggestions.
- **Everything stays in `%AppData%\TypeAssist`** on your own PC
  (`user_words.tsv`, `user_next.tsv`, `never_correct.txt`, `settings.ini`).
  "Forget learned words…" in the tray menu deletes it, and you can turn learning off.
- **Password fields are ignored.** Nothing is corrected or learned there.
- **No installer and no admin rights.** The only registry write is the optional
  "Start with Windows" entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

If you'd rather not trust a binary, read the code and build it yourself with
`build.bat`. It uses the C# compiler that ships with Windows.

## Reporting a vulnerability

Please report security issues privately through
[GitHub Security Advisories](https://github.com/mkovich/TypeAssist/security/advisories/new),
not in a public issue.
