// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// Several outputs at once: Windows Ink (cursor + Ink apps) and WinTab (architecture §3.2).

namespace Divit.Core;

public sealed class CompositeSink(IPenSink primary, params IPenSink[] secondary) : IPenSink
{
    /// <summary>The state machine follows the primary output (Windows Ink); failures of secondary outputs do not reset the pen.</summary>
    public bool Apply(in PenOutput o)
    {
        bool ok = primary.Apply(o);
        foreach (var s in secondary) s.Apply(o);
        return ok;
    }

    public void Dispose()
    {
        primary.Dispose();
        foreach (var s in secondary) s.Dispose();
    }
}
