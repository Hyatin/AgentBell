package com.hyatin.agentbell.service

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.os.IBinder
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.hyatin.agentbell.AgentBellApplication
import com.hyatin.agentbell.connection.AgentBellWebSocketClient
import com.hyatin.agentbell.connection.ConnectionState
import com.hyatin.agentbell.notification.AgentBellNotificationManager
import com.hyatin.agentbell.storage.PairingCredential
import com.hyatin.agentbell.storage.PairingCredentialLoadResult
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

class AgentBellConnectionService : Service() {
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private lateinit var app: AgentBellApplication
    private lateinit var connectivity: ConnectivityManager
    private var client: AgentBellWebSocketClient? = null
    private var stateNotificationJob: Job? = null
    private var networkCallbackRegistered = false

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            client?.onNetworkAvailable()
        }

        override fun onLost(network: Network) {
            if (connectivity.activeNetwork == null) client?.onNetworkLost()
        }
    }

    override fun onCreate() {
        super.onCreate()
        app = application as AgentBellApplication
        connectivity = getSystemService(ConnectivityManager::class.java)
        app.notifications.createChannels()
        startAsForeground(deviceName = null, connected = false)
        try {
            connectivity.registerDefaultNetworkCallback(networkCallback)
            networkCallbackRegistered = true
        } catch (_: RuntimeException) {
            app.connectionStates.update(ConnectionState.Error("network_callback_unavailable"))
        }
        stateNotificationJob = serviceScope.launch {
            app.connectionStates.state.collectLatest { state ->
                when (state) {
                    is ConnectionState.Connected -> startAsForeground(state.deviceName, true)
                    is ConnectionState.Reconnecting -> startAsForeground(state.deviceName, false)
                    else -> Unit
                }
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopFromUser()
            return START_NOT_STICKY
        }
        serviceScope.launch { startConnection() }
        // Intentional: a system-killed service does not force an uncontrolled restart loop.
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        client?.stop()
        client = null
        if (networkCallbackRegistered) {
            try {
                connectivity.unregisterNetworkCallback(networkCallback)
            } catch (_: RuntimeException) {
                // The callback may already have been removed by the OS.
            }
        }
        stateNotificationJob?.cancel()
        serviceScope.cancel()
        super.onDestroy()
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        app.notifications.createChannels()
        when (val state = app.connectionStates.state.value) {
            is ConnectionState.Connected -> startAsForeground(state.deviceName, true)
            is ConnectionState.Reconnecting -> startAsForeground(state.deviceName, false)
            else -> startAsForeground(deviceName = null, connected = false)
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private suspend fun startConnection() {
        if (client != null) return
        val credential = when (val loaded = app.credentialStore.load()) {
            is PairingCredentialLoadResult.Available -> loaded.credential
            PairingCredentialLoadResult.Unpaired -> {
                app.connectionStates.update(ConnectionState.Unpaired)
                stopSelf()
                return
            }
            PairingCredentialLoadResult.DecryptionFailed -> {
                app.connectionStates.update(ConnectionState.Error("credential_decryption_failed"))
                stopSelf()
                return
            }
        }
        app.credentialStore.updateContinuousReceiving(true)
        app.eventHistory.initialize()
        val created = createClient(credential)
        client = created
        if (connectivity.activeNetwork == null) created.onNetworkLost()
        created.start()
    }

    private fun createClient(credential: PairingCredential) = AgentBellWebSocketClient(
        credential = credential,
        okHttpClient = app.okHttpClient,
        eventHistory = app.eventHistory,
        notifications = app.notifications,
        states = app.connectionStates,
        diagnostics = app.diagnostics,
        scope = serviceScope,
    )

    private fun stopFromUser() {
        serviceScope.launch {
            try {
                app.credentialStore.updateContinuousReceiving(false)
            } finally {
                client?.stop()
                client = null
                ServiceCompat.stopForeground(this@AgentBellConnectionService, ServiceCompat.STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
    }

    private fun startAsForeground(deviceName: String?, connected: Boolean) {
        val notification = app.notifications.connectionNotification(deviceName, connected)
        // connectedDevice is the Android 14+ type for sustained interaction with an
        // external device over a network. No camera/location/background bypass is used.
        ServiceCompat.startForeground(
            this,
            AgentBellNotificationManager.CONNECTION_NOTIFICATION_ID,
            notification,
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
        )
    }

    companion object {
        private const val ACTION_START = "com.hyatin.agentbell.action.START_CONNECTION"
        private const val ACTION_STOP = "com.hyatin.agentbell.action.STOP_CONNECTION"

        fun start(context: Context) {
            ContextCompat.startForegroundService(
                context,
                Intent(context, AgentBellConnectionService::class.java).setAction(ACTION_START),
            )
        }

        fun stop(context: Context) {
            context.startService(
                Intent(context, AgentBellConnectionService::class.java).setAction(ACTION_STOP),
            )
        }
    }
}
