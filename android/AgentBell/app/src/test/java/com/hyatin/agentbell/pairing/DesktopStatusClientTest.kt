package com.hyatin.agentbell.pairing

import kotlinx.coroutines.test.runTest
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.TimeUnit

class DesktopStatusClientTest {
    @Test fun statusUsesBearerHeaderAndNeverPlacesTokenInQuery() = runTest {
        val server = MockWebServer()
        server.enqueue(
            MockResponse().setBody(
                """{"protocolVersion":1,"serverVersion":"0.2.0","deviceName":"电脑🔔","deviceId":"device","lanAddress":"192.168.1.20","lanPort":17864,"webSocketPath":"/ws/v1/events","latestSequence":7}""",
            ),
        )
        server.start()
        try {
            val token = "T".repeat(43)
            val candidate = PairingCandidate(server.hostName, server.port, token, null, 1)
            val result = OkHttpDesktopStatusTransport(OkHttpClient()).fetch(candidate)

            assertTrue(result is StatusFetchResult.Success)
            val request = server.takeRequest(3, TimeUnit.SECONDS)!!
            assertEquals("Bearer $token", request.getHeader("Authorization"))
            assertEquals("/api/v1/status", request.requestUrl?.encodedPath)
            assertNull(request.requestUrl?.query)
        } finally {
            server.shutdown()
        }
    }

    @Test fun unauthorizedAndOversizedResponsesUseStableFailures() = runTest {
        val server = MockWebServer()
        server.enqueue(MockResponse().setResponseCode(403))
        server.enqueue(MockResponse().setBody("x".repeat(64 * 1024 + 1)))
        server.start()
        try {
            val candidate = PairingCandidate(server.hostName, server.port, "T".repeat(43), null, 1)
            val transport = OkHttpDesktopStatusTransport(OkHttpClient())
            assertTrue(transport.fetch(candidate) is StatusFetchResult.Unauthorized)
            assertEquals(
                "status_too_large",
                (transport.fetch(candidate) as StatusFetchResult.Failure).code,
            )
        } finally {
            server.shutdown()
        }
    }
}
