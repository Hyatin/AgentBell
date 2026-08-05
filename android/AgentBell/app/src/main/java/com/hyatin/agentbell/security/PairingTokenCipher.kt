package com.hyatin.agentbell.security

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

data class CipherEnvelope(val initializationVector: ByteArray, val ciphertext: ByteArray)

interface PairingTokenCipher {
    fun encrypt(plaintext: ByteArray): CipherEnvelope
    fun decrypt(envelope: CipherEnvelope): ByteArray
    fun deleteKey()
}

class AndroidKeystorePairingTokenCipher : PairingTokenCipher {
    override fun encrypt(plaintext: ByteArray): CipherEnvelope {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        cipher.updateAAD(ASSOCIATED_DATA)
        return CipherEnvelope(cipher.iv.copyOf(), cipher.doFinal(plaintext))
    }

    override fun decrypt(envelope: CipherEnvelope): ByteArray {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(
            Cipher.DECRYPT_MODE,
            getExistingKey() ?: throw IllegalStateException("pairing_key_unavailable"),
            GCMParameterSpec(128, envelope.initializationVector),
        )
        cipher.updateAAD(ASSOCIATED_DATA)
        return cipher.doFinal(envelope.ciphertext)
    }

    override fun deleteKey() {
        val keyStore = loadKeyStore()
        if (keyStore.containsAlias(KEY_ALIAS)) keyStore.deleteEntry(KEY_ALIAS)
    }

    private fun getOrCreateKey(): SecretKey = synchronized(keyLock) {
        getExistingKey() ?: KeyGenerator.getInstance(
            KeyProperties.KEY_ALGORITHM_AES,
            ANDROID_KEYSTORE,
        ).run {
            init(
                KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                )
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .setRandomizedEncryptionRequired(true)
                    .build(),
            )
            generateKey()
        }
    }

    private fun getExistingKey(): SecretKey? =
        loadKeyStore().getKey(KEY_ALIAS, null) as? SecretKey

    private fun loadKeyStore(): KeyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply {
        load(null)
    }

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "agentbell_pairing_token_v1"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        val ASSOCIATED_DATA = "AgentBell.PairingToken.v1".toByteArray(Charsets.UTF_8)
        val keyLock = Any()
    }
}
