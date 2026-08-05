package com.hyatin.agentbell.pairing

import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class PairingValidatorTest {
    private val token = "A".repeat(43)
    private val url = "http://192.168.1.20:17864/pair#token=$token&device=PC&v=1"

    @Test fun validStatusCreatesCredentialWithoutChangingProtocol() = runTest {
        val result = validator(validStatus()).validate(url)
        val credential = (result as PairingValidationResult.Success).credential
        assertEquals("192.168.1.20", credential.host)
        assertEquals(17864, credential.port)
        assertEquals(token, credential.token)
        assertEquals("/ws/v1/events", credential.webSocketPath)
        assertTrue(!credential.toString().contains(token))
    }

    @Test fun unauthorizedDoesNotCreateCredential() = runTest {
        val result = PairingValidator(object : DesktopStatusTransport {
            override suspend fun fetch(candidate: PairingCandidate) = StatusFetchResult.Unauthorized
        }).validate(url)
        assertEquals("unauthorized", (result as PairingValidationResult.Failure).code)
    }

    @Test fun protocolMismatchFails() = runTest {
        val result = validator(validStatus().copy(protocolVersion = 2)).validate(url)
        assertEquals("protocol_mismatch", (result as PairingValidationResult.Failure).code)
    }

    @Test fun addressMismatchFails() = runTest {
        val result = validator(validStatus().copy(lanAddress = "192.168.1.21")).validate(url)
        assertEquals("status_address_mismatch", (result as PairingValidationResult.Failure).code)
    }

    @Test fun portMismatchFails() = runTest {
        val result = validator(validStatus().copy(lanPort = 17865)).validate(url)
        assertEquals("status_port_mismatch", (result as PairingValidationResult.Failure).code)
    }

    @Test fun unsafeWebSocketPathFails() = runTest {
        val result = validator(validStatus().copy(webSocketPath = "/other")).validate(url)
        assertEquals("status_websocket_path_invalid", (result as PairingValidationResult.Failure).code)
    }

    private fun validator(status: DesktopStatus) = PairingValidator(
        object : DesktopStatusTransport {
            override suspend fun fetch(candidate: PairingCandidate) = StatusFetchResult.Success(status)
        },
        now = { "2026-08-03T00:00:00Z" },
    )

    private fun validStatus() = DesktopStatus(
        protocolVersion = 1,
        serverVersion = "0.2.0",
        deviceName = "测试电脑",
        deviceId = "device-id",
        lanAddress = "192.168.1.20",
        lanPort = 17864,
        webSocketPath = "/ws/v1/events",
        latestSequence = 42,
    )
}
