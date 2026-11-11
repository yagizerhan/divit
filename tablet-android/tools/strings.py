# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 Divit contributors
#
# Source of the tablet app's UI strings: English (default, values/) and Turkish (values-tr/).
# Generates app/src/main/res/values{,-tr}/strings.xml with correct Android escaping (apostrophes, quotes).
# Edit this file, then run:  python -I tools/strings.py
# Placeholders use Android format syntax: %1$s, %1$.1f.

import os

S = {
    # App
    "app_name": ("Divit", "Divit"),
    "settings_title": ("Connection", "Bağlantı"),

    # Drawing screen
    "status_pill_desc": ("Connection status (tap for details)", "Bağlantı durumu (dokununca ayrıntılar)"),
    "settings_button_desc": ("Connection settings", "Bağlantı ayarları"),
    "offline_button": ("Connection settings", "Bağlantı ayarları"),

    # Connection screen
    "back_to_drawing": ("Back to drawing", "Çizim ekranına dön"),
    "connection_type": ("Connection type", "Bağlantı türü"),
    "choice_usb_title": ("USB cable", "USB kablosu"),
    "choice_usb_body": ("The most stable connection. Needs a cable and \"USB debugging\" turned on once.",
                        "En kararlı bağlantı. Kablo ve bir kez \"USB hata ayıklama\" gerekir."),
    "choice_wifi_title": ("Wi-Fi", "Wi-Fi"),
    "choice_wifi_body": ("Wireless. Must be on the same network as the PC; asks for a pairing code the first time.",
                         "Kablosuz. PC ile aynı ağda olmalı; ilk bağlantıda eşleştirme kodu sorulur."),
    "pcs_on_network": ("Computers on the network", "Ağdaki bilgisayarlar"),
    "search_again": ("Search again", "Yeniden ara"),
    "searching": ("Searching…", "Aranıyor…"),
    "search_none": ("Not found. Is Divit open on the PC with \"Allow Wi-Fi connections\" checked? Are you on the same network? If not, enter the address below.",
                    "Bulunamadı. PC'de Divit açık ve \"Wi-Fi bağlantısına izin ver\" işaretli mi? Aynı ağda mısınız? Olmazsa adresi aşağıya elle girin."),
    "search_tap": ("Tap to connect:", "Bağlanmak için dokunun:"),
    "enter_address": ("Or enter the PC address", "Ya da PC adresini elle girin"),
    "address_hint": ("e.g. 192.168.1.5", "ör. 192.168.1.5"),
    "own_address_error": ("This is the tablet's own address. Enter the PC's address (there may be an IP conflict).",
                          "Bu, tabletin kendi adresi. PC'nin adresini girin (IP çakışması olabilir)."),
    "wifi_hint_address": ("This tablet's address: %1$s", "Bu tabletin adresi: %1$s"),
    "wifi_hint_paired": ("Paired with this PC.", "Bu PC ile eşleştirildi."),
    "wifi_hint_unpaired": ("The code shown on the PC will be asked on first connection.", "İlk bağlantıda PC'deki kod sorulacak."),
    "forget_pairing": ("Forget pairing", "Eşleştirmeyi unut"),
    "forget_title": ("Delete the pairing?", "Eşleştirme silinsin mi?"),
    "forget_message": ("On the next Wi-Fi connection you will need to enter the 6-digit code shown on the PC again.",
                       "Bir sonraki Wi-Fi bağlantısında PC'deki 6 haneli kodu yeniden girmeniz gerekir."),
    "forget_confirm": ("Delete", "Sil"),
    "forget_done": ("Pairing deleted.", "Eşleştirme silindi."),
    "cancel": ("Cancel", "Vazgeç"),
    "brightness_title": ("Drawing screen brightness", "Çizim ekranı parlaklığı"),
    "brightness_body": ("The tablet is only used as a drawing surface; dimming the screen saves battery.",
                        "Tablet yalnızca çizim yüzeyi olarak kullanılıyor; ekranı kısmak pili korur."),
    "how_to_connect": ("How to connect?", "Nasıl bağlanır?"),
    "help_text": ("USB (one-time setup on the tablet):\n"
                  "1. Turn on Developer options: Settings → About tablet → Software information → tap \"Build number\" "
                  "7 times. Enter your PIN if asked; \"Developer mode has been turned on\" appears.\n"
                  "2. Turn on USB debugging: Settings → Developer options (at the bottom of the list) → turn on \"USB debugging\".\n"
                  "3. Open Divit on the PC, then connect the tablet to the PC with a USB cable (a data cable, not a charge-only one).\n"
                  "4. The tablet asks \"Allow USB debugging?\": tick \"Always allow from this computer\" and tap Allow.\n"
                  "From then on just plug in the cable: the PC sets up the connection by itself. USB debugging lets only "
                  "the computers you allowed talk to the tablet; you can turn it off again in Developer options.\n\n"
                  "Wi-Fi: in the Divit window on the PC, turn on \"Allow Wi-Fi connections\". The PC address and the "
                  "pairing code are shown there. If the window is closed, click the Divit icon in the system tray.\n\n"
                  "The Back button leaves the drawing screen and closes the connection.",
                  "USB (tablette bir kerelik kurulum):\n"
                  "1. Geliştirici seçeneklerini açın: Ayarlar → Tablet hakkında → Yazılım bilgileri → \"Yapım numarası\"na "
                  "7 kez dokunun. PIN sorulursa girin; \"Geliştirici modu açıldı\" yazısı çıkar.\n"
                  "2. USB hata ayıklamayı açın: Ayarlar → Geliştirici seçenekleri (listenin en altında) → \"USB hata ayıklama\"yı açın.\n"
                  "3. PC'de Divit'i açın, sonra tableti USB kablosuyla PC'ye bağlayın (yalnızca şarj eden değil, veri aktaran bir kablo).\n"
                  "4. Tablette \"USB hata ayıklamaya izin verilsin mi?\" sorusu çıkar: \"Bu bilgisayardan her zaman izin ver\"i "
                  "işaretleyip İzin ver'e dokunun.\n"
                  "Bundan sonra kabloyu takmanız yeterli: bağlantıyı PC kendisi kurar. USB hata ayıklama yalnızca izin "
                  "verdiğiniz bilgisayarların tabletle konuşmasını sağlar; istediğinizde Geliştirici seçeneklerinden kapatabilirsiniz.\n\n"
                  "Wi-Fi: PC'deki Divit penceresinde \"Wi-Fi bağlantısına izin ver\"i açın. PC adresi ve eşleştirme kodu "
                  "orada yazar. Pencere kapalıysa sistem tepsisindeki Divit simgesine tıklayın.\n\n"
                  "Geri tuşu çizim ekranından çıkar ve bağlantıyı kapatır."),

    # Status
    "st_connected": ("Connected: %1$s", "Bağlı: %1$s"),
    "st_rtt": ("round trip %1$.1f ms", "gidiş-dönüş %1$.1f ms"),
    "st_connecting": ("Connecting…", "Bağlanıyor…"),
    "st_connecting_wifi": ("Looking for the PC at %1$s.", "%1$s adresindeki PC aranıyor."),
    "st_connecting_usb": ("Divit must be open on the PC and the tablet connected over USB.", "PC'de Divit açık ve tablet USB ile bağlı olmalı."),
    "st_found_new": ("PC found at a new address: %1$s", "PC yeni adreste bulundu: %1$s"),
    "st_pairing": ("Waiting for pairing", "Eşleştirme bekleniyor"),
    "st_pairing_detail": ("Enter the 6-digit code shown on the PC.", "PC'deki 6 haneli kodu girin."),
    "st_pairing_invalid": ("The pairing is no longer valid; pair again.", "Eşleştirme geçersiz; yeniden eşleştirin."),
    "st_disconnected": ("Not connected", "Bağlı değil"),

    # Reasons (net.Reason)
    "r_none": ("Connection closed.", "Bağlantı kapalı."),
    "r_pc_not_running_usb": ("Divit is not running on the PC, or USB is not ready.", "PC'de Divit çalışmıyor ya da USB hazır değil."),
    "r_wifi_not_allowed": ("Wi-Fi is off on the PC — turn on \"Allow Wi-Fi connections\" in the Divit window on the PC.",
                           "PC'de Wi-Fi izni kapalı — PC'deki Divit penceresinde \"Wi-Fi bağlantısına izin ver\"i açın."),
    "r_unreachable": ("Cannot reach the PC (%1$s).", "PC'ye ulaşılamıyor (%1$s)."),
    "r_no_answer": ("%1$s does not answer — is the address right? Does the firewall allow Divit?",
                    "%1$s yanıt vermiyor — adres doğru mu? Güvenlik duvarı Divit'e izin veriyor mu?"),
    "r_timeout": ("Timed out.", "Zaman aşımı."),
    "r_protocol_error": ("Protocol error: %1$s", "Protokol hatası: %1$s"),
    "r_version": ("The Divit version on the PC is not compatible. Update both apps.", "PC'deki Divit sürümü uyumsuz. İki uygulamayı da güncelleyin."),
    "r_different_pc": ("The PC you paired with is not at this address: a different computer answered.",
                       "Bu adreste eşleştirdiğiniz PC yok: farklı bir bilgisayar yanıt verdi."),
    "r_pc_not_verified": ("The PC could not prove its identity: this is not the PC you paired with.",
                          "PC kimliğini kanıtlayamadı: bu, eşleştirdiğiniz PC değil."),
    "r_usb_key_mismatch": ("The PC's USB key does not match; the PC is sending a new one.", "PC'nin USB anahtarı uyuşmuyor; PC yeni anahtarı gönderiyor."),
    "r_usb_key_pending": ("Waiting for the USB key (the PC is sending it)…", "USB anahtarı bekleniyor (PC gönderiyor)…"),
    "r_pc_closed": ("The PC closed the connection.", "PC bağlantıyı kapattı."),
    "r_replaced": ("Another tablet connected to this PC. Tap to reconnect.", "Başka bir tablet bu PC'ye bağlandı. Yeniden bağlanmak için dokunun."),
    "r_connection_lost": ("Connection lost (%1$s).", "Bağlantı koptu (%1$s)."),
    "r_pairing_cancelled": ("Pairing cancelled.", "Eşleştirme iptal edildi."),
    "r_too_many_wrong": ("Pairing failed: too many wrong codes. Wait a little and try again.", "Eşleştirme başarısız: çok fazla yanlış kod. Biraz bekleyip tekrar deneyin."),

    # Pairing dialog
    "pair_title": ("Pair with %1$s", "%1$s ile eşleştir"),
    "pair_info": ("The code is shown in the Divit window on the PC (\"Wi-Fi and startup\"). It is asked only once for this PC.",
                  "Kod, PC'deki Divit penceresinde (\"Wi-Fi ve başlangıç\") yazar. Bu PC için yalnızca bir kez sorulur."),
    "pair_wrong": ("Wrong code. Enter the current code shown on the PC again.", "Kod yanlış. PC'deki güncel kodu tekrar girin."),
    "pair_paused": ("Too many wrong codes were tried, so the PC paused pairing for a while; even the right code is refused until then. Wait a little and try again.",
                    "Çok fazla yanlış kod denendiği için PC eşleştirmeyi bir süre durdurdu; o zamana kadar doğru kod da reddedilir. Biraz bekleyip tekrar deneyin."),
    "pair_pc_forgot": ("This PC says it no longer knows this tablet. That is expected if you cleared the pairings on the PC. If you did not, do not enter the code: another device may be imitating the PC.",
                       "Bu PC artık bu tableti tanımadığını söylüyor. PC'de eşleştirmeleri sildiyseniz bu normaldir. Silmediyseniz kodu girmeyin: başka bir cihaz PC'yi taklit ediyor olabilir."),
    "pair_ok": ("Pair", "Eşleştir"),
}


def esc(s):
    s = s.replace("\\", "\\\\").replace("'", "\\'").replace('"', '\\"').replace("\n", "\\n")
    if s[:1] in ("@", "?"):
        s = "\\" + s
    return s


def write(lang_index, folder, comment):
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "..", "app", "src", "main", "res", folder, "strings.xml")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    lines = ['<?xml version="1.0" encoding="utf-8"?>',
             f"<!-- {comment} Generated by tools/strings.py; edit that file instead. -->",
             "<resources>"]
    for key, vals in S.items():
        attr = ' translatable="false"' if key == "app_name" and lang_index == 0 else ""
        if key == "app_name" and lang_index == 1:
            continue   # not translatable
        lines.append(f'    <string name="{key}"{attr}>{esc(vals[lang_index])}</string>')
    lines.append("</resources>")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print(out)


write(0, "values", "English (default).")
write(1, "values-tr", "Turkish.")
