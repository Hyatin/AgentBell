package com.hyatin.agentbell.security

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertFalse
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class AndroidKeystorePairingTokenCipherTest {
    private val cipher = AndroidKeystorePairingTokenCipher()

    @After fun cleanUp() {
        cipher.deleteKey()
    }

    @Test fun androidKeystoreEncryptsAndRestoresWithoutPlaintextCiphertext() {
        cipher.deleteKey()
        val plaintext = "A".repeat(43).toByteArray(Charsets.UTF_8)
        val envelope = cipher.encrypt(plaintext)
        assertFalse(envelope.ciphertext.contentEquals(plaintext))
        assertArrayEquals(plaintext, cipher.decrypt(envelope))
        plaintext.fill(0)
    }
}
