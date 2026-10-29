<p align="center">
  <img src="assets/app.ico" width="72" alt="TypeAssist icon">
</p>

<h1 align="center">TypeAssist</h1>

<p align="center">
  <b>Autocorrect and word prediction for Persian and English, in every Windows app.</b><br>
  100% offline · no installer · nothing leaves your PC
</p>

<p align="center">
  <a href="https://github.com/mkovich/TypeAssist/releases/latest"><img src="https://img.shields.io/github/v/release/mkovich/TypeAssist?label=download" alt="Latest release"></a>
  <a href="https://github.com/mkovich/TypeAssist/actions/workflows/ci.yml"><img src="https://github.com/mkovich/TypeAssist/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/mkovich/TypeAssist" alt="MIT license"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6" alt="Windows 10 | 11">
</p>

<p align="center"><a href="README.fa.md">فارسی</a></p>

---

TypeAssist sits in the system tray and fixes typos as you type, in your browser,
Word, Telegram, Notepad, anywhere. It also shows a small suggestion bar next to
the caret, so you can finish words with one key. It handles both Persian and
English and switches automatically with your keyboard layout.

```
وجدود ␣   →  وجود ␣
میخواهم ␣  →  می‌خواهم ␣      (adds the missing half-space / ZWNJ)
teh ␣     →  the ␣
```

## 🔒 Fully local and offline

- **No internet connection, ever.** The code contains no networking at all. Nothing you type is sent anywhere.
- **No full text is saved.** Only single words and word pairs with a count are kept, to personalise suggestions.
- **Your data stays in `%AppData%\TypeAssist`** on your own PC, and you can wipe it from the tray menu at any time.
- **Password fields are ignored.** Nothing is corrected or learned there.
- **No installer and no admin rights.** It's a single `.exe`.
- **Open source.** Read the code, or build the exe yourself with `build.bat`.

More detail in [SECURITY.md](SECURITY.md).

## Download and run

1. Download `TypeAssist-1.6.3.zip` from the [latest release](https://github.com/mkovich/TypeAssist/releases/latest) and extract it (for example to `C:\Tools\TypeAssist`).
2. Run `TypeAssist.exe`. It needs .NET Framework 4.8, which Windows 10 and 11 already include.
3. **Windows SmartScreen warning:** because the exe isn't digitally signed, Windows may show
   *"Windows protected your PC"*. Click **More info → Run anyway**. This is normal for
   small unsigned open-source apps, not a sign of a problem.
4. A green **Aa** icon appears next to the clock. It's grey for the first few seconds while the dictionary loads.
5. To start it with Windows: right-click the icon → **Start with Windows**. TypeAssist copies itself to
   `%LocalAppData%\Programs\TypeAssist` and registers that copy, so you can delete or move the downloaded folder.
   Running a newer version later updates that copy.

> Some antivirus products are suspicious of any app that reads the keyboard, because
> keyloggers do that too. TypeAssist only reads keys to correct them and has no way
> to send anything out. If in doubt, build it from source (see below).

## How to use

| Action | Key |
|---|---|
| Autocorrect | Happens after Space, Enter or punctuation (`. , ? !` and their Persian forms) |
| Undo a correction | Press **Backspace** right away. Your word comes back and is never corrected again |
| Accept the first (highlighted) suggestion | **Tab** |
| Accept suggestion 1 / 2 / 3 | **Alt+1 / Alt+2 / Alt+3**, or click it |
| Close the suggestion bar | **Esc** |
| Turn TypeAssist on/off | **Ctrl+Alt+F12**, or left-click the tray icon |

When you accept a suggestion, a space is added for you. If you then type `.` or `,`,
that extra space is removed.

### Wrong keyboard language

Typed Persian while the keyboard was on English (`sghl` instead of `سلام`), or English on the
Persian keyboard (`اثممخ` instead of `hello`)? When you press Space, TypeAssist retypes the word
in the right language and switches the keyboard for you, from the very first word. If a word
was too rare to be sure of on its own, it's fixed as soon as the next word gives it away, whether
TypeAssist switches the keyboard or you do (`sghl o,fd` → `سلام خوبی`). Press
**Backspace** right away to undo: the word and the keyboard go back, and that word is never
switched again. It only acts when what you typed is clear gibberish and the other reading is a
common word. Anything that means something in both languages (`بعد` / `fun`), names, slang and
short words like `ok` are left alone. In apps that ignore keyboard switching, or that deliver
finished characters instead of keys, it stops retyping altogether. Turn it off from the tray menu →
**Fix wrong keyboard language**.

## Why it doesn't get in the way

- It only corrects when it's nearly certain. If two words are likely (e.g. «ضرف»: ظرف or صرف?) it only suggests.
- Finglish (salam, chetori, merci…), capitalised names mid-sentence, ALL-CAPS words (NASA), words with digits, and parts of URLs, emails and file paths are left alone.
- Valid inflected forms (کتابش، می‌ترکند، نخریدن, plurals, -ed, -ing…) aren't treated as typos.
- The first word after a click is corrected only if it has at least 4 letters and the fix is obvious, since TypeAssist hasn't seen the letters before it.
- It's switched off in terminals, code editors (VS Code, Visual Studio, JetBrains, Notepad++…), remote desktop, password managers and full-screen games.
- You can exclude any other app from the tray menu → **Pause in this app** (excludes the app you last typed in; click again to undo).

## Learning

TypeAssist learns from what you type, so suggestions get more personal over time:

- **Your vocabulary.** An unknown word you type 3 times is never corrected again.
- **What you type after each word, and after each pair of words.** After you've typed
  "see you at the Hormuz office" once, typing "see you at the" offers **Hormuz** first.
  Your own habits outrank the general word lists.
- **How you write names.** Words you write with a capital (Hormuz, Dubai) are suggested that way.

Everything stays on your PC in `%AppData%\TypeAssist`. Rare pairs are pruned automatically so
the files stay small.
You can turn learning off or clear everything it has learned from the tray menu.

## Settings

Tray menu → **Open settings folder**, edit `settings.ini`, then **Reload settings**.

| Key | Meaning |
|---|---|
| `autocorrect`, `suggestions`, `learn` | `yes` / `no` |
| `zwnj_fix` | Add the half-space in می‌/نمی‌ and ها/های |
| `accept_with_tab` | Set to `no` if you need Tab for something else (Alt+1..3 still work) |
| `smart_space` | Add a space after accepting a suggestion |
| `auto_switch_layout` | Fix words typed with the wrong keyboard language and switch the keyboard |
| `popup_delay_ms` | Delay before the suggestion bar appears (default 60) |
| `excluded_apps` | Process names without `.exe`, comma-separated |

## Known limitations

- Apps run as administrator aren't covered unless TypeAssist also runs as administrator.
- Some apps (certain Electron and Java apps) don't report the caret position, so the bar appears next to the mouse pointer instead.
- Persian suggestions start out based mostly on formal text (Wikipedia) and get better as you type.

## Build from source

Nothing to install. The build uses the C# compiler that ships with Windows.

```bat
build.bat              :: produces TypeAssist.exe next to it
tests\run_tests.bat    :: builds and runs the test suite
```

Rebuilding the language data is optional. See `tools/build_data.py`.

### Project layout

```
src/Engine/   correction and prediction engine (no Windows dependencies)
src/Win/      keyboard hook, tray app, suggestion popup, settings
data/         compressed word lists and word-pair frequencies (embedded into the exe)
tests/        engine and input-tracker tests
tools/        script that generates data/
```

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Code: [MIT](LICENSE) © 2026 MH.Kovich · [mkovich.com](https://mkovich.com).
Language data comes from wordfreq, codespell, NLTK and Persian-NER under their own licenses. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
