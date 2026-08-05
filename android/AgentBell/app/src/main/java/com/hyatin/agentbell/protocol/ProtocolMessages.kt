package com.hyatin.agentbell.protocol

import org.json.JSONException
import org.json.JSONObject
import java.nio.charset.StandardCharsets

object AgentBellProtocol {
    const val VERSION = 1
    const val WEB_SOCKET_PATH = "/ws/v1/events"
    const val MAX_MESSAGE_BYTES = 64 * 1024
}

sealed interface ServerMessage {
    data class Hello(
        val protocolVersion: Int,
        val serverVersion: String,
        val deviceName: String,
        val deviceId: String,
        val latestSequence: Long,
        val serverTime: String,
    ) : ServerMessage

    data class Event(val payload: AgentEvent) : ServerMessage

    data class Ping(val timestamp: Long) : ServerMessage

    data class Error(val code: String) : ServerMessage
}

data class AgentEvent(
    val eventId: String,
    val agent: String,
    val status: String,
    val title: String,
    val project: String?,
    val summary: String?,
    val occurredAt: String,
    val sequence: Long,
)

sealed interface ProtocolParseResult {
    data class Success(val message: ServerMessage) : ProtocolParseResult
    data class Invalid(val code: String) : ProtocolParseResult
    data object Ignored : ProtocolParseResult
}

object ProtocolMessageCodec {
    fun parseServerMessage(json: String): ProtocolParseResult {
        if (json.toByteArray(StandardCharsets.UTF_8).size > AgentBellProtocol.MAX_MESSAGE_BYTES) {
            return ProtocolParseResult.Invalid("message_too_large")
        }

        val root = try {
            JSONObject(json)
        } catch (_: JSONException) {
            return ProtocolParseResult.Invalid("invalid_json")
        }

        return when (val type = root.strictString("type", 32)) {
            "hello" -> parseHello(root)
            "event" -> parseEvent(root)
            "ping" -> parsePing(root)
            "error" -> parseError(root)
            null -> ProtocolParseResult.Invalid("invalid_message")
            else -> ProtocolParseResult.Ignored
        }
    }

    fun resume(lastSequence: Long): String = JSONObject()
        .put("type", "resume")
        .put("lastSequence", lastSequence.coerceAtLeast(0))
        .toString()

    fun pong(timestamp: Long): String = JSONObject()
        .put("type", "pong")
        .put("timestamp", timestamp)
        .toString()

    private fun parseHello(root: JSONObject): ProtocolParseResult {
        val protocolVersion = root.strictInt("protocolVersion")
            ?: return ProtocolParseResult.Invalid("invalid_hello")
        val deviceName = root.strictString("deviceName", 128)
            ?: return ProtocolParseResult.Invalid("invalid_hello")
        val deviceId = root.strictString("deviceId", 128)
            ?: return ProtocolParseResult.Invalid("invalid_hello")
        val latestSequence = root.strictLong("latestSequence")
            ?: return ProtocolParseResult.Invalid("invalid_hello")
        if (latestSequence < 0) return ProtocolParseResult.Invalid("invalid_hello")

        return ProtocolParseResult.Success(
            ServerMessage.Hello(
                protocolVersion = protocolVersion,
                serverVersion = root.strictString("serverVersion", 64).orEmpty(),
                deviceName = deviceName,
                deviceId = deviceId,
                latestSequence = latestSequence,
                serverTime = root.strictString("serverTime", 64).orEmpty(),
            ),
        )
    }

    private fun parseEvent(root: JSONObject): ProtocolParseResult {
        val payload = root.opt("payload") as? JSONObject
            ?: return ProtocolParseResult.Invalid("invalid_event")
        val eventId = payload.strictString("eventId", 256)
            ?: return ProtocolParseResult.Invalid("invalid_event")
        val agent = payload.strictString("agent", 32)
            ?: return ProtocolParseResult.Invalid("invalid_event")
        val status = payload.strictString("status", 32)
            ?: return ProtocolParseResult.Invalid("invalid_event")
        val title = payload.strictString("title", 256)
            ?: return ProtocolParseResult.Invalid("invalid_event")
        val occurredAt = payload.strictString("occurredAt", 64)
            ?: return ProtocolParseResult.Invalid("invalid_event")
        val sequence = payload.strictLong("sequence")
            ?: return ProtocolParseResult.Invalid("invalid_event")
        if (sequence <= 0) return ProtocolParseResult.Invalid("invalid_event")

        return ProtocolParseResult.Success(
            ServerMessage.Event(
                AgentEvent(
                    eventId = eventId,
                    agent = agent,
                    status = status,
                    title = title,
                    project = payload.optionalString("project", 256),
                    summary = payload.optionalString("summary", 1024),
                    occurredAt = occurredAt,
                    sequence = sequence,
                ),
            ),
        )
    }

    private fun parsePing(root: JSONObject): ProtocolParseResult {
        val timestamp = root.strictLong("timestamp")
            ?: return ProtocolParseResult.Invalid("invalid_ping")
        return ProtocolParseResult.Success(ServerMessage.Ping(timestamp))
    }

    private fun parseError(root: JSONObject): ProtocolParseResult {
        val code = root.strictString("code", 64)
            ?: return ProtocolParseResult.Invalid("invalid_error")
        return ProtocolParseResult.Success(ServerMessage.Error(code))
    }

    private fun JSONObject.strictString(name: String, maximumLength: Int): String? {
        val value = opt(name) as? String ?: return null
        val trimmed = value.trim()
        return trimmed.takeIf { it.isNotEmpty() && it.length <= maximumLength }
    }

    private fun JSONObject.optionalString(name: String, maximumLength: Int): String? {
        if (!has(name) || isNull(name)) return null
        return strictString(name, maximumLength)
    }

    private fun JSONObject.strictLong(name: String): Long? = when (val value = opt(name)) {
        is Byte -> value.toLong()
        is Short -> value.toLong()
        is Int -> value.toLong()
        is Long -> value
        else -> null
    }

    private fun JSONObject.strictInt(name: String): Int? {
        val value = strictLong(name) ?: return null
        return value.takeIf { it in Int.MIN_VALUE..Int.MAX_VALUE }?.toInt()
    }
}
