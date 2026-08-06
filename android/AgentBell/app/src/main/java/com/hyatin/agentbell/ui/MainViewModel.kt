package com.hyatin.agentbell.ui

import android.app.Application
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Build
import android.provider.Settings
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.hyatin.agentbell.AgentBellApplication
import com.hyatin.agentbell.connection.ConnectionState
import com.hyatin.agentbell.localization.AppLanguage
import com.hyatin.agentbell.localization.AppLanguageController
import com.hyatin.agentbell.notification.AgentBellNotificationManager
import com.hyatin.agentbell.pairing.PairingValidationResult
import com.hyatin.agentbell.pairing.PrivateIpv4
import com.hyatin.agentbell.protocol.AgentEvent
import com.hyatin.agentbell.service.AgentBellConnectionService
import com.hyatin.agentbell.storage.PairingCredential
import com.hyatin.agentbell.storage.PairingCredentialLoadResult
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class PairedComputer(
    val deviceName: String,
    val maskedHost: String,
    val port: Int,
    val protocolVersion: Int,
    val lastSequence: Long,
    val continuousReceiving: Boolean,
)

sealed interface MainScreen {
    data object Loading : MainScreen
    data class Unpaired(val errorCode: String? = null) : MainScreen
    data object Scanning : MainScreen
    data object Validating : MainScreen
    data class Paired(val computer: PairedComputer) : MainScreen
    data class EventDetails(val event: AgentEvent) : MainScreen
    data object Settings : MainScreen
}

class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val app = application as AgentBellApplication
    private val mutableScreen = MutableStateFlow<MainScreen>(MainScreen.Loading)

    val screen: StateFlow<MainScreen> = mutableScreen.asStateFlow()
    val connectionState = app.connectionStates.state
    val events = app.eventHistory.events

    init {
        refreshPairing(startServiceIfEnabled = true)
    }

    fun beginScan() {
        mutableScreen.value = MainScreen.Scanning
    }

    fun cancelScan() {
        refreshPairing()
    }

    fun validate(pairingUrl: String) {
        if (mutableScreen.value == MainScreen.Validating) return
        mutableScreen.value = MainScreen.Validating
        app.connectionStates.update(ConnectionState.Validating)
        viewModelScope.launch {
            when (val result = app.pairingValidator.validate(pairingUrl)) {
                is PairingValidationResult.Success -> savePairing(result.credential)
                is PairingValidationResult.Failure -> {
                    app.connectionStates.update(
                        if (result.code == "unauthorized") ConnectionState.Unauthorized
                        else ConnectionState.Unpaired,
                    )
                    mutableScreen.value = MainScreen.Unpaired(result.code)
                }
            }
        }
    }

    fun setContinuousReceiving(enabled: Boolean) {
        viewModelScope.launch {
            app.credentialStore.updateContinuousReceiving(enabled)
            val current = loadCredential() ?: return@launch
            mutableScreen.value = MainScreen.Paired(current.toComputer(enabled))
            if (enabled) {
                AgentBellConnectionService.start(getApplication())
            } else {
                AgentBellConnectionService.stop(getApplication())
                app.connectionStates.update(ConnectionState.Stopped)
            }
        }
    }

    fun rePair() {
        mutableScreen.value = MainScreen.Scanning
    }

    fun unpair() {
        viewModelScope.launch {
            AgentBellConnectionService.stop(getApplication())
            app.credentialStore.clear()
            app.eventHistory.clear()
            app.connectionStates.update(ConnectionState.Unpaired)
            mutableScreen.value = MainScreen.Unpaired()
        }
    }

    fun showEvent(event: AgentEvent) {
        mutableScreen.value = MainScreen.EventDetails(event)
    }

    fun showEventByKey(eventKey: String?) {
        if (eventKey.isNullOrBlank()) return
        val match = events.value.firstOrNull {
            AgentBellNotificationManager.stableEventKey(it.eventId) == eventKey
        }
        if (match != null) mutableScreen.value = MainScreen.EventDetails(match)
    }

    fun closeDetails() {
        refreshPairing()
    }

    fun openSettings() {
        mutableScreen.value = MainScreen.Settings
    }

    fun closeSettings() {
        refreshPairing()
    }

    fun setLanguage(language: AppLanguage) {
        AppLanguageController.set(language)
    }

    fun copyDiagnostics() {
        val clipboard = getApplication<Application>()
            .getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.setPrimaryClip(
            ClipData.newPlainText(
                getApplication<Application>().getString(com.hyatin.agentbell.R.string.diagnostics_clip_label),
                app.diagnostics.sanitizedSummary(),
            ),
        )
    }

    fun openNotificationSettings() {
        val context = getApplication<Application>()
        context.startActivity(
            Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        )
    }

    fun openBatterySettings() {
        val context = getApplication<Application>()
        val intent = Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        try {
            context.startActivity(intent)
        } catch (_: Exception) {
            context.startActivity(
                Intent(Settings.ACTION_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            )
        }
    }

    fun notificationPermissionGranted(): Boolean = app.notifications.hasNotificationPermission()

    fun isXiaomiFamily(): Boolean =
        Build.MANUFACTURER.contains("xiaomi", ignoreCase = true) ||
            Build.BRAND.contains("redmi", ignoreCase = true)

    private fun refreshPairing(startServiceIfEnabled: Boolean = false) {
        viewModelScope.launch {
            val credential = loadCredential()
            mutableScreen.value = if (credential == null) {
                MainScreen.Unpaired(
                    if (app.connectionStates.state.value is ConnectionState.Error) {
                        "credential_decryption_failed"
                    } else {
                        null
                    },
                )
            } else {
                if (startServiceIfEnabled && credential.continuousReceiving) {
                    AgentBellConnectionService.start(getApplication())
                }
                MainScreen.Paired(credential.toComputer(credential.continuousReceiving))
            }
        }
    }

    private suspend fun savePairing(credential: PairingCredential) {
        try {
            val enabled = credential.withContinuousReceiving(true, credential.updatedAt)
            app.credentialStore.save(enabled)
            app.connectionStates.update(ConnectionState.Stopped)
            mutableScreen.value = MainScreen.Paired(enabled.toComputer(true))
            AgentBellConnectionService.start(getApplication())
        } catch (_: Exception) {
            app.connectionStates.update(ConnectionState.Error("credential_store_failed"))
            mutableScreen.value = MainScreen.Unpaired("credential_store_failed")
        }
    }

    private suspend fun loadCredential(): PairingCredential? =
        when (val loaded = app.credentialStore.load()) {
            is PairingCredentialLoadResult.Available -> loaded.credential
            PairingCredentialLoadResult.Unpaired -> null
            PairingCredentialLoadResult.DecryptionFailed -> {
                app.connectionStates.update(ConnectionState.Error("credential_decryption_failed"))
                null
            }
        }

    private fun PairingCredential.toComputer(continuous: Boolean) = PairedComputer(
        deviceName = deviceName,
        maskedHost = PrivateIpv4.masked(host),
        port = port,
        protocolVersion = protocolVersion,
        lastSequence = maxOf(lastSequence, events.value.maxOfOrNull { it.sequence } ?: 0),
        continuousReceiving = continuous,
    )
}
