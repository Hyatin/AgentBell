package com.hyatin.agentbell.connection

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import java.io.IOException

class PrivateLanRequestGuardTest {
    private val client = OkHttpClient.Builder()
        .addInterceptor(PrivateLanRequestGuard())
        .addInterceptor { chain ->
            okhttp3.Response.Builder()
                .request(chain.request())
                .protocol(okhttp3.Protocol.HTTP_1_1)
                .code(200)
                .message("OK")
                .body("{}".toResponseBody())
                .build()
        }
        .build()

    @Test fun permitsOnlyKnownAgentBellPrivateEndpoint() {
        val response = client.newCall(
            Request.Builder().url("http://192.168.1.20:17864/api/v1/status").build(),
        ).execute()
        assertEquals(200, response.use { it.code })
    }

    @Test fun rejectsPublicHost() = assertRejected("http://8.8.8.8:17864/api/v1/status")

    @Test fun rejectsLoopback() = assertRejected("http://127.0.0.1:17864/api/v1/status")

    @Test fun rejectsUnknownPath() = assertRejected("http://192.168.1.20:17864/other")

    @Test fun rejectsTokenQuery() =
        assertRejected("http://192.168.1.20:17864/ws/v1/events?access_token=secret")

    private fun assertRejected(url: String) {
        assertThrows(IOException::class.java) {
            client.newCall(Request.Builder().url(url).build()).execute()
        }
    }
}
