package com.hyatin.agentbell.storage

import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import kotlinx.coroutines.flow.first

class DataStoreCredentialMetadataStorage(
    private val dataStore: DataStore<Preferences>,
) : CredentialMetadataStorage {
    override suspend fun read(): StoredPairingCredential? {
        val values = dataStore.data.first()
        return StoredPairingCredential(
            deviceId = values[Keys.DEVICE_ID] ?: return null,
            deviceName = values[Keys.DEVICE_NAME] ?: return null,
            host = values[Keys.HOST] ?: return null,
            port = values[Keys.PORT] ?: return null,
            encryptedToken = values[Keys.ENCRYPTED_TOKEN] ?: return null,
            tokenIv = values[Keys.TOKEN_IV] ?: return null,
            protocolVersion = values[Keys.PROTOCOL_VERSION] ?: return null,
            webSocketPath = values[Keys.WEB_SOCKET_PATH] ?: return null,
            lastSequence = values[Keys.LAST_SEQUENCE] ?: 0,
            pairedAt = values[Keys.PAIRED_AT] ?: return null,
            updatedAt = values[Keys.UPDATED_AT] ?: return null,
            continuousReceiving = values[Keys.CONTINUOUS_RECEIVING] ?: false,
        )
    }

    override suspend fun write(value: StoredPairingCredential) {
        dataStore.edit { values ->
            values.clear()
            values[Keys.DEVICE_ID] = value.deviceId
            values[Keys.DEVICE_NAME] = value.deviceName
            values[Keys.HOST] = value.host
            values[Keys.PORT] = value.port
            values[Keys.ENCRYPTED_TOKEN] = value.encryptedToken
            values[Keys.TOKEN_IV] = value.tokenIv
            values[Keys.PROTOCOL_VERSION] = value.protocolVersion
            values[Keys.WEB_SOCKET_PATH] = value.webSocketPath
            values[Keys.LAST_SEQUENCE] = value.lastSequence
            values[Keys.PAIRED_AT] = value.pairedAt
            values[Keys.UPDATED_AT] = value.updatedAt
            values[Keys.CONTINUOUS_RECEIVING] = value.continuousReceiving
        }
    }

    override suspend fun clear() {
        dataStore.edit { it.clear() }
    }

    override suspend fun updateLastSequence(value: Long, updatedAt: String) {
        dataStore.edit { preferences ->
            preferences[Keys.LAST_SEQUENCE] = maxOf(preferences[Keys.LAST_SEQUENCE] ?: 0, value)
            preferences[Keys.UPDATED_AT] = updatedAt
        }
    }

    override suspend fun updateContinuousReceiving(value: Boolean, updatedAt: String) {
        dataStore.edit { preferences ->
            preferences[Keys.CONTINUOUS_RECEIVING] = value
            preferences[Keys.UPDATED_AT] = updatedAt
        }
    }

    private object Keys {
        val DEVICE_ID = stringPreferencesKey("device_id")
        val DEVICE_NAME = stringPreferencesKey("device_name")
        val HOST = stringPreferencesKey("host")
        val PORT = intPreferencesKey("port")
        val ENCRYPTED_TOKEN = stringPreferencesKey("encrypted_pairing_token")
        val TOKEN_IV = stringPreferencesKey("pairing_token_iv")
        val PROTOCOL_VERSION = intPreferencesKey("protocol_version")
        val WEB_SOCKET_PATH = stringPreferencesKey("websocket_path")
        val LAST_SEQUENCE = longPreferencesKey("last_sequence")
        val PAIRED_AT = stringPreferencesKey("paired_at")
        val UPDATED_AT = stringPreferencesKey("updated_at")
        val CONTINUOUS_RECEIVING = booleanPreferencesKey("continuous_receiving")
    }
}
