package com.hyatin.agentbell.storage

import com.hyatin.agentbell.pairing.PrivateIpv4
import com.hyatin.agentbell.protocol.AgentBellProtocol
import com.hyatin.agentbell.security.CipherEnvelope
import com.hyatin.agentbell.security.PairingTokenCipher
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.nio.charset.StandardCharsets
import java.time.Instant
import java.util.Base64

class PairingCredential(
    val deviceId: String,
    val deviceName: String,
    val host: String,
    val port: Int,
    val token: String,
    val protocolVersion: Int,
    val webSocketPath: String,
    val lastSequence: Long,
    val pairedAt: String,
    val updatedAt: String,
    val continuousReceiving: Boolean,
) {
    fun withLastSequence(value: Long, updatedAt: String): PairingCredential = PairingCredential(
        deviceId,
        deviceName,
        host,
        port,
        token,
        protocolVersion,
        webSocketPath,
        maxOf(lastSequence, value),
        pairedAt,
        updatedAt,
        continuousReceiving,
    )

    fun withContinuousReceiving(value: Boolean, updatedAt: String): PairingCredential =
        PairingCredential(
            deviceId,
            deviceName,
            host,
            port,
            token,
            protocolVersion,
            webSocketPath,
            lastSequence,
            pairedAt,
            updatedAt,
            value,
        )

    override fun toString(): String =
        "PairingCredential(deviceId=<redacted>, deviceName=$deviceName, privateHost=true, " +
            "port=$port, token=<redacted>, protocolVersion=$protocolVersion, " +
            "lastSequence=$lastSequence, continuousReceiving=$continuousReceiving)"
}

data class StoredPairingCredential(
    val deviceId: String,
    val deviceName: String,
    val host: String,
    val port: Int,
    val encryptedToken: String,
    val tokenIv: String,
    val protocolVersion: Int,
    val webSocketPath: String,
    val lastSequence: Long,
    val pairedAt: String,
    val updatedAt: String,
    val continuousReceiving: Boolean,
) {
    override fun toString(): String =
        "StoredPairingCredential(deviceId=<redacted>, deviceName=$deviceName, " +
            "privateHost=true, port=$port, encryptedToken=<redacted>, tokenIv=<redacted>, " +
            "protocolVersion=$protocolVersion, lastSequence=$lastSequence, " +
            "continuousReceiving=$continuousReceiving)"
}

interface CredentialMetadataStorage {
    suspend fun read(): StoredPairingCredential?
    suspend fun write(value: StoredPairingCredential)
    suspend fun clear()
    suspend fun updateLastSequence(value: Long, updatedAt: String)
    suspend fun updateContinuousReceiving(value: Boolean, updatedAt: String)
}

sealed interface PairingCredentialLoadResult {
    data class Available(val credential: PairingCredential) : PairingCredentialLoadResult
    data object Unpaired : PairingCredentialLoadResult
    data object DecryptionFailed : PairingCredentialLoadResult
}

interface PairingCredentialStore {
    suspend fun load(): PairingCredentialLoadResult
    suspend fun save(credential: PairingCredential)
    suspend fun updateLastSequence(sequence: Long)
    suspend fun updateContinuousReceiving(enabled: Boolean)
    suspend fun clear()
}

class SecurePairingCredentialStore(
    private val metadataStorage: CredentialMetadataStorage,
    private val cipher: PairingTokenCipher,
    private val now: () -> String = { Instant.now().toString() },
) : PairingCredentialStore {
    private val mutex = Mutex()

    override suspend fun load(): PairingCredentialLoadResult = mutex.withLock {
        val stored = metadataStorage.read() ?: return@withLock PairingCredentialLoadResult.Unpaired
        if (!isValid(stored)) return@withLock PairingCredentialLoadResult.Unpaired

        val encrypted = try {
            Base64.getDecoder().decode(stored.encryptedToken)
        } catch (_: IllegalArgumentException) {
            return@withLock PairingCredentialLoadResult.DecryptionFailed
        }
        val iv = try {
            Base64.getDecoder().decode(stored.tokenIv)
        } catch (_: IllegalArgumentException) {
            encrypted.fill(0)
            return@withLock PairingCredentialLoadResult.DecryptionFailed
        }

        val plaintext = try {
            cipher.decrypt(CipherEnvelope(iv, encrypted))
        } catch (_: Exception) {
            encrypted.fill(0)
            iv.fill(0)
            return@withLock PairingCredentialLoadResult.DecryptionFailed
        }
        return@withLock try {
            val token = String(plaintext, StandardCharsets.UTF_8)
            if (!TOKEN_PATTERN.matches(token)) {
                PairingCredentialLoadResult.DecryptionFailed
            } else {
                PairingCredentialLoadResult.Available(stored.toCredential(token))
            }
        } finally {
            plaintext.fill(0)
            encrypted.fill(0)
            iv.fill(0)
        }
    }

    override suspend fun save(credential: PairingCredential) = mutex.withLock {
        require(isValid(credential)) { "Invalid pairing credential." }
        val plaintext = credential.token.toByteArray(StandardCharsets.UTF_8)
        val envelope = try {
            cipher.encrypt(plaintext)
        } finally {
            plaintext.fill(0)
        }
        try {
            metadataStorage.write(
                StoredPairingCredential(
                    deviceId = credential.deviceId,
                    deviceName = credential.deviceName,
                    host = credential.host,
                    port = credential.port,
                    encryptedToken = Base64.getEncoder().encodeToString(envelope.ciphertext),
                    tokenIv = Base64.getEncoder().encodeToString(envelope.initializationVector),
                    protocolVersion = credential.protocolVersion,
                    webSocketPath = credential.webSocketPath,
                    lastSequence = credential.lastSequence.coerceAtLeast(0),
                    pairedAt = credential.pairedAt,
                    updatedAt = credential.updatedAt,
                    continuousReceiving = credential.continuousReceiving,
                ),
            )
        } finally {
            envelope.ciphertext.fill(0)
            envelope.initializationVector.fill(0)
        }
    }

    override suspend fun updateLastSequence(sequence: Long) = mutex.withLock {
        metadataStorage.updateLastSequence(sequence.coerceAtLeast(0), now())
    }

    override suspend fun updateContinuousReceiving(enabled: Boolean) = mutex.withLock {
        metadataStorage.updateContinuousReceiving(enabled, now())
    }

    override suspend fun clear() = mutex.withLock {
        metadataStorage.clear()
        try {
            cipher.deleteKey()
        } catch (_: Exception) {
            // Metadata is already gone. A stale non-exportable key contains no credential.
        }
    }

    private fun isValid(value: StoredPairingCredential): Boolean =
        value.deviceId.isNotBlank() &&
            value.deviceName.isNotBlank() &&
            PrivateIpv4.isPrivate(value.host) &&
            value.port in 17864..17874 &&
            value.protocolVersion == AgentBellProtocol.VERSION &&
            value.webSocketPath == AgentBellProtocol.WEB_SOCKET_PATH &&
            value.lastSequence >= 0 &&
            value.encryptedToken.isNotBlank() &&
            value.tokenIv.isNotBlank()

    private fun isValid(value: PairingCredential): Boolean =
        value.deviceId.isNotBlank() &&
            value.deviceName.isNotBlank() &&
            PrivateIpv4.isPrivate(value.host) &&
            value.port in 17864..17874 &&
            TOKEN_PATTERN.matches(value.token) &&
            value.protocolVersion == AgentBellProtocol.VERSION &&
            value.webSocketPath == AgentBellProtocol.WEB_SOCKET_PATH &&
            value.lastSequence >= 0

    private fun StoredPairingCredential.toCredential(token: String) = PairingCredential(
        deviceId,
        deviceName,
        host,
        port,
        token,
        protocolVersion,
        webSocketPath,
        lastSequence,
        pairedAt,
        updatedAt,
        continuousReceiving,
    )

    private companion object {
        val TOKEN_PATTERN = Regex("^[A-Za-z0-9_-]{43}$")
    }
}
