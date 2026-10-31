# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [Semantic Versioning](https://semver.org/).

## [1.6.4] - 2026-10-31

### Fixed
- **Words merging or a space disappearing around words with a half-space.** Apps such as Chrome, Edge and
  Telegram delete a half-space (ZWNJ) together with the letter before it, so fixing a word that contained one
  deleted one character too many: the space before the word vanished and words merged
  (`وجود پایه‌گذران` → `وجودپایه‌گذاران`). TypeAssist now never deletes and retypes text that contains a
  half-space or a diacritic, and Backspace right after a half-space fix just works normally.
- **A half-space turned into a real space.** When Windows reports a different keyboard than the one typing,
  Shift+Space (the Persian half-space) was read as a plain space: the half-word before it could be "fixed" and a
  real space written in place of the half-space. Shift+Space no longer ends a word, and the word after it is
  never touched.
- **Persian words "corrected" into English.** With a misreported keyboard, letters on punctuation keys
  (`ج` on `[`, `و` on `,`, `گ` on `'`) looked like punctuation, and the rest of the word was autocorrected into an
  English word (`خانوادگی` → `خانوhe'd`). A fix is now skipped whenever another keyboard reads the keys as one
  word, or reads the word as any known word.

### Testing
- New simulation that types 2,000 sentences per run in bursts, with half-spaces, apps that delete half-spaces
  with the letter before them, misreported keyboards, and an extra Arabic-script keyboard. 14,000 sentences:
  no word lost or merged, no correct word replaced with the other language.

## [1.6.3] - 2026-10-30

### Fixed
- **Words disappearing or getting jumbled when typing fast.** When TypeAssist fixed a word, letters typed right
  after it (before the fix was finished) landed in the middle of the fix, so its Backspaces deleted the wrong
  letters: words vanished, merged or were left half-typed. Keys typed while a fix is in progress are now held
  back and passed on right after it, in their original order.
- **Correct words replaced with nonsense from the other language.** When Windows doesn't report the keyboard
  language correctly, a correct word could be read in the wrong language and "corrected". A typo fix is now
  skipped whenever the same keys spell a common word on another installed keyboard.
- In apps that ignore keyboard switching, TypeAssist now goes back to the keyboard Windows reports instead of
  keeping its own guess.
- Logging no longer writes to disk from the keyboard hook, so the hook stays fast.

## [1.6.2] - 2026-10-29

### Fixed
- `بعد` could still be turned into `fun` (and the keyboard switched) when Windows reported a different
  Arabic-script layout than the one actually typing (for example Arabic or Dari alongside Persian), because
  `بعد` read with that layout is not a word. Keys are now never switched if they spell a word in **any**
  installed layout, so words that mean something in both languages always stay as typed.

## [1.6.1] - 2026-10-29

### Fixed
- Words that mean something in both languages were sometimes switched to the other language (`بعد` became
  `fun`). A word is now only retyped when what was typed is clear gibberish: no rare words, no likely typos.
- Undoing a keyboard-language fix with Backspace now blocks that switch for good, in both directions.
- In apps that don't follow keyboard switching, or that deliver finished characters (VK_PACKET) instead of keys,
  TypeAssist no longer retypes words, because it can't be sure what is on screen there.

## [1.6.0] - 2026-10-29

### Added
- **Earlier words typed on the wrong keyboard are fixed too.** If a word was too rare to switch on by itself,
  it's retyped as soon as the next word shows the keyboard was wrong, whether TypeAssist switched the keyboard
  or you did (`sghl o,fd` → `سلام خوبی`). Up to four earlier words, only if nothing else was typed in between.
  Backspace right after undoes everything.
- **Learns your two-word habits.** Besides what you type after each word, TypeAssist now learns what you type
  after each pair of words and ranks it first next time (one use is enough).
- **Remembers how you capitalise names** and suggests them that way.

### Changed
- A misspelt reading no longer blocks the keyboard switch when the other reading is a common word of four or
  more letters (هدفخ → into).
- Learned word pairs seen only once are pruned when the data grows large.

## [1.5.0] - 2026-10-29

### Added
- **Wrong keyboard language fix.** A word typed with the keyboard on the wrong language (`sghl` → `سلام`,
  `اثممخ` → `hello`) is retyped in the right language when you press Space, and the keyboard is switched for
  you, from the first word. Backspace right after undoes both. On by default; tray menu →
  **Fix wrong keyboard language**, or `auto_switch_layout` in `settings.ini`.
  In a simulation over 73,000 correctly typed English and Persian words it switched 2 by mistake, and it fixed
  92% of common words typed on the wrong keyboard.

### Changed
- The keyboard-layout cross-check now needs a common word as evidence before it changes how keys are read,
  so rare words and names can no longer flip it.

## [1.4.0] - 2026-10-27

### Fixed
- **Start with Windows** did not reliably start the app at sign-in. TypeAssist now copies itself to
  `%LocalAppData%\Programs\TypeAssist` and registers that copy (so it keeps working after the download folder is
  moved or deleted), removes the downloaded-from-internet mark from its own exe, clears an earlier "disabled" state
  from Task Manager's Startup apps, and waits briefly at sign-in so the tray icon appears reliably.
- The startup entry is repaired automatically on each launch, and the installed copy is updated when a newer version runs.

### Added
- **About TypeAssist…** and **Developer: mkovich.com** in the tray menu; developer details in the exe's file properties.

## [1.3.0] - 2026-10-23

First public release.

### Features
- System-wide autocorrect for Persian and English in any Windows app.
- Word suggestion bar next to the caret (Tab, Alt+1..3, click).
- Undo a correction with an immediate Backspace; that word is then never corrected again.
- Persian ZWNJ (half-space) fixes for می‌/نمی‌ and ها/های.
- Learns your vocabulary and word order, locally only.
- Stays off in password fields, terminals, code editors, remote desktop, password managers and full-screen games.
- Per-app exclusions, `settings.ini`, Ctrl+Alt+F12 toggle, run-at-startup option.

[1.6.4]: https://github.com/mkovich/TypeAssist/releases/tag/v1.6.4
[1.6.3]: https://github.com/mkovich/TypeAssist/releases/tag/v1.6.3
[1.6.2]: https://github.com/mkovich/TypeAssist/releases/tag/v1.6.2
[1.6.1]: https://github.com/mkovich/TypeAssist/releases/tag/v1.6.1
[1.6.0]: https://github.com/mkovich/TypeAssist/releases/tag/v1.6.0
[1.5.0]: https://github.com/mkovich/TypeAssist/releases/tag/v1.5.0
[1.4.0]: https://github.com/mkovich/TypeAssist/releases/tag/v1.4.0
[1.3.0]: https://github.com/mkovich/TypeAssist/releases/tag/v1.3.0
