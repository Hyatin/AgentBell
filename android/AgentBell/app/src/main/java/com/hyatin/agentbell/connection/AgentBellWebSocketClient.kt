package com.hyatin.agentbell.connection

import com.hyatin.agentbell.diagnostics.AgentBellDiagnostics
import com.hyatin.agentbell.diagnostics.BoundedAgentBellDiagnostics
import com.hyatin.agentbell.protocol.AgentBellProtocol
import com.hyatin.agentbell.protocol.ProtocolMessageCodec
import com.hyatin.agentbell.protocol.ProtocolParseResult
import com.hyatin.agentbell.protocol.ServerMessage
import com.hyatin.agentbell.storage.EventHistoryRepository
import com.hyatin.agentbell.storage.EventProcessResult
import com.hyatin.agentbell.storage.PairingCredential
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.UUID

interface CompletionNotificationSink {
    fun post(event: com.hyatin.agentbell.protocol.AgentEvent): Boolean
}

fun interface ReconnectDelay {
    suspend fun await(seconds: Int)
}

class AgentBellWebSocketClient(
    private val credential: PairingCredential,
    private val okHttpClient: OkHttpClient,
    private val eventHistory: EventHistoryRepository,
    private val notifications: CompletionNotificationSink,
    private val states: ConnectionStateRepository,
    private val diagnostics: AgentBellDiagnostics,
    private val scope: CoroutineScope,
    private val reconnectDelay: ReconnectDelay = ReconnectDelay { delay(it * 1000L) },
) {
    private val gate = Any()
    private val incoming = Channel<IncomingFrame>(capacity = 64)
    private val reconnectPolicy = ReconnectPolicy()
    private val connectionId = UUID.randomUUID().toString().replace("-", "").take(8)

    private var socket: WebSocket? = null
    private var reconnectJob: Job? = null
    private var processorJob: Job? = null
    private var started = false
    private var networkAvailable = true
    private var phase = SocketPhase.IDLE
    private var terminalFailure = false

    fun start() {
        synchronized(gate) {
            if (started) return
            started = true
            terminalFailure = false
            if (processorJob?.isActive != true) {
                processorJob = scope.launch { processIncoming() }
            }
        }
        connectIfAllowed()
    }

    fun stop() {
        val activeSocket: WebSocket?
        synchronized(gate) {
            started = false
            terminalFailure = true
            reconnectJob?.cancel()
            reconnectJob = null
            activeSocket = socket
            socket = null
            phase = SocketPhase.IDLE
        }
        activeSocket?.close(1000, "stopped")
        processorJob?.cancel()
        states.update(ConnectionState.Stopped)
        record(state = "stopped")
    }

    fun onNetworkAvailable() {
        synchronized(gate) {
            networkAvailable = true
            reconnectJob?.cancel()
            reconnectJob = null
        }
        connectIfAllowed()
    }

    fun onNetworkLost() {
        val activeSocket: WebSocket?
        synchronized(gate) {
            networkAvailable = false
            reconnectJob?.cancel()
            reconnectJob = null
            activeSocket = socket
            socket = null
            phase = SocketPhase.IDLE
        }
        activeSocket?.cancel()
        if (started && !terminalFailure) states.update(ConnectionState.NoNetwork)
        record(state = "no_network")
    }

    private fun connectIfAllowed() {
        val shouldConnect = synchronized(gate) {
            started && networkAvailable && !terminalFailure && phase == SocketPhase.IDLE
        }
        if (!shouldConnect) {
            if (started && !networkAvailable) states.update(ConnectionState.NoNetwork)
            return
        }

        synchronized(gate) {
            if (!started || !networkAvailable || terminalFailure || phase != SocketPhase.IDLE) return
            phase = SocketPhase.CONNECTING
        }
        states.update(ConnectionState.Connecting)
        record(state = "connecting")
        val request = Request.Builder()
            .url("ws://${credential.host}:${credential.port}${credential.webSocketPath}")
            .header("Authorization", "Bearer ${credential.token}")
            .build()
        val listener = Listener()
        val created = okHttpClient.newWebSocket(request, listener)
        synchronized(gate) {
            if (started && !terminalFailure && (socket == null || socket === created)) {
                socket = created
            } else {
                created.cancel()
            }
        }
    }

    private suspend fun processIncoming() {
        for (frame in incoming) {
            val current = synchronized(gate) { socket }
            if (current !== frame.socket) continue
            when (val parsed = ProtocolMessageCodec.parseServerMessage(frame.text)) {
                is ProtocolParseResult.Success -> handleMessage(current, parsed.message)
                is ProtocolParseResult.Invalid -> {
                    record(messageType = "invalid", protocolErrorCode = parsed.code)
                }
                ProtocolParseResult.Ignored -> record(messageType = "unknown")
            }
        }
    }

    private suspend fun handleMessage(webSocket: WebSocket, message: ServerMessage) {
        if (message !is ServerMessage.Hello && synchronized(gate) { phase != SocketPhase.OPEN }) {
            record(messageType = "ignored", protocolErrorCode = "message_before_hello")
            return
        }
        when (message) {
            is ServerMessage.Hello -> handleHello(webSocket, message)
            is ServerMessage.Event -> {
                val startedAt = System.nanoTime()
                when (val result = eventHistory.process(message.payload)) {
                    is EventProcessResult.Accepted -> {
                        val posted = notifications.post(result.event)
                        record(
                            messageType = "event",
                            sequence = result.event.sequence,
                            notificationPosted = posted,
                            deduplicated = false,
                            elapsedMs = (System.nanoTime() - startedAt) / 1_000_000,
                        )
                    }
                    EventProcessResult.Duplicate -> record(
                        messageType = "event",
                        sequence = message.payload.sequence,
                        deduplicated = true,
                    )
                    is EventProcessResult.Suppressed -> record(
                        messageType = "event",
                        sequence = message.payload.sequence,
                        notificationPosted = false,
                        protocolErrorCode = "permission_notification_off",
                    )
                    is EventProcessResult.Invalid -> record(
                        messageType = "event",
                        protocolErrorCode = result.code,
                    )
                }
            }
            is ServerMessage.Ping -> {
                webSocket.send(ProtocolMessageCodec.pong(message.timestamp))
                record(messageType = "ping")
            }
            is ServerMessage.Error -> {
                record(messageType = "error", protocolErrorCode = message.code)
                if (message.code == "unauthorized") stopTerminal(ConnectionState.Unauthorized)
                if (message.code == "protocol_mismatch") {
                    stopTerminal(ConnectionState.ProtocolMismatch)
                }
            }
        }
    }

    private suspend fun handleHello(webSocket: WebSocket, hello: ServerMessage.Hello) {
        if (synchronized(gate) { phase != SocketPhase.AWAITING_HELLO }) {
            record(messageType = "hello", protocolErrorCode = "unexpected_hello")
            return
        }
        if (hello.protocolVersion != AgentBellProtocol.VERSION) {
            webSocket.close(1002, "protocol_mismatch")
            stopTerminal(ConnectionState.ProtocolMismatch)
            return
        }
        if (hello.deviceId != credential.deviceId) {
            webSocket.close(1008, "device_mismatch")
            stopTerminal(ConnectionState.Unauthorized)
            return
        }

        synchronized(gate) { phase = SocketPhase.OPEN }
        reconnectPolicy.reset()
        states.connected(credential.deviceName)
        val lastSequence = eventHistory.lastSequence()
        webSocket.send(ProtocolMessageCodec.resume(lastSequence))
        record(messageType = "hello", sequence = lastSequence, state = "connected")
    }

    private fun stopTerminal(state: ConnectionState) {
        val active: WebSocket?
        synchronized(gate) {
            terminalFailure = true
            reconnectJob?.cancel()
            reconnectJob = null
            active = socket
            socket = null
            phase = SocketPhase.IDLE
        }
        active?.close(1008, "terminal")
        states.update(state)
    }

    private fun disconnected(failedSocket: WebSocket, response: Response?) {
        val unauthorized = response?.code == 401 || response?.code == 403
        synchronized(gate) {
            if (socket !== failedSocket) return
            socket = null
            phase = SocketPhase.IDLE
        }
        if (unauthorized) {
            stopTerminal(ConnectionState.Unauthorized)
        } else {
            scheduleReconnect()
        }
    }

    private fun scheduleReconnect() {
        val delaySeconds: Int
        synchronized(gate) {
            if (!started || terminalFailure) return
            if (!networkAvailable) {
                states.update(ConnectionState.NoNetwork)
                return
            }
            if (reconnectJob?.isActive == true) return
            delaySeconds = reconnectPolicy.nextDelaySeconds()
            states.update(ConnectionState.Reconnecting(credential.deviceName, delaySeconds))
            reconnectJob = scope.launch {
                reconnectDelay.await(delaySeconds)
                synchronized(gate) { reconnectJob = null }
                connectIfAllowed()
            }
        }
        record(state = "reconnecting", reconnectDelay = delaySeconds)
    }

    private fun record(
        state: String? = null,
        messageType: String? = null,
        sequence: Long? = null,
        reconnectDelay: Int? = null,
        notificationPosted: Boolean? = null,
        deduplicated: Boolean? = null,
        protocolErrorCode: String? = null,
        elapsedMs: Long? = null,
    ) {
        diagnostics.record(
            BoundedAgentBellDiagnostics.create(
                state = state,
                deviceId = credential.deviceId,
                connectionId = connectionId,
                messageType = messageType,
                sequence = sequence,
                reconnectDelay = reconnectDelay,
                notificationPosted = notificationPosted,
                deduplicated = deduplicated,
                protocolErrorCode = protocolErrorCode,
                elapsedMs = elapsedMs,
            ),
        )
    }

    private inner class Listener : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            synchronized(gate) {
                if (socket != null && socket !== webSocket) return
                socket = webSocket
                phase = SocketPhase.AWAITING_HELLO
            }
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (!incoming.trySend(IncomingFrame(webSocket, text)).isSuccess) {
                webSocket.close(1008, "client_queue_full")
            }
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(code, null)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            disconnected(webSocket, response = null)
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            disconnected(webSocket, response)
        }
    }

    private data class IncomingFrame(val socket: WebSocket, val text: String)

    private enum class SocketPhase {
        IDLE,
        CONNECTING,
        AWAITING_HELLO,
        OPEN,
    }
}
