package com.hyatin.agentbell.ui

import androidx.annotation.StringRes
import com.hyatin.agentbell.R
import com.hyatin.agentbell.connection.ConnectionState
import com.hyatin.agentbell.protocol.AgentEvent

data class UiText(
    @param:StringRes val resourceId: Int,
    val arguments: List<Any> = emptyList(),
)

object MainUiProjection {
    fun connectionText(state: ConnectionState): UiText = when (state) {
        ConnectionState.Unpaired -> UiText(R.string.connection_unpaired)
        ConnectionState.Validating -> UiText(R.string.connection_validating)
        ConnectionState.Connecting -> UiText(R.string.connection_connecting)
        is ConnectionState.Connected -> UiText(R.string.connection_connected)
        is ConnectionState.Reconnecting -> UiText(
            R.string.connection_reconnecting,
            listOf(state.delaySeconds),
        )
        ConnectionState.Unauthorized -> UiText(R.string.connection_unauthorized)
        ConnectionState.ProtocolMismatch -> UiText(R.string.connection_protocol_mismatch)
        ConnectionState.NoNetwork -> UiText(R.string.connection_no_network)
        ConnectionState.Stopped -> UiText(R.string.connection_stopped)
        is ConnectionState.Error -> UiText(R.string.connection_error)
    }

    fun pairingErrorText(code: String): UiText = when {
        code == "unauthorized" -> UiText(R.string.pairing_error_unauthorized)
        code == "protocol_mismatch" -> UiText(R.string.pairing_error_protocol)
        code == "status_unavailable" || code == "status_http_error" ->
            UiText(R.string.pairing_error_unavailable)
        code == "credential_store_failed" -> UiText(R.string.pairing_error_storage)
        code.startsWith("pairing_") && code.contains("token") ->
            UiText(R.string.pairing_error_invalid_code)
        code.startsWith("pairing_") -> UiText(R.string.pairing_error_invalid_url)
        else -> UiText(R.string.pairing_error_configuration)
    }

    fun recentEvents(events: List<AgentEvent>): List<AgentEvent> = events
        .filter { it.resolvedAt == null }
        .distinctBy { it.eventId }
        .sortedByDescending { it.sequence }
        .take(50)
}
