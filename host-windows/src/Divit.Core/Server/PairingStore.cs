// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Wi-Fi pairing store (SPEC §3.3): deviceId → pairing key. The key never goes on the wire (derived with ECDH,
// then used only for HMAC proofs).

using System.Text.Json;
using Divit.Protocol;

namespace Divit.Core.Server;

/// <summary>%APPDATA%\Divit\paired.json</summary>
public sealed class PairingStore
{
    public sealed record Entry(string DeviceId, string Token, string Name, DateTime PairedAt);

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Divit", "paired.json");
    private readonly string FilePath;
    private readonly object _gate = new();
    private List<Entry> _entries;

    public PairingStore(string? path = null)
    {
        FilePath = path ?? DefaultPath;
        try { _entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { _entries = new(); }
    }

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>This device's pairing key; null if none (or the entry is corrupt / an old 16-byte token).</summary>
    public byte[]? KeyFor(byte[] deviceId)
    {
        string id = Convert.ToHexString(deviceId);
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.DeviceId == id);
            if (e == null) return null;
            try { var k = Convert.FromHexString(e.Token); return k.Length == Auth.KeySize ? k : null; }
            catch (FormatException) { return null; }
        }
    }

    public void Add(byte[] deviceId, string name, byte[] key)
    {
        string id = Convert.ToHexString(deviceId);
        lock (_gate)
        {
            _entries.RemoveAll(e => e.DeviceId == id);
            _entries.Add(new Entry(id, Convert.ToHexString(key), name, DateTime.Now));
            Save();
        }
    }

    public void Clear() { lock (_gate) { _entries.Clear(); Save(); } }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.Write(FilePath, JsonSerializer.Serialize(_entries));
        }
        catch (Exception e) { Log.Info($"could not write the pairing store: {e.Message}"); }   // must not take down the receive thread
    }
}
