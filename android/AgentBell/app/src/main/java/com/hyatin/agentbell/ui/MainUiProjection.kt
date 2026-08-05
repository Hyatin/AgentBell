package com.hyatin.agentbell.ui

import com.hyatin.agentbell.connection.ConnectionState
import com.hyatin.agentbell.protocol.AgentEvent

object MainUiProjection {
    fun connectionLabel(state: ConnectionState): String = when (state) {
        ConnectionState.Unpaired -> "Unpaired"
        ConnectionState.Validating -> "Validating"
        ConnectionState.Connecting -> "Connecting"
        is ConnectionState.Connected -> "Connected"
        is ConnectionState.Reconnecting -> "Reconnecting（${state.delaySeconds}s）"
        ConnectionState.Unauthorized -> "Unauthorized"
        ConnectionState.ProtocolMismatch -> "ProtocolMismatch"
        ConnectionState.NoNetwork -> "NoNetwork"
        ConnectionState.Stopped -> "Stopped"
        is ConnectionState.Error -> "Error（${state.code}）"
    }

    fun recentEvents(events: List<AgentEvent>): List<AgentEvent> = events
        .distinctBy { it.eventId }
        .sortedByDescending { it.sequence }
        .take(50)
}
