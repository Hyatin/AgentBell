package com.hyatin.agentbell.pairing

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.URLEncoder
import java.nio.charset.StandardCharsets

class PairingUrlParserTest {
    private val token = "A".repeat(43)

    @Test fun parses192Address() = assertSuccess("192.168.1.20")
    @Test fun parses10Address() = assertSuccess("10.1.2.3")
    @Test fun parses172_16Address() = assertSuccess("172.16.0.1")
    @Test fun parses172_31Address() = assertSuccess("172.31.255.254")

    @Test fun rejects172_15() = assertFailure("http://172.15.0.1:17864/pair#token=$token&v=1", PairingUrlError.INVALID_HOST)
    @Test fun rejects172_32() = assertFailure("http://172.32.0.1:17864/pair#token=$token&v=1", PairingUrlError.INVALID_HOST)
    @Test fun rejectsLoopback() = assertFailure("http://127.0.0.1:17864/pair#token=$token&v=1", PairingUrlError.INVALID_HOST)
    @Test fun rejectsApipa() = assertFailure("http://169.254.1.2:17864/pair#token=$token&v=1", PairingUrlError.INVALID_HOST)
    @Test fun rejectsPublicAddress() = assertFailure("http://8.8.8.8:17864/pair#token=$token&v=1", PairingUrlError.INVALID_HOST)
    @Test fun rejectsMissingToken() = assertFailure("http://192.168.1.2:17864/pair#v=1", PairingUrlError.MISSING_TOKEN)
    @Test fun rejectsTokenInQuery() = assertFailure("http://192.168.1.2:17864/pair?token=$token#v=1", PairingUrlError.QUERY_NOT_ALLOWED)
    @Test fun rejectsWrongPath() = assertFailure("http://192.168.1.2:17864/other#token=$token&v=1", PairingUrlError.INVALID_PATH)
    @Test fun rejectsHttps() = assertFailure("https://192.168.1.2:17864/pair#token=$token&v=1", PairingUrlError.INVALID_SCHEME)
    @Test fun rejectsUnsupportedVersion() = assertFailure("http://192.168.1.2:17864/pair#token=$token&v=2", PairingUrlError.UNSUPPORTED_VERSION)
    @Test fun rejectsInvalidPort() = assertFailure("http://192.168.1.2:65535/pair#token=$token&v=1", PairingUrlError.INVALID_PORT)

    @Test fun preservesChineseDeviceName() {
        val encoded = URLEncoder.encode("开发电脑 🔔", StandardCharsets.UTF_8.name()).replace("+", "%20")
        val result = PairingUrlParser.parse(
            "http://192.168.1.2:17864/pair#token=$token&device=$encoded&v=1",
        )
        val candidate = (result as PairingUrlResult.Success).candidate
        assertEquals("开发电脑 🔔", candidate.deviceName)
        assertTrue(candidate.toString().contains("<redacted>"))
        assertTrue(!candidate.toString().contains(token))
    }

    private fun assertSuccess(host: String) {
        val result = PairingUrlParser.parse(
            "http://$host:17864/pair#token=$token&device=PC&v=1",
        )
        assertTrue(result is PairingUrlResult.Success)
        assertEquals(host, (result as PairingUrlResult.Success).candidate.host)
    }

    private fun assertFailure(url: String, expected: PairingUrlError) {
        val result = PairingUrlParser.parse(url)
        assertEquals(expected, (result as PairingUrlResult.Failure).code)
    }
}
