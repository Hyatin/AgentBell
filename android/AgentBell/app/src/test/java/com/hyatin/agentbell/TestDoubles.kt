package com.hyatin.agentbell

import com.hyatin.agentbell.connection.CompletionNotificationSink
import com.hyatin.agentbell.protocol.AgentEvent
import com.hyatin.agentbell.security.CipherEnvelope
import com.hyatin.agentbell.security.PairingTokenCipher
import com.hyatin.agentbell.storage.CredentialMetadataStorage
import com.hyatin.agentbell.storage.EventStateStorage
import com.hyatin.agentbell.storage.PairingCredential
import com.hyatin.agentbell.storage.PairingCredentialLoadResult
import com.hyatin.agentbell.storage.PairingCredentialStore
import com.hyatin.agentbell.storage.StoredEventState
import com.hyatin.agentbell.storage.StoredPairingCredential

class FakeTokenCipher : PairingTokenCipher {
    var failDecrypt = false
    var deleted = false

    override fun encrypt(plaintext: ByteArray): CipherEnvelope = CipherEnvelope(
        initializationVector = ByteArray(12) { 7 },
        ciphertext = plaintext.map { (it.toInt() xor 0x5a).toByte() }.toByteArray(),
    )

    override fun decrypt(envelope: CipherEnvelope): ByteArray {
        if (failDecrypt) error("test decrypt failure")
        return envelope.ciphertext.map { (it.toInt() xor 0x5a).toByte() }.toByteArray()
    }

    override fun deleteKey() {
        deleted = true
    }
}

class InMemoryCredentialMetadataStorage : CredentialMetadataStorage {
    var value: StoredPairingCredential? = null

    override suspend fun read(): StoredPairingCredential? = value
    override suspend fun write(value: StoredPairingCredential) {
        this.value = value
    }
    override suspend fun clear() {
        value = null
    }
    override suspend fun updateLastSequence(value: Long, updatedAt: String) {
        this.value = this.value?.copy(lastSequence = maxOf(this.value!!.lastSequence, value), updatedAt = updatedAt)
    }
    override suspend fun updateContinuousReceiving(value: Boolean, updatedAt: String) {
        this.value = this.value?.copy(continuousReceiving = value, updatedAt = updatedAt)
    }
}

class InMemoryPairingCredentialStore(
    var credential: PairingCredential = testCredential(),
) : PairingCredentialStore {
    var cleared = false
    var lastSequence = credential.lastSequence

    override suspend fun load(): PairingCredentialLoadResult =
        if (cleared) PairingCredentialLoadResult.Unpaired
        else PairingCredentialLoadResult.Available(credential)

    override suspend fun save(credential: PairingCredential) {
        this.credential = credential
        cleared = false
    }

    override suspend fun updateLastSequence(sequence: Long) {
        lastSequence = maxOf(lastSequence, sequence)
    }

    override suspend fun updateContinuousReceiving(enabled: Boolean) {
        credential = credential.withContinuousReceiving(enabled, "2026-08-03T00:00:00Z")
    }

    override suspend fun clear() {
        cleared = true
    }
}

class InMemoryEventStateStorage(
    var state: StoredEventState = StoredEventState(emptyList(), emptyList(), 0),
) : EventStateStorage {
    var failWrites = false
    var writeCount = 0

    override suspend fun read(): StoredEventState = state
    override suspend fun write(value: StoredEventState) {
        writeCount++
        if (failWrites) error("test write failure")
        state = value
    }
    override suspend fun clear() {
        state = StoredEventState(emptyList(), emptyList(), 0)
    }
}

class CollectingNotificationSink : CompletionNotificationSink {
    val events = mutableListOf<AgentEvent>()
    var allowed = true

    override fun post(event: AgentEvent): Boolean {
        if (allowed) events += event
        return allowed
    }
}

fun testCredential(
    host: String = "192.168.1.20",
    port: Int = 17864,
    token: String = "A".repeat(43),
    lastSequence: Long = 0,
) = PairingCredential(
    deviceId = "device-id",
    deviceName = "测试电脑",
    host = host,
    port = port,
    token = token,
    protocolVersion = 1,
    webSocketPath = "/ws/v1/events",
    lastSequence = lastSequence,
    pairedAt = "2026-08-03T00:00:00Z",
    updatedAt = "2026-08-03T00:00:00Z",
    continuousReceiving = true,
)

fun testEvent(id: String, sequence: Long, summary: String? = "完成 🔔") = AgentEvent(
    eventId = id,
    agent = "codex",
    status = "completed",
    title = "Codex 已完成当前回合",
    project = "AgentBell",
    summary = summary,
    occurredAt = "2026-08-03T00:00:00Z",
    sequence = sequence,
)
