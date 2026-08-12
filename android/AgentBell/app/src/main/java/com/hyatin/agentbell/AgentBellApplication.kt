package com.hyatin.agentbell

import android.app.Application
import android.content.res.Configuration
import com.hyatin.agentbell.connection.ConnectionState
import com.hyatin.agentbell.connection.ConnectionStateRepository
import com.hyatin.agentbell.connection.PrivateLanRequestGuard
import com.hyatin.agentbell.diagnostics.BoundedAgentBellDiagnostics
import com.hyatin.agentbell.notification.AgentBellNotificationManager
import com.hyatin.agentbell.notification.SharedPreferencesNotificationPreferences
import com.hyatin.agentbell.notification.PermissionNotificationPolicy
import com.hyatin.agentbell.protocol.AgentEventSemantics
import com.hyatin.agentbell.pairing.OkHttpDesktopStatusTransport
import com.hyatin.agentbell.pairing.PairingValidator
import com.hyatin.agentbell.security.AndroidKeystorePairingTokenCipher
import com.hyatin.agentbell.storage.DataStoreCredentialMetadataStorage
import com.hyatin.agentbell.storage.DataStoreEventStateStorage
import com.hyatin.agentbell.storage.EventHistoryRepository
import com.hyatin.agentbell.storage.PairingCredentialLoadResult
import com.hyatin.agentbell.storage.SecurePairingCredentialStore
import com.hyatin.agentbell.storage.agentBellCredentialsDataStore
import com.hyatin.agentbell.storage.agentBellEventsDataStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

class AgentBellApplication : Application() {
    val applicationScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    val diagnostics = BoundedAgentBellDiagnostics()
    val connectionStates = ConnectionStateRepository()

    val okHttpClient: OkHttpClient by lazy {
        OkHttpClient.Builder()
            .addInterceptor(PrivateLanRequestGuard())
            .connectTimeout(8, TimeUnit.SECONDS)
            // Bounds the HTTP upgrade handshake. OkHttp clears the socket read
            // timeout after a WebSocket upgrade, so the long-lived connection stays idle-safe.
            .readTimeout(10, TimeUnit.SECONDS)
            .pingInterval(0, TimeUnit.MILLISECONDS)
            .build()
    }

    val credentialStore by lazy {
        SecurePairingCredentialStore(
            DataStoreCredentialMetadataStorage(agentBellCredentialsDataStore),
            AndroidKeystorePairingTokenCipher(),
        )
    }

    val eventHistory by lazy {
        EventHistoryRepository(
            DataStoreEventStateStorage(agentBellEventsDataStore),
            credentialStore,
        ) { event ->
            event.actionType != AgentEventSemantics.ACTION_PERMISSION_REQUIRED ||
                notificationPreferences.current().permissionNotificationPolicy ==
                PermissionNotificationPolicy.ALWAYS_NOTIFY
        }
    }

    val notificationPreferences by lazy { SharedPreferencesNotificationPreferences(this) }
    val notifications by lazy { AgentBellNotificationManager(this, notificationPreferences) }
    val pairingValidator by lazy {
        PairingValidator(OkHttpDesktopStatusTransport(okHttpClient))
    }

    override fun onCreate() {
        super.onCreate()
        notifications.createChannels()
        applicationScope.launch {
            eventHistory.initialize()
            connectionStates.update(
                when (credentialStore.load()) {
                    is PairingCredentialLoadResult.Available -> ConnectionState.Stopped
                    PairingCredentialLoadResult.Unpaired -> ConnectionState.Unpaired
                    PairingCredentialLoadResult.DecryptionFailed ->
                        ConnectionState.Error("credential_decryption_failed")
                },
            )
        }
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        notifications.createChannels()
    }
}
