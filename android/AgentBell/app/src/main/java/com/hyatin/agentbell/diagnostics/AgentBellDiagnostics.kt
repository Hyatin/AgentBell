package com.hyatin.agentbell.diagnostics

import java.security.MessageDigest
import java.time.Instant
import java.util.ArrayDeque

data class DiagnosticEntry(
    val timestamp: String,
    val state: String? = null,
    val deviceIdHash: String? = null,
    val connectionId: String? = null,
    val messageType: String? = null,
    val sequence: Long? = null,
    val reconnectDelay: Int? = null,
    val notificationPosted: Boolean? = null,
    val deduplicated: Boolean? = null,
    val protocolErrorCode: String? = null,
    val elapsedMs: Long? = null,
)

interface AgentBellDiagnostics {
    fun record(entry: DiagnosticEntry)
    fun sanitizedSummary(): String
}

class BoundedAgentBellDiagnostics(
    private val capacity: Int = 100,
) : AgentBellDiagnostics {
    private val gate = Any()
    private val entries = ArrayDeque<DiagnosticEntry>()

    override fun record(entry: DiagnosticEntry) {
        synchronized(gate) {
            while (entries.size >= capacity) entries.removeFirst()
            entries.addLast(entry)
        }
    }

    override fun sanitizedSummary(): String = synchronized(gate) {
        entries.joinToString(separator = "\n") { entry ->
            listOfNotNull(
                entry.timestamp,
                entry.state?.let { "state=$it" },
                entry.deviceIdHash?.let { "device=$it" },
                entry.connectionId?.let { "connection=$it" },
                entry.messageType?.let { "message=$it" },
                entry.sequence?.let { "sequence=$it" },
                entry.reconnectDelay?.let { "reconnectDelay=$it" },
                entry.notificationPosted?.let { "notificationPosted=$it" },
                entry.deduplicated?.let { "deduplicated=$it" },
                entry.protocolErrorCode?.let { "protocolError=$it" },
                entry.elapsedMs?.let { "elapsedMs=$it" },
            ).joinToString(" ")
        }
    }

    companion object {
        fun create(
            state: String? = null,
            deviceId: String? = null,
            connectionId: String? = null,
            messageType: String? = null,
            sequence: Long? = null,
            reconnectDelay: Int? = null,
            notificationPosted: Boolean? = null,
            deduplicated: Boolean? = null,
            protocolErrorCode: String? = null,
            elapsedMs: Long? = null,
        ) = DiagnosticEntry(
            timestamp = Instant.now().toString(),
            state = state,
            deviceIdHash = deviceId?.let(::hashIdentifier),
            connectionId = connectionId,
            messageType = messageType,
            sequence = sequence,
            reconnectDelay = reconnectDelay,
            notificationPosted = notificationPosted,
            deduplicated = deduplicated,
            protocolErrorCode = protocolErrorCode,
            elapsedMs = elapsedMs,
        )

        fun hashIdentifier(value: String): String = MessageDigest.getInstance("SHA-256")
            .digest(value.toByteArray(Charsets.UTF_8))
            .take(6)
            .joinToString("") { "%02x".format(it) }
    }
}
