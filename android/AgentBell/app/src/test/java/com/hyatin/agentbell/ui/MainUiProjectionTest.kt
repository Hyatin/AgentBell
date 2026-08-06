package com.hyatin.agentbell.ui

import com.hyatin.agentbell.R
import com.hyatin.agentbell.connection.ConnectionState
import com.hyatin.agentbell.testEvent
import org.junit.Assert.assertEquals
import org.junit.Test

class MainUiProjectionTest {
    @Test fun exposesAllRequiredConnectionStatesAsStableResources() {
        assertEquals(R.string.connection_unpaired, MainUiProjection.connectionText(ConnectionState.Unpaired).resourceId)
        assertEquals(R.string.connection_validating, MainUiProjection.connectionText(ConnectionState.Validating).resourceId)
        assertEquals(R.string.connection_connecting, MainUiProjection.connectionText(ConnectionState.Connecting).resourceId)
        assertEquals(
            R.string.connection_connected,
            MainUiProjection.connectionText(ConnectionState.Connected("PC", "now")).resourceId,
        )
        val reconnecting = MainUiProjection.connectionText(ConnectionState.Reconnecting("PC", 5))
        assertEquals(R.string.connection_reconnecting, reconnecting.resourceId)
        assertEquals(listOf(5), reconnecting.arguments)
        assertEquals(R.string.connection_unauthorized, MainUiProjection.connectionText(ConnectionState.Unauthorized).resourceId)
        assertEquals(R.string.connection_protocol_mismatch, MainUiProjection.connectionText(ConnectionState.ProtocolMismatch).resourceId)
        assertEquals(R.string.connection_no_network, MainUiProjection.connectionText(ConnectionState.NoNetwork).resourceId)
        assertEquals(R.string.connection_stopped, MainUiProjection.connectionText(ConnectionState.Stopped).resourceId)
        assertEquals(R.string.connection_error, MainUiProjection.connectionText(ConnectionState.Error("test_error")).resourceId)
    }

    @Test fun mapsPairingCodesWithoutDisplayingProtocolCodes() {
        assertEquals(
            R.string.pairing_error_unauthorized,
            MainUiProjection.pairingErrorText("unauthorized").resourceId,
        )
        assertEquals(
            R.string.pairing_error_invalid_code,
            MainUiProjection.pairingErrorText("pairing_invalid_token").resourceId,
        )
        assertEquals(
            R.string.pairing_error_unavailable,
            MainUiProjection.pairingErrorText("status_unavailable").resourceId,
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
