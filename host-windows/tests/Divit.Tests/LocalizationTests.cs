// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// UI strings: every key exists in both English and Turkish, and every format string accepts its arguments.

using Divit.Host;
using Xunit;

namespace Divit.Tests;

public class LocalizationTests
{
    [Fact]
    public void EnglishAndTurkishHaveTheSameKeys() => Assert.Empty(L.MissingKeys());

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public void FormatsWithArguments(string lang)
    {
        L.Use(lang);
        try
        {
            Assert.Contains("42", L.T("conn.paired", 42));
            Assert.Contains("ERHAN", L.T("status.pairing", "ERHAN"));
            Assert.Contains("12", L.T("pressure.values", 12f, 70f, 1f));
            Assert.NotEqual("menu.exit", L.T("menu.exit"));          // a real translation, not the key
            Assert.Equal("no.such.key", L.T("no.such.key"));          // unknown keys fall back to the key itself
        }
        finally { L.Use("System"); }
    }
}
