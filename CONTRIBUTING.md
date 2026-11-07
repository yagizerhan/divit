# Contributing to Divit

Thanks for your interest! Bug reports, test results with drawing apps and pull requests are welcome.

## Reporting a problem

Open an issue with:
- tablet model and Android version, Windows version, Divit versions (PC and tablet);
- USB or Wi-Fi;
- the drawing app and its tablet setting (Windows Ink / WinTab);
- the PC log (tray menu → *Open log file*, `%LOCALAPPDATA%\Divit\host.log`). For a detailed frame trace, start Divit with the environment variable `DIVIT_TRACE=1`.

**Security issues:** please do not open a public issue; use GitHub's private vulnerability reporting for this repository.

## Pull requests

1. Keep the hot path rules in [docs/architecture.md §3.1](docs/architecture.md): no allocation, no locks, no queues between receiving a pen sample and injecting it.
2. Run the tests: `dotnet test host-windows/Divit.sln` and `./gradlew testDebugUnitTest` in `tablet-android/`.
3. Protocol changes: update [protocol/SPEC.md](protocol/SPEC.md) and `protocol/test-vectors/generate.py`, regenerate `vectors.json`, and keep both the C# and Kotlin vector tests passing.
4. User-visible text: add it in **English and Turkish** — `host-windows/src/Divit.Host/Strings.cs` for Windows, `tablet-android/tools/strings.py` for Android (then run `python -I tools/strings.py`).
5. Code, comments and documentation are in English.
6. Every source file starts with:
   ```
   // SPDX-License-Identifier: GPL-3.0-only
   // Copyright (C) 2026 Divit contributors
   ```

## Developer Certificate of Origin

Contributions are accepted under the [Developer Certificate of Origin](https://developercertificate.org/). Sign off every commit:

```bash
git commit -s -m "Describe the change"
```

This adds `Signed-off-by: Your Name <you@example.com>`, certifying that you wrote the change or have the right to submit it under GPL-3.0-only. You keep the copyright of your contribution.
