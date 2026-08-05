package com.hyatin.agentbell.ui

import com.hyatin.agentbell.connection.ConnectionState
import com.hyatin.agentbell.testEvent
import org.junit.Assert.assertEquals
import org.junit.Test

class MainUiProjectionTest {
    @Test fun exposesAllRequiredConnectionStates() {
        assertEquals("Unpaired", MainUiProjection.connectionLabel(ConnectionState.Unpaired))
        assertEquals("Validating", MainUiProjection.connectionLabel(ConnectionState.Validating))
        assertEquals("Connecting", MainUiProjection.connectionLabel(ConnectionState.Connecting))
        assertEquals(
            "Connected",
            MainUiProjection.connectionLabel(ConnectionState.Connected("PC", "now")),
        )
        assertEquals(
            "Reconnecting（5s）",
            MainUiProjection.connectionLabel(ConnectionState.Reconnecting("PC", 5)),
        )
        assertEquals("Unauthorized", MainUiProjection.connectionLabel(ConnectionState.Unauthorized))
        assertEquals(
            "ProtocolMismatch",
            MainUiProjection.connectionLabel(ConnectionState.ProtocolMismatch),
        )
        assertEquals("NoNetwork", MainUiProjection.connectionLabel(ConnectionState.NoNetwork))
        assertEquals("Stopped", MainUiProjection.connectionLabel(ConnectionState.Stopped))
        assertEquals(
            "Error（test_error）",
            MainUiProjection.connectionLabel(ConnectionState.Error("test_error")),
        )
    }

    @Test fun recentEventsAreSortedDeduplicatedAndLimited() {
        val values = (1..60).map { testEvent("event-$it", it.toLong()) } +
            testEvent("event-60", 60)
        val result = MainUiProjection.recentEvents(values)
        assertEquals(50, result.size)
        assertEquals(60, result.first().sequence)
        assertEquals(11, result.last().sequence)
        assertEquals(1, result.count { it.eventId == "event-60" })
    }
}
