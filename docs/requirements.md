# Requirements

## 1. Goal

A stable, low-latency pen experience with a Samsung tablet and its S Pen, **as if you had bought a new graphics tablet**:

- Windows sees **a real pen**; drawing apps receive pressure.
- The PC app behaves like a tablet driver panel: area mapping, pressure curve, button assignment.
- The tablet lies in front of you and you draw on it (the tablet is the drawing surface, like a pen tablet without a screen).

**The goal is pen input.** Mirroring the PC screen to the tablet is not part of Divit.

## 2. Target environment

| Topic | Decision |
|---|---|
| Tablet | Samsung Galaxy Tab with an S Pen; reference device **Galaxy Tab S9 FE+**. |
| Pen | The bundled S Pen (EMR, no battery, no Bluetooth), no eraser tip. Measured: ~4096 pressure levels, tilt, side button while hovering and in contact, ~366 Hz sampling. |
| PC | **Windows 11.** |
| Connection | **USB (primary)** and **Wi-Fi (secondary)**. |
| Cost | Zero: no paid certificates, services or licenses. |
| App type | Native apps (Android + Windows). |

## 3. Scope

| In scope | Out of scope |
|---|---|
| Pressure-sensitive pen input (Windows Ink) | A kernel-mode driver |
| Hover, side button, tilt | Windows 10 |
| Tablet area ↔ monitor mapping | Non-Samsung tablets, iPad |
| Pressure curve | Web app |
| WinTab compatibility layer | Screen mirroring |
| USB + Wi-Fi | |

## 4. Functional requirements

**Pen input (IN)**

| ID | Requirement |
|---|---|
| IN-01 | The S Pen position moves the pen cursor in Windows. |
| IN-02 | **Pressure** reaches the app; the tablet's full pressure resolution is preserved. |
| IN-03 | Hovering moves the cursor without drawing. |
| IN-04 | Finger touches are ignored; a resting palm draws nothing. |
| IN-05 | The pen area maps to a chosen monitor; the aspect ratio is kept. |
| IN-06 | The side button reaches Windows as the pen barrel button. |
| IN-07 | The side button is assignable: pen button / right click / eraser / keyboard shortcut / off. |
| IN-08 | Adjustable pressure curve (lower/upper threshold + curve). |
| IN-09 | Only part of the tablet can be used (active area). |
| IN-10 | Tilt is passed on. |
| IN-11 | Apps that use WinTab also receive pressure (without switching them to Windows Ink). |

**Tablet app (TB)**

| ID | Requirement |
|---|---|
| TB-01 | Full-screen drawing surface; the status bar and Samsung pen shortcuts do not interfere. |
| TB-02 | The screen stays on while drawing. Divit runs only while it is on screen: a few seconds after it leaves the screen (Home, another app, swiped away) the connection closes and the PC releases the pen; it reconnects by itself when Divit is opened again. |
| TB-03 | The active area and connection status are visible; brightness can be dimmed. |

**Host app (HS)**

| ID | Requirement |
|---|---|
| HS-01 | Runs in the system tray with connection status and latency. |
| HS-02 | Settings panel: monitor, area, pressure curve, button (like a tablet driver panel). |
| HS-03 | Optional start with Windows. |

**Connection (CN) and security (SEC)**

| ID | Requirement |
|---|---|
| CN-01 | USB connection. |
| CN-02 | Wi-Fi connection (same local network). |
| CN-03 | If the connection drops, the pen counts as lifted (no stuck click) and reconnection is automatic. |
| SEC-01 | In USB-only mode the host listens on `127.0.0.1` only. |
| SEC-02 | Wi-Fi needs a pairing code; only paired tablets are accepted. |
| SEC-04 | USB connections are authenticated too: other apps on the tablet cannot inject input through the `adb reverse` tunnel. |
| SEC-05 | No key ever goes on the wire; the tablet verifies the PC first and tells a fake PC nothing. |

## 5. Non-functional requirements

Priority: **latency and stability.**

| ID | Requirement | Target |
|---|---|---|
| NFR-LAT-1 | Tablet event → Windows injection (USB) | p95 ≤ **8 ms** |
| NFR-LAT-2 | Tablet event → Windows injection (Wi-Fi 5/6 GHz) | p95 ≤ **20 ms** |
| NFR-JIT | Latency jitter (USB) | p99 − p50 ≤ **5 ms** |
| NFR-STB-1 | Disconnects in a 2-hour drawing session (USB) | **0** |
| NFR-STB-2 | Reconnection after a drop | ≤ 2 s, no user action |
| NFR-ACC-1 | Pressure resolution | Raw value carried losslessly; 0–1024 to Windows Ink, 0–4095 to WinTab |
| NFR-ACC-2 | Sample loss / reordering | None (verified by sequence numbers) |
| NFR-COST | Development and distribution cost | **0** |
| NFR-INST | Pen mode needs no administrator rights | Required (except the optional WinTab install) |
| NFR-RES | Host resource use | CPU < 2 %, RAM < 100 MB |

Measurements: [research.md](research.md).

> **Stabilization note:** "stability" means a stable connection and data. **Stroke smoothing (a stabilizer)** always adds latency; drawing apps (Krita, CSP) already provide it. Divit applies **no smoothing and no buffering** by default and forwards raw data as it arrives.

## 6. Quality criteria

1. Connects over USB and Wi-Fi; pulling and re-plugging the cable leaves no stuck pen and the connection returns by itself.
2. Pressure changes the stroke width in Krita and Clip Studio Paint, in Windows Ink mode and with WinTab.
3. Hover moves the cursor; a resting palm draws nothing.
4. The latency targets above hold.
5. No disconnect in a 2-hour USB session.

## 7. Compatibility matrix

Test reports are welcome (see [CONTRIBUTING.md](../CONTRIBUTING.md)).

| App | Version | Mode | Pressure | Hover | Side button | Notes |
|---|---|---|---|---|---|---|
| Krita | | Windows Ink | | | | |
| Krita | | WinTab | | | | |
| Clip Studio Paint | | Tablet PC (Windows Ink) | | | | |
| Clip Studio Paint | | WinTab | | | | |
| Photoshop | | Windows Ink | | | | |
| Blender | | Windows Ink | | | | |
| Paint Tool SAI | | | | | | |
| OneNote / Windows Ink apps | | — | | | | Reference |
