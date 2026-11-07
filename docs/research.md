# Research: validation experiments

Before writing the product, four small throw-away experiments ("spikes") answered the questions the design depended on. Their code is not kept in the repository; the measurements and conclusions are below. Test devices: Galaxy Tab S9 FE+ (SM-X610, Android 16), Windows 11 25H2 PC.

## S1 — Windows Ink pen injection

**Question:** can a user-mode app, without a driver, create a pressure-sensitive pen with the Synthetic Pointer API that drawing apps accept?

**Result: yes.** A self-test window received the synthetic pen's messages and compared them with what was sent.

| Measurement | Result |
|---|---|
| Injection calls | 258 calls, 0 failures |
| Type Windows reports | **PEN** (all messages) |
| Message order | ENTER → DOWN → UPDATE… → UP → LEAVE |
| Pressure | 1…1024, **difference 0** |
| Position error | 0 px |
| Injection → window message | p50 0.18 ms · p95 0.25 ms |
| Call duration | p50 ~25 µs |

**Findings**
1. No administrator rights needed; no driver, signature or certificate. Windows reports the synthetic device as a real pen (`PT_PEN`).
2. Pressure arrives losslessly (all 1024 levels).
3. Windows' share of the latency is negligible (~0.2 ms).
4. **Coordinate trap:** `ptPixelLocation` is **not** screen coordinates but relative to the **top-left corner of the virtual desktop**: `(screenX − VirtualScreen.Left, screenY − VirtualScreen.Top)`. With a monitor left of or above the primary one, the pen otherwise lands on the wrong screen. `ptHimetricLocation` is not needed.
5. Verified flag transitions: hover `INRANGE|UPDATE`, touch `INRANGE|INCONTACT|DOWN`, move `INRANGE|INCONTACT|UPDATE`, lift `INRANGE|UP`, out of range `UPDATE`. The product uses the "full" set closest to real pens (`PRIMARY|CONFIDENCE`, plus `FIRSTBUTTON` in contact).

## S2 — S Pen data on the Galaxy Tab S9 FE+

**Question:** what data does the S Pen deliver, at what resolution and rate? Do Samsung's pen shortcuts get in the way?

Input device `sec_e-pen`. Pressure 0…1, tilt 0…π/2, orientation −π…π, hover distance 0…255.

| Measurement | Unbuffered | Buffered |
|---|---|---|
| Pressure levels | **~4096** (smallest step 1/4096) | — |
| Contact sample rate | **~366 Hz** (2.73 ms; p99 2.96 ms) | ~368 Hz |
| Hover sample rate | ~359 Hz | ~360 Hz |
| Dispatch rate | ~90 Hz | ~90 Hz |
| Samples per dispatch | 5 (contact) / 4 (hover) | 5 / 4 |
| Delivery latency of the newest sample (contact) p50 / p95 / p99 | 2.7 / 3.5 / 5.4 ms | 2.9 / 3.5 / 6.0 ms |
| Tilt | **yes**, 1° … 58° seen | — |
| Side button | **yes**, while hovering and in contact | — |
| Air Command / focus loss | **none** in this test | — |
| Eraser tip | none (as expected) | — |
| Fingers | reported separately, ignored | — |

**Findings**
1. **~4096 pressure levels.** Windows Ink carries at most 1024 (a 4:1 quantization, not noticeable in practice); WinTab carries all 4096 — one more reason for the WinTab layer. The protocol sends pressure as raw `f32`. **Note:** even the hardest press peaked at about 0.68, so the default upper threshold of the pressure curve is 0.70; otherwise a third of the range Windows receives would never be used.
2. The pen samples **~366 Hz, very regularly**. Payload: 366 × 32 B ≈ **12 KB/s**.
3. Tilt and side button are usable. `ACTION_BUTTON_PRESS` never arrives; the button must be read from `buttonState`.
4. In this first run Android batched samples to the 90 Hz display refresh even in unbuffered mode: the newest sample arrived after ~2.7 ms but the oldest in each batch waited ~11 ms more (mean sample age ≈ 8 ms). See S2b.
5. Air Command did not interfere in this test (it did later, with the button pressed while hovering; fixed with activity meta-data — see the architecture).

## S2b — event-based unbuffered dispatch

`requestUnbufferedDispatch` was moved to after the view is attached to the window, and additionally requested per event at the start of each gesture (`ACTION_DOWN` / `ACTION_HOVER_ENTER`).

| Measurement (unbuffered) | S2 | S2b |
|---|---|---|
| Contact dispatch rate | ~90 Hz | **~382 Hz** |
| Contact samples per dispatch p50 / p95 | 5 / 5 | **1 / 2** |
| Contact delivery latency p50 / p95 / p99 | 2.7 / 3.5 / 5.4 ms | **0.86 / 2.9 / 3.7 ms** |
| Hover dispatch rate | ~90 Hz | ~90 Hz |

**Result: batching is gone in contact.** In-tablet latency is ~3 ms p95. Hover stayed batched at 90 Hz with a request only at `HOVER_ENTER`; the product renews the request on every hover event, which removed hover batching too (measured in the product: 0 % batched hover samples, down from ~75 %).

## S4 — USB and Wi-Fi link latency

**Question:** latency, jitter and stability of `adb reverse` over USB and of Wi-Fi, over 30 minutes, with pen-rate traffic (~366 Hz × 32 B).

| Measurement | USB | Wi-Fi + low-latency lock | Wi-Fi without lock |
|---|---|---|---|
| Duration / frames | 31.9 min / 701 392 | 30.6 min / 675 518 | 20.2 min / 442 766 |
| RTT p50 / p95 / p99 / p99.9 | **3.30 / 3.65 / 4.02 / 5.00 ms** | **1.98 / 2.55 / 3.25 / 4.82 ms** | 1.99 / 2.60 / 3.49 / 5.70 ms |
| One-way jitter p50 / p99 | **0.68 / 1.42 ms** | — ¹ | — ¹ |
| Largest gap / gaps > 50 ms | 22 ms / 0 | 29 ms / 0 | 34 ms / 0 |
| Disconnects | **0** | **0** | **0** |

¹ Invalid in the first runs because of a clock bug in the test program; bounded above by RTT p99 − RTT min ≈ 2 ms.

Network: PC on Ethernet, tablet on Wi-Fi, same router, at night (idle network).

**Findings**
1. **Both links are stable:** zero drops and zero loss in 30 minutes each. **TCP is enough**; no need for UDP.
2. **Wi-Fi was faster than USB** (RTT p50 2.0 vs 3.3 ms): `adb reverse` traffic passes through adb processes on both ends, adding ~1.3 ms. USB's advantage is stability and independence from the network, not latency. The Wi-Fi result should be repeated on a busy network.
3. **Jitter is very low** (USB one-way p99 − p50 = 0.7 ms).
4. **Reconnecting took 3–4 s** with periodic polling; the product therefore watches `adb track-devices` for device events.
5. The lock made no difference on an idle network; the product uses it anyway because it costs nothing.
6. Side finding: the home router once gave the PC and the tablet the same IP. The product shows the tablet's own IP and warns about a conflict.

## Measured in the product

| Measurement | USB | Wi-Fi |
|---|---|---|
| Latency (tablet sample → Windows injection) p50 / p95 / p99 | 2.4 / 3.5 / 5.2 ms | 2.7 / 7.3 / 12.4 ms |
| Sequence gaps | 0 (~92 000 samples) | 0 (~30 000 samples) |
| Hover batching | — | 0 % |
| Pull and re-plug the cable | Pen released, connection back by itself | — |
