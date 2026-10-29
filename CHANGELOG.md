# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [Semantic Versioning](https://semver.org/).

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

[1.6.0]: https://github.com/mkovich/TypeAssist/releases/tag/v1.6.0
[1.5.0]: https://github.com/mkovich/TypeAssist/releases/tag/v1.5.0
[1.4.0]: https://github.com/mkovich/TypeAssist/releases/tag/v1.4.0
[1.3.0]: https://github.com/mkovich/TypeAssist/releases/tag/v1.3.0
