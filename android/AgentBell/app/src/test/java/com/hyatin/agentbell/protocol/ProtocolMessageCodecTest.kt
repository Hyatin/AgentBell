package com.hyatin.agentbell.protocol

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ProtocolMessageCodecTest {
    @Test fun parsesHelloAndIgnoresUnknownFields() {
        val result = ProtocolMessageCodec.parseServerMessage(
            """{"type":"hello","protocolVersion":1,"serverVersion":"0.2.0","deviceName":"电脑🔔","deviceId":"device","latestSequence":7,"serverTime":"2026-08-03T00:00:00Z","future":true}""",
        )
        val hello = (result as ProtocolParseResult.Success).message as ServerMessage.Hello
        assertEquals(1, hello.protocolVersion)
        assertEquals("电脑🔔", hello.deviceName)
        assertEquals(7, hello.latestSequence)
    }

    @Test fun parsesChineseEmojiEvent() {
        val result = ProtocolMessageCodec.parseServerMessage(eventJson(8, "中文完成 👩🏽‍💻"))
        val event = ((result as ProtocolParseResult.Success).message as ServerMessage.Event).payload
        assertEquals("中文完成 👩🏽‍💻", event.summary)
        assertEquals(8, event.sequence)
    }

    @Test fun parsesPingAndCreatesExactPongMeaning() {
        val result = ProtocolMessageCodec.parseServerMessage("""{"type":"ping","timestamp":1234}""")
        assertEquals(1234, ((result as ProtocolParseResult.Success).message as ServerMessage.Ping).timestamp)
        assertEquals("{\"type\":\"pong\",\"timestamp\":1234}", ProtocolMessageCodec.pong(1234))
    }

    @Test fun parsesStableError() {
        val result = ProtocolMessageCodec.parseServerMessage("""{"type":"error","code":"server_busy"}""")
        assertEquals("server_busy", ((result as ProtocolParseResult.Success).message as ServerMessage.Error).code)
    }

    @Test fun resumeIncludesZeroWatermark() {
        val resume = org.json.JSONObject(ProtocolMessageCodec.resume(0))
        assertEquals("resume", resume.getString("type"))
        assertEquals(0, resume.getLong("lastSequence"))
    }

    @Test fun invalidJsonDoesNotBecomeEvent() {
        val result = ProtocolMessageCodec.parseServerMessage("{not-json")
        assertEquals("invalid_json", (result as ProtocolParseResult.Invalid).code)
    }

    @Test fun unknownTypeIsIgnored() {
        assertTrue(ProtocolMessageCodec.parseServerMessage("""{"type":"future"}""") is ProtocolParseResult.Ignored)
    }

    @Test fun oversizedMessageIsRejected() {
        val value = "x".repeat(AgentBellProtocol.MAX_MESSAGE_BYTES + 1)
        val result = ProtocolMessageCodec.parseServerMessage(value)
        assertEquals("message_too_large", (result as ProtocolParseResult.Invalid).code)
    }

    @Test fun invalidEventFieldsAreRejected() {
        val result = ProtocolMessageCodec.parseServerMessage("""{"type":"event","payload":{"sequence":1}}""")
        assertEquals("invalid_event", (result as ProtocolParseResult.Invalid).code)
    }

    private fun eventJson(sequence: Long, summary: String) =
        """{"type":"event","payload":{"eventId":"event-$sequence","agent":"codex","status":"completed","title":"Codex 已完成当前回合","project":"AgentBell","summary":"$summary","occurredAt":"2026-08-03T00:00:00Z","sequence":$sequence}}"""
}
