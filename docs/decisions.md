# Decisions

> Each decision: context → choice → reason. The detailed discussion history is kept outside the repository.

## K-01 · Pen first
The goal is a stable pen experience with the tablet and S Pen you already own, like a new graphics tablet. Screen mirroring is not the goal; any optional feature must add no dependency to the pen path. **Accepted.**

## K-02 · Native apps, written from scratch
Our own native Android and Windows apps, not a web app. Projects such as Weylus and OpenTabletDriver were looked at for ideas only; no code was taken, so their licenses do not apply. **Accepted.**

## K-03 · Platform: Windows 11 + Samsung
Windows 11 only (build 22000+) and Samsung tablets with an S Pen only; reference device Galaxy Tab S9 FE+. No Windows 10 test matrix, Windows 11 APIs can be used, and Samsung-specific behavior (Air Command) can be handled directly. **Accepted.**

## K-04 · Pen input path
| Option | Verdict |
|---|---|
| **A. Windows Synthetic Pointer API** (user mode, a Windows Ink pen) | **Accepted** — no driver or signature, Windows sees a pen (verified in S1) |
| **C. WinTab provider DLL** (user mode) | **Accepted** — real tablet compatibility for WinTab apps, no signature required |
| B. Bluetooth HID digitizer | Deferred — the pen is not used away from the tablet |
| D′. UMDF2 virtual HID driver | Deferred — driver package signing and installation complexity |
| D. Kernel-mode virtual HID driver | Rejected — needs a paid EV certificate and Microsoft signing |

A real Wacom driver offers both A and C; together they cover nearly all drawing apps.

## K-05 · Connection: USB first, Wi-Fi second, one TCP connection
USB through `adb reverse` + TCP (`TCP_NODELAY`); Wi-Fi uses the same code plus pairing. Cable is the most stable medium and both transports share one code path. Pen data is tiny, so TCP is enough (S4: zero drops, low jitter). Cost: "USB debugging" must be enabled once on the tablet. The host re-creates `adb reverse` on device events. **Accepted, verified in S4.**

## K-06 · WinTab layer structure
A C `wintab32.dll` (x64 + x86) plus a shared-memory bridge in the host; the cursor moves through Windows Ink, WinTab carries pressure (4096 levels) and packets. Installed into `System32` / `SysWOW64` (one administrator prompt, the only exception to NFR-INST). Another driver's DLL (Wacom etc.) is never overwritten. The Windows installer offers WinTab as a checkbox (on by default); the settings window can repair or remove it. **Accepted.** Details: [wintab.md](wintab.md).

## K-07 · Languages
Host: C# on the current LTS .NET, Windows APIs through P/Invoke, allocation-free hot path, WinForms for tray and settings (no extra dependency). Tablet: Kotlin, Android framework only. WinTab DLL: C, because it is loaded into other apps' processes and must not carry the .NET runtime. **Accepted.**

## K-08 · Screen mirroring
Not part of Divit. Any optional feature must stay independent of the pen path.

## K-09 · Stabilization / stroke smoothing
Two different "stabilizations": (1) connection and data stability — top priority; (2) stroke smoothing — always adds latency, and drawing apps already provide it. Divit applies **no smoothing by default**. **Accepted.**

## K-10 · License and protection against misuse
**GPL-3.0-only** plus a name and logo policy ([TRADEMARKS.md](../TRADEMARKS.md)). Anyone may study, change and share the code, but derivatives must stay open under the same license, and modified builds must use a different name. "-only" rather than "-or-later" keeps control over future GPL versions. Contributions under the DCO (`Signed-off-by`). **Accepted.**

## K-11 · Third-party components
No third-party layer on the hot path: only OS APIs and our own code. Development-only tools: xunit (Apache-2.0), JUnit (EPL-1.0), org.json (tests), Python `cryptography` (test-vector generation), Inno Setup (installer build). `adb` (Android platform-tools) is used for the USB tunnel. **Accepted.**

## K-12 · Distribution
Only free distribution channels. Official builds are published on the project's GitHub Releases page with SHA-256 checksums; the APK may additionally be listed in free app stores. Build steps: [releasing.md](releasing.md). **Accepted.**

## K-13 · Name: Divit
The working name "sPen" was too close to Samsung's "S Pen". A *divit* is an old writing set combining a pen case and an inkwell — fitting for a product that brings a tablet and its S Pen together. Five letters, ASCII only, readable in Turkish and English. Android application ID: **`io.github.yagizerhan.divit`** (permanent). **Accepted.**

## K-14 · Pen capture and 90 Hz batching on the tablet
Android batched S Pen samples to the 90 Hz display refresh (S2). Decision: request unbuffered dispatch both when the view is attached and per event at the start of every gesture (renewed on every hover event), and always read historical samples as a safety net. Verified in S2b: one sample per event in contact, p95 2.9 ms. Default upper pressure threshold 0.70 because the hardest press peaked around 0.68. **Accepted.**

## K-15 · Authentication on every connection
The original design sent a pairing token in clear text and trusted every `127.0.0.1` connection as USB. Review found that any app on the tablet could inject input through the `adb reverse` tunnel, and that a spoofed PC on the LAN could steal the token. Decision: HMAC challenge-response on every connection (the PC proves itself first), a USB key delivered over adb to a `DUMP`-guarded receiver, ECDH pairing, and the tablet remembering the expected PC ID. Remaining known limit: an active man-in-the-middle at the moment of pairing. **Accepted** ([SPEC §3.2–3.3](../protocol/SPEC.md)).

## K-16 · Localization
English and Turkish. The tablet follows the system language (Android resources, `values/` + `values-tr/`). The Windows app follows the Windows display language by default and has a language choice in its settings. The installer offers both languages. **Accepted.**
