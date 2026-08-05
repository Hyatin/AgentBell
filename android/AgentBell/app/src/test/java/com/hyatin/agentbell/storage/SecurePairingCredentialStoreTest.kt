package com.hyatin.agentbell.storage

import com.hyatin.agentbell.FakeTokenCipher
import com.hyatin.agentbell.InMemoryCredentialMetadataStorage
import com.hyatin.agentbell.testCredential
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SecurePairingCredentialStoreTest {
    @Test fun encryptsAndRestoresTokenWithoutPlaintextMetadata() = runTest {
        val metadata = InMemoryCredentialMetadataStorage()
        val store = SecurePairingCredentialStore(metadata, FakeTokenCipher())
        val credential = testCredential()

        store.save(credential)

        val persisted = requireNotNull(metadata.value)
        assertFalse(persisted.encryptedToken.contains(credential.token))
        assertFalse(persisted.toString().contains(credential.token))
        val restored = (store.load() as PairingCredentialLoadResult.Available).credential
        assertEquals(credential.token, restored.token)
    }

    @Test fun decryptionFailureRequiresRepair() = runTest {
        val metadata = InMemoryCredentialMetadataStorage()
        val cipher = FakeTokenCipher()
        val store = SecurePairingCredentialStore(metadata, cipher)
        store.save(testCredential())
        cipher.failDecrypt = true

        assertTrue(store.load() is PairingCredentialLoadResult.DecryptionFailed)
    }

    @Test fun clearRemovesMetadataAndKeystoreAlias() = runTest {
        val metadata = InMemoryCredentialMetadataStorage()
        val cipher = FakeTokenCipher()
        val store = SecurePairingCredentialStore(metadata, cipher)
        store.save(testCredential())

        store.clear()

        assertNull(metadata.value)
        assertTrue(cipher.deleted)
        assertTrue(store.load() is PairingCredentialLoadResult.Unpaired)
    }

    @Test fun updatesSequenceMonotonically() = runTest {
        val metadata = InMemoryCredentialMetadataStorage()
        val store = SecurePairingCredentialStore(metadata, FakeTokenCipher())
        store.save(testCredential(lastSequence = 5))
        store.updateLastSequence(9)
        store.updateLastSequence(3)
        assertEquals(9L, metadata.value?.lastSequence)
    }
}
