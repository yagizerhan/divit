// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// The PC's permanent identity and USB key (protocol/SPEC.md §3.2, §3.2.1): %APPDATA%\Divit\identity.json
//  - HostId: the tablet picks its key by it and tells another (fake) PC apart.
//  - UsbKey: the authentication key on USB. The PC sends it over adb only to the Divit app (AdbBridge); other apps
//    on the tablet do not know it, so they cannot inject input into the PC through the adb reverse tunnel.

using System.Text.Json;
using Divit.Protocol;

namespace Divit.Core.Server;

public sealed class HostIdentity
{
    public byte[] HostId { get; }
    public byte[] UsbKey { get; }

    private HostIdentity(byte[] hostId, byte[] usbKey) { HostId = hostId; UsbKey = usbKey; }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Divit", "identity.json");

    /// <summary>In memory only (tests).</summary>
    public static HostIdentity Ephemeral() => new(Auth.Random(Auth.IdSize), Auth.Random(Auth.KeySize));

    private sealed record Stored(string HostId, string UsbKey);

    public static HostIdentity LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            if (s != null)
            {
                byte[] id = Convert.FromHexString(s.HostId), key = Convert.FromHexString(s.UsbKey);
                if (id.Length == Auth.IdSize && key.Length == Auth.KeySize) return new(id, key);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or UnauthorizedAccessException) { }

        var created = Ephemeral();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, JsonSerializer.Serialize(new Stored(Convert.ToHexString(created.HostId), Convert.ToHexString(created.UsbKey))));
            Log.Info("created a new PC identity");
        }
        catch (Exception e) { Log.Info($"could not save the PC identity (using it for this run only): {e.Message}"); }
        return created;
    }
}

/// <summary>An interrupted write must not corrupt the file: write to a temp file, then move it into place.</summary>
public static class AtomicFile
{
    public static void Write(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
