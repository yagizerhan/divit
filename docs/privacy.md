# Divit — Privacy Policy

> Last updated: 6 November 2026. This is the policy linked from the app store listings.

**Divit does not collect, sell or share any personal data.** Divit has no servers and contains no ads, analytics or third-party SDKs.

## What is transmitted, and to whom

The tablet app talks only to the Divit app on **your own computer** that you selected and paired, over a USB cable or the same local (Wi-Fi) network. Nothing is sent to the internet. Transmitted data:

- Pen data (position, pressure, tilt, button) — in real time; never stored.
- The device model (e.g. "samsung SM-X610") and a random device ID created at install — so your computer can recognize the tablet.
- The size of the drawing surface.

## What is stored on the device

Only on your tablet: the random device ID, pairing keys, the computer's name, ID and address, and your screen-brightness preference. They are removed when you uninstall the app and are excluded from backups.

On the computer, Divit stores its settings, its own ID and keys, and the IDs of paired tablets in `%APPDATA%\Divit`, and a local log file in `%LOCALAPPDATA%\Divit`. None of it leaves the computer.

## Local network discovery

"Computers on the network" sends a broadcast on your local network; only computers running Divit with Wi-Fi connections allowed reply (computer name, ID and port). This never leaves your network.

## Permissions (Android)

- **Internet** — the local connection to your computer only.
- **Wi-Fi state and wake lock** — a low-latency Wi-Fi lock while connected.

Divit has no background service: the connection is open only while the app is on screen.

## Security

Every connection is authenticated in both directions (the computer proves itself first), and Wi-Fi pairing uses a code shown on the computer (entered once per tablet). Pen data is not encrypted in this version; use Wi-Fi only on networks you trust.

## Children

Divit does not knowingly collect data from children — it collects none from anyone.

## Contact

Questions about this policy: open an issue in the project's GitHub repository.
