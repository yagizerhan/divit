// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors

package io.github.yagizerhan.divit.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DiscoveryTest {
    @Test fun parsesReplyWithSpacesInName() =
        assertEquals(FoundPc("Erhan's PC", "192.168.1.5", 47470), Discovery.parse("DIVIT-HERE 1 47470 Erhan's PC\n", "192.168.1.5"))

    @Test fun parsesV2ReplyWithHostId() =
        assertEquals(FoundPc("ERHAN PC", "192.168.1.5", 47470, "000102030405060708090a0b0c0d0e0f"),
            Discovery.parse("DIVIT-HERE 2 47470 000102030405060708090A0B0C0D0E0F ERHAN PC", "192.168.1.5"))

    @Test fun rejectsForeignOrBrokenReplies() {
        assertNull(Discovery.parse("DIVIT-HERE 2 47470 zz X", "a"))       // the ID is not hex
        assertNull(Discovery.parse("DIVIT-HERE 2 47470 X", "a"))          // too few fields for version 2
        assertNull(Discovery.parse("DIVIT-HERE 1 99999 X", "a"))          // invalid port
        assertNull(Discovery.parse("DIVIT-HERE 1 47470", "a"))            // ad yok
        assertNull(Discovery.parse("HELLO", "a"))
    }
}
