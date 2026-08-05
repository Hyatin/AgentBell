package com.hyatin.agentbell.connection

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.time.Instant

sealed interface ConnectionState {
    data object Unpaired : ConnectionState
    data object Validating : ConnectionState
    data object Connecting : ConnectionState
    data class Connected(val deviceName: String, val connectedAt: String) : ConnectionState
    data class Reconnecting(val deviceName: String, val delaySeconds: Int) : ConnectionState
    data object Unauthorized : ConnectionState
    data object ProtocolMismatch : ConnectionState
    data object NoNetwork : ConnectionState
    data object Stopped : ConnectionState
    data class Error(val code: String) : ConnectionState
}

class ConnectionStateRepository {
    private val mutableState = MutableStateFlow<ConnectionState>(ConnectionState.Unpaired)
    val state: StateFlow<ConnectionState> = mutableState.asStateFlow()

    fun update(value: ConnectionState) {
        mutableState.value = value
    }

    fun connected(deviceName: String) {
        update(ConnectionState.Connected(deviceName, Instant.now().toString()))
    }
}

class ReconnectPolicy {
    private var attempt = 0

    fun nextDelaySeconds(): Int {
        val delay = DELAYS[minOf(attempt, DELAYS.lastIndex)]
        if (attempt < DELAYS.lastIndex) attempt++
        return delay
    }

    fun reset() {
        attempt = 0
    }

    companion object {
        val DELAYS = intArrayOf(1, 2, 5, 10, 30)
    }
}
