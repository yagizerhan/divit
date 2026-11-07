# Building release artifacts

## Android: signed release APK

On Android the signing key is created by the publisher; no certificate authority is involved. The key is the app's permanent identity: every update must be signed with the same key.

Create a key once:

```bash
keytool -genkeypair -v -keystore divit-release.jks -alias divit -keyalg RSA -keysize 4096 -validity 10000
```

Copy `tablet-android/keystore.properties.example` to `tablet-android/keystore.properties` and fill it in. That file and `*.jks` are ignored by git and must never be committed. Keep the key and its password backed up outside the repository: a lost key means the published app can no longer be updated.

Build:

```bash
cd tablet-android && ./gradlew assembleRelease
```

The signed APK is `app/build/outputs/apk/release/app-release.apk`. Without `keystore.properties` the build produces `app-release-unsigned.apk`.

Forks must use their own key and their own application ID (see [TRADEMARKS.md](../TRADEMARKS.md)).

## Windows: installer

`installer/divit.iss` (Inno Setup 6) builds `DivitSetup-<version>.exe`: English/Turkish, per-machine install to Program Files, Start menu entry, optional start with Windows, optional WinTab support (never replaces another tablet driver's DLL), clean uninstall. Build steps: [installer/README.md](../installer/README.md).

## Checksums

Publish SHA-256 checksums next to every artifact:

```bash
sha256sum DivitSetup-*.exe app-release.apk > SHA256SUMS.txt
```

## Store assets

- Icon: [brand/divit-icon.svg](brand/divit-icon.svg) (source), [brand/divit-icon-512.png](brand/divit-icon-512.png) (512 × 512).
- Privacy policy: [privacy.md](privacy.md).
- "Samsung", "Galaxy" and "S Pen" may be used only descriptively ("works with the Samsung S Pen"), without Samsung logos.
