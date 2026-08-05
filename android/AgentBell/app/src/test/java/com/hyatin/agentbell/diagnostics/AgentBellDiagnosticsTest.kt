package com.hyatin.agentbell.diagnostics

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AgentBellDiagnosticsTest {
    @Test fun summaryContainsOnlyBoundedMetadata() {
        val diagnostics = BoundedAgentBellDiagnostics(capacity = 2)
        diagnostics.record(
            BoundedAgentBellDiagnostics.create(
                state = "connected",
                deviceId = "raw-device-id",
                connectionId = "abcd1234",
                messageType = "event",
                sequence = 9,
                notificationPosted = true,
            ),
        )
        val output = diagnostics.sanitizedSummary()
        assertTrue(output.contains("message=event"))
        assertTrue(output.contains("sequence=9"))
        assertFalse(output.contains("raw-device-id"))
        assertFalse(output.contains("192.168."))
        assertFalse(output.contains("Bearer"))
        assertFalse(output.contains("summary"))
    }

    @Test fun capacityDropsOldestEntry() {
        val diagnostics = BoundedAgentBellDiagnostics(capacity = 2)
        diagnostics.record(BoundedAgentBellDiagnostics.create(state = "one"))
        diagnostics.record(BoundedAgentBellDiagnostics.create(state = "two"))
        diagnostics.record(BoundedAgentBellDiagnostics.create(state = "three"))
        val output = diagnostics.sanitizedSummary()
        assertFalse(output.contains("state=one"))
        assertTrue(output.contains("state=two"))
        assertTrue(output.contains("state=three"))
    }
}
