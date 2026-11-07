// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Background log writer (the hot path only enqueues).

using System.Collections.Concurrent;

namespace Divit.Core;

/// <summary>Simple log written in the background: %LOCALAPPDATA%\Divit\host.log (rotates at 1 MB).</summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(1000);
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Divit");
    public static string FilePath => Path.Combine(Dir, "host.log");

    static Log()
    {
        Directory.CreateDirectory(Dir);
        var t = new Thread(() =>
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".1", true);
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch { /* if the log cannot be written the app keeps running */ }
            }
        }) { IsBackground = true, Name = "log" };
        t.Start();
    }

    /// <summary>Tests turn this off so they do not pollute the user's host.log.</summary>
    public static volatile bool Enabled = true;

    public static void Info(string msg)
    {
        if (Enabled) Queue.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}");
    }
}
