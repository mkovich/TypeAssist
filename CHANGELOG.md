# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project uses [Semantic Versioning](https://semver.org/).

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

[1.4.0]: https://github.com/mkovich/TypeAssist/releases/tag/v1.4.0
[1.3.0]: https://github.com/mkovich/TypeAssist/releases/tag/v1.3.0
