// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// User-interface strings: English (default) and Turkish. L.T("key") or L.T("key", args) → composite format.
// Language: Settings.Language = "System" (Windows display language), "en" or "tr".
// The project uses InvariantGlobalization, so CultureInfo cannot tell the UI language; it is read from Windows.

using System.Runtime.InteropServices;

namespace Divit.Host;

public enum UiLanguage { En, Tr }

public static class L
{
    public static UiLanguage Current { get; private set; } = DetectSystem();

    /// <summary>"System", "en" or "tr" (anything else = System).</summary>
    public static void Use(string? preference) => Current = preference switch
    {
        "en" => UiLanguage.En,
        "tr" => UiLanguage.Tr,
        _ => DetectSystem(),
    };

    public static string T(string key) =>
        (Current == UiLanguage.Tr ? Tr : En).TryGetValue(key, out var s) ? s : En.TryGetValue(key, out var e) ? e : key;

    public static string T(string key, params object?[] args) => string.Format(T(key), args);

    [DllImport("kernel32.dll")] private static extern ushort GetUserDefaultUILanguage();

    private static UiLanguage DetectSystem()
    {
        try { return (GetUserDefaultUILanguage() & 0x3FF) == 0x1F ? UiLanguage.Tr : UiLanguage.En; }   // LANG_TURKISH
        catch { return UiLanguage.En; }
    }

    private static readonly Dictionary<string, string> En = new()
    {
        // Status
        ["status.connected"] = "Connected: {0} — {1}",
        ["status.latency"] = "Latency (tablet → Windows) p50 {0:F1} ms · p95 {1:F1} ms · ",
        ["status.samples"] = "{0}{1:N0} samples · {2}",
        ["status.pairing"] = "Waiting for pairing: {0}",
        ["status.pairingDetail"] = "Enter this code on the tablet: {0}",
        ["status.waiting"] = "Waiting for tablet",
        ["status.waitingWifi"] = "Connect the tablet over USB, or choose Wi-Fi on the tablet.",
        ["status.waitingUsb"] = "Connect the tablet over USB and open Divit on it.",
        ["tray.connected"] = "Connected: {0} ({1}){2}",
        ["tray.latency"] = " · latency p50 {0:F1} / p95 {1:F1} ms",
        ["tray.pairing"] = "Waiting for pairing — code: {0}",
        ["monitor.primary"] = "(primary)",

        // Tray menu
        ["menu.settings"] = "Settings…",
        ["menu.usb"] = "USB: {0}",
        ["menu.stats"] = "Samples: {0:N0} · sequence gaps {1} · injection failures {2}",
        ["menu.monitor"] = "Monitor",
        ["menu.keepAspect"] = "Keep aspect ratio",
        ["menu.pressure"] = "Pressure (press needed for full pressure)",
        ["menu.pressure.light"] = "Light — 55%",
        ["menu.pressure.medium"] = "Medium — 70% (recommended)",
        ["menu.pressure.firm"] = "Firm — 85%",
        ["menu.pressure.full"] = "Full — 100%",
        ["menu.wifi"] = "Allow Wi-Fi connections",
        ["menu.pcAddress"] = "   PC address: {0}",
        ["menu.pairingCode"] = "   Pairing code: {0}",
        ["menu.locked"] = "paused for 30 s (too many wrong codes)",
        ["menu.newCode"] = "   New code",
        ["menu.forget"] = "Forget paired tablets ({0})",
        ["menu.resetLatency"] = "Reset latency statistics",
        ["menu.openLog"] = "Open log file",
        ["menu.exit"] = "Exit",

        // Dialogs
        ["wifi.warn.title"] = "Divit — Wi-Fi",
        ["wifi.warn"] = "With Wi-Fi on, Divit accepts connections from the local network. On first connection the tablet asks for the 6-digit code shown here.\n\n" +
                        "Note: in this version pen data on Wi-Fi is not encrypted (connections are authenticated). Use it only on networks you trust.\n\n" +
                        "If Windows Firewall asks, allow \"Private networks\" only.",
        ["port.error"] = "Divit could not open port {0}:\n{1}\n\nAnother copy of Divit may be running.",

        // Settings window
        ["card.screen"] = "Screen",
        ["card.connection"] = "Wi-Fi and startup",
        ["card.pressure"] = "Pressure",
        ["card.button"] = "Side button",
        ["card.wintab"] = "WinTab (Krita, Clip Studio, SAI…)",
        ["screen.mapsTo"] = "The pen is mapped to this monitor:",
        ["screen.keepAspect"] = "Keep aspect ratio (drawings are not squashed or stretched)",
        ["screen.useAll"] = "Use all",
        ["area.full"] = "whole tablet",
        ["area.part"] = "{0:F0}% of the tablet",
        ["area.caption"] = "Blue area ({0}) → {1}×{2} monitor · drag to choose an area",
        ["pressure.help"] = "Drag the handles: the left one is the lower threshold (lighter pressure is ignored), the right one the upper threshold (pressure needed for full), " +
                            "the middle one the curve. The red dot moves live as you press with the pen.",
        ["pressure.soft"] = "Soft",
        ["pressure.linear"] = "Linear",
        ["pressure.hard"] = "Hard",
        ["pressure.values"] = "Lower threshold {0:F0}% · Upper threshold {1:F0}% · Curve {2:F2}",
        ["pressure.axisX"] = "Pressure on tablet →",
        ["pressure.axisY"] = "To Windows →",
        ["button.pen"] = "Pen button (the app decides)",
        ["button.right"] = "Right click",
        ["button.eraser"] = "Eraser",
        ["button.shortcut"] = "Keyboard shortcut",
        ["button.off"] = "Off",
        ["button.shortcutHint"] = "Click, then press keys",
        ["button.help"] = "Eraser: touching the screen with the side button held acts as the eraser (Krita / CSP support the eraser tip). " +
                          "Right click: press the button while the pen hovers. " +
                          "Shortcut: held while the button is (e.g. Space to pan).",
        ["screen.tapRing"] = "Show Windows' ring where the pen touches",

        ["wintab.help"] = "Some drawing apps read pressure through WinTab (4096 levels) instead of Windows Ink. Installing copies " +
                          "wintab32.dll into the Windows folder once (administrator). Another driver's file (e.g. Wacom) is never touched.",
        ["wintab.installed"] = "● Installed",
        ["wintab.installedUsers"] = "● Installed · used by {0} app(s) now",
        ["wintab.outdated"] = "● Installed (old version)",
        ["wintab.foreign"] = "Another driver's WinTab is installed",
        ["wintab.missing"] = "This package has no WinTab file",
        ["wintab.notInstalled"] = "Not installed",
        ["wintab.uninstall"] = "Uninstall",
        ["wintab.update"] = "Update",
        ["wintab.install"] = "Install WinTab",
        ["wintab.done"] = "WinTab installed. Restart any open drawing apps (Krita, Clip Studio…).",
        ["wintab.err.foreign"] = "Another tablet driver's WinTab file (e.g. Wacom) is present; Divit does not replace it.",
        ["wintab.err.inUse"] = "The file is in use: close apps that use WinTab (Krita, Clip Studio…) and try again.",
        ["wintab.err.other"] = "Setup failed (code {0}). Details: {1}",
        ["wintab.err.denied"] = "Administrator permission was not given.",
        ["conn.wifi"] = "Allow Wi-Fi connections",
        ["conn.addresses"] = "PC address for the tablet: {0}",
        ["conn.codeLabel"] = "Pairing code (enter it on the tablet; you can type your own 6 digits)",
        ["conn.newCode"] = "New code",
        ["conn.locked"] = "paused for 30 s after too many wrong codes",
        ["conn.noPaired"] = "No paired tablets",
        ["conn.paired"] = "Paired tablets: {0}",
        ["conn.forget"] = "Forget pairings",
        ["conn.autostart"] = "Start Divit when Windows starts",
        ["conn.theme"] = "Appearance:",
        ["theme.dark"] = "Dark",
        ["theme.light"] = "Light",
        ["theme.system"] = "System",
        ["conn.language"] = "Language:",
        ["lang.system"] = "System",

        // adb
        ["adb.starting"] = "starting",
        ["adb.missing"] = "adb not found — reinstall Divit with USB connection support",
        ["adb.notRunning"] = "adb is not running",
        ["adb.unauthorized"] = "waiting for \"Allow USB debugging\" on the tablet",
        ["adb.noDevice"] = "no tablet on USB",
        ["adb.ready"] = "USB ready ({0})",
    };

    private static readonly Dictionary<string, string> Tr = new()
    {
        ["status.connected"] = "Bağlı: {0} — {1}",
        ["status.latency"] = "Gecikme (tablet → Windows) p50 {0:F1} ms · p95 {1:F1} ms · ",
        ["status.samples"] = "{0}{1:N0} örnek · {2}",
        ["status.pairing"] = "Eşleştirme bekleniyor: {0}",
        ["status.pairingDetail"] = "Tablette şu kodu girin: {0}",
        ["status.waiting"] = "Tablet bekleniyor",
        ["status.waitingWifi"] = "Tableti USB ile bağlayın ya da tablette Wi-Fi'yi seçin.",
        ["status.waitingUsb"] = "Tableti USB ile bağlayın ve tablette Divit'i açın.",
        ["tray.connected"] = "Bağlı: {0} ({1}){2}",
        ["tray.latency"] = " · gecikme p50 {0:F1} / p95 {1:F1} ms",
        ["tray.pairing"] = "Eşleştirme bekleniyor — kod: {0}",
        ["monitor.primary"] = "(birincil)",

        ["menu.settings"] = "Ayarlar…",
        ["menu.usb"] = "USB: {0}",
        ["menu.stats"] = "Örnek: {0:N0} · sıra atlaması {1} · enjeksiyon hatası {2}",
        ["menu.monitor"] = "Monitör",
        ["menu.keepAspect"] = "En-boy oranını koru",
        ["menu.pressure"] = "Basınç (tam basınç için gereken baskı)",
        ["menu.pressure.light"] = "Hafif — %55",
        ["menu.pressure.medium"] = "Orta — %70 (önerilen)",
        ["menu.pressure.firm"] = "Sert — %85",
        ["menu.pressure.full"] = "Tam — %100",
        ["menu.wifi"] = "Wi-Fi bağlantısına izin ver",
        ["menu.pcAddress"] = "   PC adresi: {0}",
        ["menu.pairingCode"] = "   Eşleştirme kodu: {0}",
        ["menu.locked"] = "30 sn beklemede (çok fazla yanlış kod)",
        ["menu.newCode"] = "   Yeni kod üret",
        ["menu.forget"] = "Eşleşmiş tabletleri unut ({0})",
        ["menu.resetLatency"] = "Gecikme istatistiğini sıfırla",
        ["menu.openLog"] = "Log dosyasını aç",
        ["menu.exit"] = "Çıkış",

        ["wifi.warn.title"] = "Divit — Wi-Fi",
        ["wifi.warn"] = "Wi-Fi açılınca Divit yerel ağdan bağlantı kabul eder. Tablet ilk bağlantıda burada gösterilen 6 haneli kodu ister.\n\n" +
                        "Not: Bu sürümde Wi-Fi'de kalem verisi şifrelenmez (bağlantıların kimliği doğrulanır). Yalnızca güvendiğiniz ağlarda kullanın.\n\n" +
                        "Windows Güvenlik Duvarı izin isterse yalnızca \"Özel ağlar\"ı seçin.",
        ["port.error"] = "Divit {0} numaralı portu açamadı:\n{1}\n\nBaşka bir Divit açık olabilir.",

        ["card.screen"] = "Ekran",
        ["card.connection"] = "Wi-Fi ve başlangıç",
        ["card.pressure"] = "Basınç",
        ["card.button"] = "Yan düğme",
        ["card.wintab"] = "WinTab (Krita, Clip Studio, SAI…)",
        ["screen.mapsTo"] = "Kalem bu monitöre eşlenir:",
        ["screen.keepAspect"] = "En-boy oranını koru (çizimler basık/uzun olmaz)",
        ["screen.useAll"] = "Tümünü kullan",
        ["area.full"] = "tüm tablet",
        ["area.part"] = "tabletin %{0:F0}'i",
        ["area.caption"] = "Mavi alan ({0}) → {1}×{2} monitör · sürükleyerek alan seçin",
        ["pressure.help"] = "Tutamaçları sürükleyin: soldaki alt eşik (bundan hafif baskı yok sayılır), sağdaki üst eşik (tam basınç için gereken baskı), " +
                            "ortadaki eğri. Kalemle bastıkça kırmızı nokta canlı hareket eder.",
        ["pressure.soft"] = "Yumuşak",
        ["pressure.linear"] = "Doğrusal",
        ["pressure.hard"] = "Sert",
        ["pressure.values"] = "Alt eşik %{0:F0} · Üst eşik %{1:F0} · Eğri {2:F2}",
        ["pressure.axisX"] = "Tablette baskı →",
        ["pressure.axisY"] = "Windows'a →",
        ["button.pen"] = "Kalem düğmesi (uygulama karar verir)",
        ["button.right"] = "Sağ tık",
        ["button.eraser"] = "Silgi",
        ["button.shortcut"] = "Klavye kısayolu",
        ["button.off"] = "Kapalı",
        ["button.shortcutHint"] = "Tıklayın, tuşlara basın",
        ["button.help"] = "Silgi: kalem düğmesi basılıyken ekrana değince silgi olarak gider (Krita / CSP silgi ucunu destekler). " +
                          "Sağ tık: kalem havadayken düğmeye basın. " +
                          "Kısayol: düğme basılıyken basılı kalır (ör. kaydırmak için Space).",
        ["screen.tapRing"] = "Kalemin değdiği yerde Windows'un halkasını göster",

        ["wintab.help"] = "Bazı çizim uygulamaları basıncı Windows Ink yerine WinTab'dan okur (4096 seviye). Kurulum, wintab32.dll'yi " +
                          "bir kerelik Windows klasörüne kopyalar (yönetici izni). Başka bir sürücünün dosyasına (ör. Wacom) dokunulmaz.",
        ["wintab.installed"] = "● Kurulu",
        ["wintab.installedUsers"] = "● Kurulu · şu an {0} uygulama kullanıyor",
        ["wintab.outdated"] = "● Kurulu (eski sürüm)",
        ["wintab.foreign"] = "Başka bir sürücünün WinTab'ı kurulu",
        ["wintab.missing"] = "Bu pakette WinTab dosyası yok",
        ["wintab.notInstalled"] = "Kurulu değil",
        ["wintab.uninstall"] = "Kaldır",
        ["wintab.update"] = "Güncelle",
        ["wintab.install"] = "WinTab'ı kur",
        ["wintab.done"] = "WinTab kuruldu. Açık çizim uygulamalarını (Krita, Clip Studio…) yeniden başlatın.",
        ["wintab.err.foreign"] = "Başka bir tablet sürücüsünün (ör. Wacom) WinTab dosyası var; Divit onu değiştirmez.",
        ["wintab.err.inUse"] = "Dosya kullanımda: WinTab kullanan uygulamaları (Krita, Clip Studio…) kapatıp tekrar deneyin.",
        ["wintab.err.other"] = "Kurulum başarısız (kod {0}). Ayrıntı: {1}",
        ["wintab.err.denied"] = "Yönetici izni verilmedi.",
        ["conn.wifi"] = "Wi-Fi bağlantısına izin ver",
        ["conn.addresses"] = "Tablette PC adresi: {0}",
        ["conn.codeLabel"] = "Eşleştirme kodu (tablette girilir; kendi 6 hanenizi yazabilirsiniz)",
        ["conn.newCode"] = "Yeni kod",
        ["conn.locked"] = "çok fazla yanlış kod, 30 sn beklemede",
        ["conn.noPaired"] = "Eşleşmiş tablet yok",
        ["conn.paired"] = "Eşleşmiş tablet: {0}",
        ["conn.forget"] = "Eşleşmeleri unut",
        ["conn.autostart"] = "Windows açılınca Divit'i başlat",
        ["conn.theme"] = "Görünüm:",
        ["theme.dark"] = "Koyu",
        ["theme.light"] = "Açık",
        ["theme.system"] = "Sistem",
        ["conn.language"] = "Dil:",
        ["lang.system"] = "Sistem",

        ["adb.starting"] = "başlatılıyor",
        ["adb.missing"] = "adb bulunamadı — Divit'i USB bağlantı desteğiyle yeniden kurun",
        ["adb.notRunning"] = "adb çalışmıyor",
        ["adb.unauthorized"] = "tablette \"USB hata ayıklamaya izin ver\" onayı bekleniyor",
        ["adb.noDevice"] = "USB'de tablet yok",
        ["adb.ready"] = "USB hazır ({0})",
    };

    /// <summary>Keys missing in either dictionary (for tests).</summary>
    public static IEnumerable<string> MissingKeys() =>
        En.Keys.Except(Tr.Keys).Select(k => "tr:" + k).Concat(Tr.Keys.Except(En.Keys).Select(k => "en:" + k));
}
