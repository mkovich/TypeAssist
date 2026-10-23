# Contributing

Thanks for helping! Bug reports, wrong corrections and missing words are all useful.

## Reporting a wrong correction
Open an issue with the word you typed, what it became, what you expected,
and the app you were typing in. If you can, attach the log (tray menu → Show log).

## Building
You need Windows 10 or 11. .NET Framework 4.8 is built in, so there is nothing to install.

```bat
build.bat              :: builds TypeAssist.exe
tests\run_tests.bat    :: builds and runs the tests
```

## Pull requests
- Keep the code compatible with the .NET Framework 4.x `csc.exe` (C# 5).
- Run `tests\run_tests.bat` and make sure it ends with `ALL PASSED`.
- Add a test in `tests/` for any change to correction or tracking behaviour.
- To rebuild the language data, see the header of `tools/build_data.py`.
