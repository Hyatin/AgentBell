package com.hyatin.agentbell.pairing

import com.hyatin.agentbell.protocol.AgentBellProtocol
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONException
import org.json.JSONObject
import java.util.concurrent.TimeUnit

data class DesktopStatus(
    val protocolVersion: Int,
    val serverVersion: String,
    val deviceName: String,
    val deviceId: String,
    val lanAddress: String,
    val lanPort: Int,
    val webSocketPath: String,
    val latestSequence: Long,
)

sealed interface StatusFetchResult {
    data class Success(val status: DesktopStatus) : StatusFetchResult
    data object Unauthorized : StatusFetchResult
    data class Failure(val code: String) : StatusFetchResult
}

interface DesktopStatusTransport {
    suspend fun fetch(candidate: PairingCandidate): StatusFetchResult
}

class OkHttpDesktopStatusTransport(
    baseClient: OkHttpClient = OkHttpClient(),
) : DesktopStatusTransport {
    private val client = baseClient.newBuilder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(5, TimeUnit.SECONDS)
        .callTimeout(8, TimeUnit.SECONDS)
        .build()

    override suspend fun fetch(candidate: PairingCandidate): StatusFetchResult =
        withContext(Dispatchers.IO) {
            val request = Request.Builder()
                .url("http://${candidate.host}:${candidate.port}/api/v1/status")
                .header("Authorization", "Bearer ${candidate.token}")
                .header("Cache-Control", "no-store")
                .get()
                .build()
            try {
                client.newCall(request).execute().use { response ->
                    if (response.code == 401 || response.code == 403) {
                        return@withContext StatusFetchResult.Unauthorized
                    }
                    if (!response.isSuccessful) {
                        return@withContext StatusFetchResult.Failure("status_http_error")
                    }
                    val body = response.body ?: return@withContext StatusFetchResult.Failure("status_empty")
                    val source = body.source()
                    source.request(MAX_STATUS_BYTES + 1L)
                    if (source.buffer.size > MAX_STATUS_BYTES) {
                        return@withContext StatusFetchResult.Failure("status_too_large")
                    }
                    parseStatus(source.readUtf8())
                }
            } catch (_: Exception) {
                StatusFetchResult.Failure("status_unavailable")
            }
        }

    private fun parseStatus(json: String): StatusFetchResult {
        val root = try {
            JSONObject(json)
        } catch (_: JSONException) {
            return StatusFetchResult.Failure("status_invalid_json")
        }

        fun string(name: String, max: Int): String? {
            val value = root.opt(name) as? String ?: return null
            return value.trim().takeIf { it.isNotEmpty() && it.length <= max }
        }
        fun long(name: String): Long? = when (val value = root.opt(name)) {
            is Int -> value.toLong()
            is Long -> value
            else -> null
        }

        val protocol = long("protocolVersion")?.toInt()
            ?: return StatusFetchResult.Failure("status_invalid")
        val port = long("lanPort")?.toInt()
            ?: return StatusFetchResult.Failure("status_invalid")
        val latestSequence = long("latestSequence")
            ?: return StatusFetchResult.Failure("status_invalid")
        return StatusFetchResult.Success(
            DesktopStatus(
                protocolVersion = protocol,
                serverVersion = string("serverVersion", 64).orEmpty(),
                deviceName = string("deviceName", 128)
                    ?: return StatusFetchResult.Failure("status_invalid"),
                deviceId = string("deviceId", 128)
                    ?: return StatusFetchResult.Failure("status_invalid"),
                lanAddress = string("lanAddress", 64)
                    ?: return StatusFetchResult.Failure("status_invalid"),
                lanPort = port,
                webSocketPath = string("webSocketPath", 128)
                    ?: return StatusFetchResult.Failure("status_invalid"),
                latestSequence = latestSequence,
            ),
        )
    }

    private companion object {
        const val MAX_STATUS_BYTES = 64 * 1024L
    }
}

sealed interface PairingValidationResult {
    data class Success(val credential: com.hyatin.agentbell.storage.PairingCredential) : PairingValidationResult
    data class Failure(val code: String) : PairingValidationResult
}

class PairingValidator(
    private val transport: DesktopStatusTransport,
    private val now: () -> String = { java.time.Instant.now().toString() },
) {
    suspend fun validate(pairingUrl: String): PairingValidationResult {
        val candidate = when (val parsed = PairingUrlParser.parse(pairingUrl)) {
            is PairingUrlResult.Success -> parsed.candidate
            is PairingUrlResult.Failure -> return PairingValidationResult.Failure(
                "pairing_${parsed.code.name.lowercase()}",
            )
        }

        val status = when (val fetched = transport.fetch(candidate)) {
            is StatusFetchResult.Success -> fetched.status
            StatusFetchResult.Unauthorized -> return PairingValidationResult.Failure("unauthorized")
            is StatusFetchResult.Failure -> return PairingValidationResult.Failure(fetched.code)
        }
        if (status.protocolVersion != AgentBellProtocol.VERSION) {
            return PairingValidationResult.Failure("protocol_mismatch")
        }
        if (status.lanAddress != candidate.host || !PrivateIpv4.isPrivate(status.lanAddress)) {
            return PairingValidationResult.Failure("status_address_mismatch")
        }
        if (status.lanPort != candidate.port) {
            return PairingValidationResult.Failure("status_port_mismatch")
        }
        if (status.webSocketPath != AgentBellProtocol.WEB_SOCKET_PATH) {
            return PairingValidationResult.Failure("status_websocket_path_invalid")
        }
        if (status.deviceId.isBlank() || status.deviceName.isBlank() || status.latestSequence < 0) {
            return PairingValidationResult.Failure("status_invalid")
        }

        val timestamp = now()
        return PairingValidationResult.Success(
            com.hyatin.agentbell.storage.PairingCredential(
                deviceId = status.deviceId,
                deviceName = status.deviceName,
                host = candidate.host,
                port = candidate.port,
                token = candidate.token,
                protocolVersion = status.protocolVersion,
                webSocketPath = status.webSocketPath,
                lastSequence = 0,
                pairedAt = timestamp,
                updatedAt = timestamp,
                continuousReceiving = false,
            ),
        )
    }
}
