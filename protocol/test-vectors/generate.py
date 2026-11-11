# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 Divit contributors
#
# Generates the protocol test vectors: vectors.json.
# This script is an independent "reference encoder" for SPEC.md; the C# and Kotlin implementations
# must produce and decode the same bytes. Run: python -I generate.py  (needs the "cryptography" package)

import json, struct, os, hmac, hashlib
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives import serialization

VERSION = 1

def frame(channel, mtype, payload, version=VERSION):
    return struct.pack('<BBHI', version, channel, mtype, len(payload)) + payload

def s(text):
    b = text.encode('utf-8')
    return struct.pack('<B', len(b)) + b

DEV = bytes(range(16))
ZERO16 = bytes(16)
CNONCE = bytes(range(0x10, 0x20))
SNONCE = bytes(range(0x20, 0x30))
HOSTID = bytes(range(0x30, 0x40))
KEY = bytes(range(0x40, 0x60))   # 32 bytes (USB key or pairing key)

def mac(key, *parts):
    return hmac.new(key, b''.join(parts), hashlib.sha256).digest()

def host_proof(k, dev, hid, cn, sn): return mac(k, b"DIVIT-HOST1", dev, hid, cn, sn)
def tablet_proof(k, dev, hid, cn, sn): return mac(k, b"DIVIT-TAB1", dev, hid, cn, sn)
def pair_key(secret, dev, hid): return mac(secret, b"DIVIT-PAIR1", dev, hid)

def b8(b):
    return struct.pack('<B', len(b)) + b

# Fixed (deterministic) P-256 keys: for tests only.
def p256(d):
    priv = ec.derive_private_key(d, ec.SECP256R1())
    spki = priv.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    pkcs8 = priv.private_bytes(serialization.Encoding.DER, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
    return priv, spki, pkcs8

T_PRIV, T_SPKI, T_PKCS8 = p256(0x1111111111111111111111111111111111111111111111111111111111111111)
H_PRIV, H_SPKI, H_PKCS8 = p256(0x2222222222222222222222222222222222222222222222222222222222222222)
SHARED = T_PRIV.exchange(ec.ECDH(), H_PRIV.public_key())
assert SHARED == H_PRIV.exchange(ec.ECDH(), T_PRIV.public_key())
HPROOF = host_proof(KEY, DEV, HOSTID, CNONCE, SNONCE)
TPROOF = tablet_proof(KEY, DEV, HOSTID, CNONCE, SNONCE)

def sample(t_us, u, v, p, tx, ty, flags, seq):
    return struct.pack('<qfffffHH', t_us, u, v, p, tx, ty, flags, seq)

vectors = []
def ok(name, ch, mtype, payload, message):
    vectors.append({"name": name, "valid": True, "hex": frame(ch, mtype, payload).hex(), "message": message})
def bad(name, raw, error):
    vectors.append({"name": name, "valid": False, "hex": raw.hex(), "error": error})

ok("hello_usb", 0, 0x0001,
   struct.pack('<HBB', 1, 0, 0b01111) + DEV + CNONCE + s("samsung SM-X610"),
   {"type": "HELLO", "minor": 1, "transport": 0, "capabilities": 15, "deviceId": DEV.hex(), "clientNonce": CNONCE.hex(), "model": "samsung SM-X610"})

ok("hello_ack_authenticate", 0, 0x0002, struct.pack('<BH', 0, 0) + s("ERHAN") + HOSTID + SNONCE + HPROOF,
   {"type": "HELLO_ACK", "result": 0, "minor": 0, "hostName": "ERHAN", "hostId": HOSTID.hex(), "serverNonce": SNONCE.hex(), "hostProof": HPROOF.hex()})

ok("hello_ack_pairing_required", 0, 0x0002, struct.pack('<BH', 2, 0) + s("ERHAN") + HOSTID + SNONCE + bytes(32),
   {"type": "HELLO_ACK", "result": 2, "minor": 0, "hostName": "ERHAN", "hostId": HOSTID.hex(), "serverNonce": SNONCE.hex(), "hostProof": bytes(32).hex()})

ok("auth", 0, 0x0005, TPROOF, {"type": "AUTH", "proof": TPROOF.hex()})

ok("auth_result_ok", 0, 0x0006, struct.pack('<B', 1), {"type": "AUTH_RESULT", "ok": 1})

ok("pair_request", 0, 0x0003, struct.pack('<I', 482913) + s("Tab S9 FE+") + b8(T_SPKI),
   {"type": "PAIR_REQUEST", "code": 482913, "deviceName": "Tab S9 FE+", "tabletKey": T_SPKI.hex()})

# Minor 1: PAIR_RESULT ends with reason:u8 (0 wrong code, 1 paused, 2 Wi-Fi off). Ignored on success.
ok("pair_result_ok", 0, 0x0004, struct.pack('<B', 1) + b8(H_SPKI) + struct.pack('<B', 0),
   {"type": "PAIR_RESULT", "ok": 1, "hostKey": H_SPKI.hex(), "reason": 0})

ok("pair_result_wrong_code", 0, 0x0004, struct.pack('<BBB', 0, 0, 0),
   {"type": "PAIR_RESULT", "ok": 0, "hostKey": "", "reason": 0})

ok("pair_result_paused", 0, 0x0004, struct.pack('<BBB', 0, 0, 1),
   {"type": "PAIR_RESULT", "ok": 0, "hostKey": "", "reason": 1})

# A minor-0 host sends no reason: read as "wrong code" (decode only; it re-encodes with the reason byte).
ok("pair_result_minor0_no_reason", 0, 0x0004, struct.pack('<BB', 0, 0),
   {"type": "PAIR_RESULT", "ok": 0, "hostKey": "", "reason": 0})

ok("ping", 0, 0x0010, struct.pack('<Iq', 7, 123456789),
   {"type": "PING", "id": 7, "t0": 123456789})

ok("pong", 0, 0x0011, struct.pack('<Iqqq', 7, 123456789, -5000000000, -4999999990),
   {"type": "PONG", "id": 7, "t0": 123456789, "t1": -5000000000, "t2": -4999999990})

ok("surface_info", 0, 0x0020, struct.pack('<IIff', 2560, 1600, 279.5, 174.7),
   {"type": "SURFACE_INFO", "widthPx": 2560, "heightPx": 1600, "widthMm": struct.unpack('<f', struct.pack('<f', 279.5))[0], "heightMm": struct.unpack('<f', struct.pack('<f', 174.7))[0]})

samples = [
    (1000000, 0.25, 0.5, 0.0, 0.0, 0.0, 0b00001, 65535),          # hover
    (1002730, 0.2505, 0.5, 0.125, 10.0, -20.0, 0b01011, 0),       # contact, historical, seq wrap-around
    (1005460, 1.005, -0.004, 0.6816, 0.0, 0.0, 0b00111, 1),       # contact + button, edge tolerance
]
ok("pen_batch_3", 1, 0x0030,
   struct.pack('<HH', len(samples), 0) + b''.join(sample(*x) for x in samples),
   {"type": "PEN_BATCH", "samples": [
       {"t_us": a, "u": struct.unpack('<f', struct.pack('<f', b))[0], "v": struct.unpack('<f', struct.pack('<f', c))[0],
        "pressure": struct.unpack('<f', struct.pack('<f', d))[0], "tiltX": e, "tiltY": f, "flags": g, "seq": h}
       for (a, b, c, d, e, f, g, h) in samples]})

ok("host_status", 0, 0x0040, s("\\\\.\\DISPLAY1") + struct.pack('<ffff', 0.0, 0.0625, 1.0, 0.9375),
   {"type": "HOST_STATUS", "monitorName": "\\\\.\\DISPLAY1", "areaLeft": 0.0, "areaTop": 0.0625, "areaRight": 1.0, "areaBottom": 0.9375})

ok("bye_normal", 0, 0x00FF, struct.pack('<B', 0), {"type": "BYE", "reason": 0})

ok("unknown_type_ignored", 0, 0x7777, b'\x01\x02\x03', {"type": "UNKNOWN", "rawType": 0x7777})

ok("ping_with_trailing_bytes", 0, 0x0010, struct.pack('<Iq', 8, 1) + b'\xEE\xEE',
   {"type": "PING", "id": 8, "t0": 1})

# Invalid
bad("wrong_version", frame(0, 0x0010, struct.pack('<Iq', 1, 1), version=2), "version")
bad("ping_too_short", frame(0, 0x0010, struct.pack('<I', 1)), "length")
bad("pen_batch_zero_count", frame(1, 0x0030, struct.pack('<HH', 0, 0)), "range")
bad("pen_batch_length_mismatch", frame(1, 0x0030, struct.pack('<HH', 2, 0) + sample(*samples[0])), "length")
bad("pen_batch_nan_pressure", frame(1, 0x0030, struct.pack('<HH', 1, 0) + sample(1, 0.5, 0.5, float('nan'), 0, 0, 3, 0)), "range")
bad("pen_batch_u_out_of_range", frame(1, 0x0030, struct.pack('<HH', 1, 0) + sample(1, 1.02, 0.5, 0.5, 0, 0, 3, 0)), "range")
bad("pen_batch_tilt_out_of_range", frame(1, 0x0030, struct.pack('<HH', 1, 0) + sample(1, 0.5, 0.5, 0.5, 91.0, 0, 3, 0)), "range")
bad("pen_batch_contact_without_range", frame(1, 0x0030, struct.pack('<HH', 1, 0) + sample(1, 0.5, 0.5, 0.5, 0, 0, 0b10, 0)), "range")
bad("frame_too_large", struct.pack('<BBHI', 1, 0, 0x0010, 65537), "length")
bad("string_overrun", frame(0, 0x0002, struct.pack('<BH', 0, 0) + struct.pack('<B', 10) + b'abc'), "length")
bad("hello_ack_missing_proof", frame(0, 0x0002, struct.pack('<BH', 0, 0) + s("ERHAN") + HOSTID + SNONCE), "length")
bad("surface_info_infinite_mm", frame(0, 0x0020, struct.pack('<IIff', 2560, 1600, float('inf'), 174.7)), "range")
bad("surface_info_too_small_mm", frame(0, 0x0020, struct.pack('<IIff', 2560, 1600, 279.5, 0.5)), "range")

# Cryptography (SPEC §3.2–3.3): C# and Kotlin must produce the same values.
crypto = {
    "key": KEY.hex(), "deviceId": DEV.hex(), "hostId": HOSTID.hex(), "clientNonce": CNONCE.hex(), "serverNonce": SNONCE.hex(),
    "hostProof": HPROOF.hex(), "tabletProof": TPROOF.hex(),
    "tabletPrivatePkcs8": T_PKCS8.hex(), "tabletPublicSpki": T_SPKI.hex(),
    "hostPrivatePkcs8": H_PKCS8.hex(), "hostPublicSpki": H_SPKI.hex(),
    "sharedSecret": SHARED.hex(), "pairKey": pair_key(SHARED, DEV, HOSTID).hex(),
}

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'vectors.json')
with open(out, 'w', encoding='utf-8', newline='\n') as f:
    json.dump({"protocolVersion": VERSION, "vectors": vectors, "crypto": crypto}, f, ensure_ascii=False, indent=2)
    f.write('\n')
print(f"{len(vectors)} vectors -> {out}")
