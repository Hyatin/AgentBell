package com.hyatin.agentbell.pairing

import java.net.URI
import java.net.URLDecoder
import java.nio.charset.StandardCharsets

class PairingCandidate(
    val host: String,
    val port: Int,
    val token: String,
    val deviceName: String?,
    val protocolVersion: Int,
) {
    override fun toString(): String =
        "PairingCandidate(privateHost=true, port=$port, token=<redacted>, protocolVersion=$protocolVersion)"
}

sealed interface PairingUrlResult {
    data class Success(val candidate: PairingCandidate) : PairingUrlResult
    data class Failure(val code: PairingUrlError) : PairingUrlResult
}

enum class PairingUrlError {
    INVALID_URL,
    INVALID_SCHEME,
    INVALID_HOST,
    INVALID_PORT,
    INVALID_PATH,
    QUERY_NOT_ALLOWED,
    MISSING_TOKEN,
    INVALID_TOKEN,
    UNSUPPORTED_VERSION,
}

object PairingUrlParser {
    private val tokenPattern = Regex("^[A-Za-z0-9_-]{43}$")

    fun parse(value: String): PairingUrlResult {
        if (value.isBlank() || value.length > 2048) return failure(PairingUrlError.INVALID_URL)
        val uri = try {
            URI(value.trim())
        } catch (_: Exception) {
            return failure(PairingUrlError.INVALID_URL)
        }

        if (!uri.scheme.equals("http", ignoreCase = true)) {
            return failure(PairingUrlError.INVALID_SCHEME)
        }
        if (uri.rawUserInfo != null) return failure(PairingUrlError.INVALID_URL)
        if (!uri.rawQuery.isNullOrEmpty()) return failure(PairingUrlError.QUERY_NOT_ALLOWED)
        if (uri.path != "/pair") return failure(PairingUrlError.INVALID_PATH)

        val host = uri.host?.lowercase() ?: return failure(PairingUrlError.INVALID_HOST)
        if (!PrivateIpv4.isPrivate(host)) return failure(PairingUrlError.INVALID_HOST)
        val port = uri.port
        if (port !in 17864..17874) return failure(PairingUrlError.INVALID_PORT)

        val fragment = parseFragment(uri.rawFragment)
            ?: return failure(PairingUrlError.INVALID_URL)
        val token = fragment["token"] ?: return failure(PairingUrlError.MISSING_TOKEN)
        if (!tokenPattern.matches(token)) return failure(PairingUrlError.INVALID_TOKEN)
        val version = fragment["v"]?.toIntOrNull()
        if (version != 1) return failure(PairingUrlError.UNSUPPORTED_VERSION)

        return PairingUrlResult.Success(
            PairingCandidate(
                host = host,
                port = port,
                token = token,
                deviceName = fragment["device"]?.trim()?.take(128)?.ifBlank { null },
                protocolVersion = version,
            ),
        )
    }

    private fun parseFragment(rawFragment: String?): Map<String, String>? {
        if (rawFragment.isNullOrBlank()) return emptyMap()
        val result = linkedMapOf<String, String>()
        for (part in rawFragment.split('&')) {
            val separator = part.indexOf('=')
            if (separator <= 0) return null
            val key = decode(part.substring(0, separator)) ?: return null
            val value = decode(part.substring(separator + 1)) ?: return null
            if (key in result) return null
            result[key] = value
        }
        return result
    }

    private fun decode(value: String): String? = try {
        URLDecoder.decode(value, StandardCharsets.UTF_8.name())
    } catch (_: IllegalArgumentException) {
        null
    }

    private fun failure(code: PairingUrlError) = PairingUrlResult.Failure(code)
}

object PrivateIpv4 {
    fun isPrivate(host: String): Boolean {
        val parts = host.split('.')
        if (parts.size != 4) return false
        val octets = parts.mapIndexed { index, part ->
            if (part.isEmpty() || (part.length > 1 && part.startsWith('0'))) return false
            val value = part.toIntOrNull() ?: return false
            if (value !in 0..255) return false
            if (index == 0 && value == 0) return false
            value
        }
        return octets[0] == 10 ||
            (octets[0] == 172 && octets[1] in 16..31) ||
            (octets[0] == 192 && octets[1] == 168)
    }

    fun masked(host: String): String {
        val parts = host.split('.')
        return if (parts.size == 4) "${parts[0]}.${parts[1]}.*.*" else "private-address"
    }
}
