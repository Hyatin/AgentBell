package com.hyatin.agentbell

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.compose.setContent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.appcompat.app.AppCompatActivity
import androidx.camera.view.PreviewView
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.hyatin.agentbell.connection.ConnectionState
import com.hyatin.agentbell.localization.AppLanguage
import com.hyatin.agentbell.localization.AppLanguageController
import com.hyatin.agentbell.notification.AgentBellNotificationManager
import com.hyatin.agentbell.notification.PermissionNotificationPolicy
import com.hyatin.agentbell.pairing.QrScannerController
import com.hyatin.agentbell.protocol.AgentEvent
import com.hyatin.agentbell.protocol.AgentEventSemantics
import com.hyatin.agentbell.ui.MainScreen
import com.hyatin.agentbell.ui.MainUiProjection
import com.hyatin.agentbell.ui.MainViewModel
import com.hyatin.agentbell.ui.PairedComputer
import com.hyatin.agentbell.ui.UiText

class MainActivity : AppCompatActivity() {
    private val viewModel by viewModels<MainViewModel>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme {
                AgentBellApp(viewModel)
            }
        }
        handleIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleIntent(intent)
    }

    private fun handleIntent(intent: Intent?) {
        viewModel.showEventByKey(
            intent?.getStringExtra(AgentBellNotificationManager.EXTRA_EVENT_KEY),
        )
    }
}

@Composable
private fun AgentBellApp(viewModel: MainViewModel) {
    val screen by viewModel.screen.collectAsStateWithLifecycle()
    val connection by viewModel.connectionState.collectAsStateWithLifecycle()
    val events by viewModel.events.collectAsStateWithLifecycle()
    Scaffold { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(20.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text("AgentBell", style = MaterialTheme.typography.headlineMedium)
                    Text(
                        stringResource(R.string.version_format, BuildConfig.VERSION_NAME),
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
                TextButton(onClick = viewModel::openSettings) {
                    Text(stringResource(R.string.common_settings))
                }
            }
            Spacer(Modifier.height(16.dp))
            when (val current = screen) {
                MainScreen.Loading -> CircularProgressIndicator()
                is MainScreen.Unpaired -> UnpairedScreen(current, viewModel)
                MainScreen.Scanning -> ScannerScreen(viewModel)
                MainScreen.Validating -> ValidationScreen()
                is MainScreen.Paired -> PairedScreen(
                    current.computer,
                    connection,
                    events,
                    viewModel,
                    modifier = Modifier.weight(1f, fill = false),
                )
                is MainScreen.EventDetails -> EventDetailsScreen(current.event, viewModel)
                MainScreen.Settings -> SettingsScreen(
                    viewModel,
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

@Composable
private fun UnpairedScreen(screen: MainScreen.Unpaired, viewModel: MainViewModel) {
    var manualUrl by remember { mutableStateOf("") }
    Text(stringResource(R.string.pairing_scan_computer_qr))
    Text(
        stringResource(R.string.pairing_local_network_notice),
        style = MaterialTheme.typography.bodySmall,
    )
    screen.errorCode?.let {
        Spacer(Modifier.height(8.dp))
        Text(
            localized(MainUiProjection.pairingErrorText(it)),
            color = MaterialTheme.colorScheme.error,
        )
    }
    Spacer(Modifier.height(16.dp))
    Button(onClick = viewModel::beginScan) {
        Text(stringResource(R.string.pairing_scan_qr_code))
    }
    Spacer(Modifier.height(20.dp))
    Text(
        stringResource(R.string.pairing_manual_diagnostic),
        style = MaterialTheme.typography.labelMedium,
    )
    OutlinedTextField(
        value = manualUrl,
        onValueChange = { manualUrl = it.take(2048) },
        label = { Text(stringResource(R.string.pairing_url)) },
        singleLine = true,
        visualTransformation = PasswordVisualTransformation(),
        modifier = Modifier.fillMaxWidth(),
    )
    TextButton(
        enabled = manualUrl.isNotBlank(),
        onClick = { viewModel.validate(manualUrl) },
    ) {
        Text(stringResource(R.string.pairing_validate))
    }
}

@Composable
private fun ScannerScreen(viewModel: MainViewModel) {
    val context = LocalContext.current
    var granted by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) ==
                PackageManager.PERMISSION_GRANTED,
        )
    }
    val permission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { granted = it }
    if (!granted) {
        Text(stringResource(R.string.pairing_camera_privacy))
        Button(onClick = { permission.launch(Manifest.permission.CAMERA) }) {
            Text(stringResource(R.string.pairing_allow_camera))
        }
    } else {
        ScannerPreview(
            contentDescription = stringResource(R.string.pairing_scanner_content_description),
            onDecoded = viewModel::validate,
        )
    }
    TextButton(onClick = viewModel::cancelScan) {
        Text(stringResource(R.string.common_back))
    }
}

@Composable
private fun ScannerPreview(contentDescription: String, onDecoded: (String) -> Unit) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    var controller by remember { mutableStateOf<QrScannerController?>(null) }
    AndroidView(
        factory = {
            PreviewView(it).also { preview ->
                controller = QrScannerController(context, lifecycleOwner, preview, onDecoded)
                    .also(QrScannerController::start)
            }
        },
        modifier = Modifier
            .fillMaxWidth()
            .height(420.dp)
            .semantics { this.contentDescription = contentDescription },
    )
    DisposableEffect(Unit) {
        onDispose { controller?.close() }
    }
}

@Composable
private fun ValidationScreen() {
    Row(verticalAlignment = Alignment.CenterVertically) {
        CircularProgressIndicator()
        Text(
            stringResource(R.string.pairing_validating),
            modifier = Modifier.padding(start = 12.dp),
        )
    }
}

@Composable
private fun PairedScreen(
    computer: PairedComputer,
    state: ConnectionState,
    events: List<AgentEvent>,
    viewModel: MainViewModel,
    modifier: Modifier = Modifier,
) {
    var notificationGranted by remember { mutableStateOf(viewModel.notificationPermissionGranted()) }
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                notificationGranted = viewModel.notificationPermissionGranted()
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }
    val permission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { notificationGranted = it }

    Text(stringResource(R.string.computer_format, computer.deviceName))
    Text(stringResource(R.string.connection_format, localized(MainUiProjection.connectionText(state))))
    Text(stringResource(R.string.address_format, computer.maskedHost, computer.port))
    Text(
        stringResource(
            R.string.protocol_sequence_format,
            computer.protocolVersion,
            computer.lastSequence,
        ),
    )
    if (state is ConnectionState.Connected) {
        Text(stringResource(R.string.last_connected_format, state.connectedAt))
    }
    Spacer(Modifier.height(12.dp))
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(stringResource(R.string.continuous_receiving), modifier = Modifier.weight(1f))
        Switch(
            checked = computer.continuousReceiving,
            onCheckedChange = viewModel::setContinuousReceiving,
        )
    }
    if (!notificationGranted) {
        Text(
            stringResource(R.string.notification_permission_missing),
            color = MaterialTheme.colorScheme.error,
        )
        if (Build.VERSION.SDK_INT >= 33) {
            Button(onClick = { permission.launch(Manifest.permission.POST_NOTIFICATIONS) }) {
                Text(stringResource(R.string.notification_allow))
            }
        }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton(onClick = viewModel::openNotificationSettings) {
            Text(stringResource(R.string.notification_settings))
        }
        OutlinedButton(onClick = viewModel::openBatterySettings) {
            Text(stringResource(R.string.battery_settings))
        }
    }
    Text(
        stringResource(
            if (viewModel.isXiaomiFamily()) {
                R.string.battery_xiaomi_guidance
            } else {
                R.string.battery_general_guidance
            },
        ),
    )
    Text(stringResource(R.string.background_policy_notice))
    HorizontalDivider(Modifier.padding(vertical = 12.dp))
    Text(stringResource(R.string.events_recent), style = MaterialTheme.typography.titleMedium)
    LazyColumn(modifier = modifier) {
        items(MainUiProjection.recentEvents(events), key = { it.eventId }) { event ->
            EventCard(event) { viewModel.showEvent(event) }
        }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        TextButton(onClick = viewModel::copyDiagnostics) {
            Text(stringResource(R.string.diagnostics_copy))
        }
        TextButton(onClick = viewModel::rePair) {
            Text(stringResource(R.string.pairing_repair))
        }
        TextButton(onClick = viewModel::unpair) {
            Text(stringResource(R.string.pairing_unpair))
        }
    }
}

@Composable
private fun EventCard(event: AgentEvent, onClick: () -> Unit) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 4.dp)
            .clickable(onClick = onClick),
    ) {
        Column(Modifier.padding(12.dp)) {
            Text(localizedEventTitle(event), style = MaterialTheme.typography.titleSmall)
            event.project?.let { Text(it) }
            Text(
                if (event.category == AgentEventSemantics.CATEGORY_ACTION_REQUIRED) {
                    stringResource(R.string.event_action_required_safe_summary)
                } else {
                    event.summary ?: stringResource(R.string.event_turn_ended)
                },
            )
            Text(
                stringResource(R.string.event_sequence_format, event.sequence),
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

@Composable
private fun EventDetailsScreen(event: AgentEvent, viewModel: MainViewModel) {
    Text(localizedEventTitle(event), style = MaterialTheme.typography.titleLarge)
    Text(stringResource(R.string.event_project_format, event.project ?: "—"))
    Text(
        stringResource(
            R.string.event_summary_format,
            if (event.category == AgentEventSemantics.CATEGORY_ACTION_REQUIRED) {
                stringResource(R.string.event_action_required_safe_summary)
            } else {
                event.summary ?: stringResource(R.string.event_turn_ended)
            },
        ),
    )
    Text(stringResource(R.string.event_occurred_at_format, event.occurredAt))
    Text(stringResource(R.string.event_sequence_format, event.sequence))
    Text(stringResource(R.string.event_agent_format, event.agent))
    Text(stringResource(R.string.event_status_format, event.status))
    TextButton(onClick = viewModel::closeDetails) {
        Text(stringResource(R.string.common_back))
    }
}

@Composable
private fun SettingsScreen(viewModel: MainViewModel, modifier: Modifier = Modifier) {
    val selected = AppLanguageController.current()
    val notificationPreferences by viewModel.notificationPreferences.collectAsStateWithLifecycle()
    LazyColumn(modifier = modifier.fillMaxWidth()) {
        item {
            Column {
                Text(stringResource(R.string.common_settings), style = MaterialTheme.typography.titleLarge)
                Spacer(Modifier.height(12.dp))
                Text(stringResource(R.string.settings_language), style = MaterialTheme.typography.titleMedium)
                val options = listOf(
                    AppLanguage.SYSTEM to R.string.language_system,
                    AppLanguage.ENGLISH to R.string.language_english,
                    AppLanguage.CHINESE_SIMPLIFIED to R.string.language_chinese_simplified,
                )
                options.forEach { (language, textResource) ->
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .clickable { viewModel.setLanguage(language) }
                            .padding(vertical = 6.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        RadioButton(
                            selected = selected == language,
                            onClick = { viewModel.setLanguage(language) },
                        )
                        Text(stringResource(textResource))
                    }
                }
                Spacer(Modifier.height(16.dp))
                Text(
                    stringResource(R.string.settings_notifications),
                    style = MaterialTheme.typography.titleMedium,
                )
                NotificationSettingRow(
                    R.string.settings_notify_task_completion,
                    notificationPreferences.notifyTaskCompletion,
                    viewModel::setNotifyTaskCompletion,
                )
                NotificationSettingRow(
                    R.string.settings_notify_action_required,
                    notificationPreferences.notifyActionRequired,
                    viewModel::setNotifyActionRequired,
                )
                PermissionNotificationPolicySetting(
                    notificationPreferences.permissionNotificationPolicy,
                    viewModel::setPermissionNotificationPolicy,
                )
                NotificationSettingRow(
                    R.string.settings_reply_confirmation_requests,
                    notificationPreferences.replyAndConfirmationRequests,
                    viewModel::setReplyAndConfirmationRequests,
                )
                TextButton(onClick = viewModel::closeSettings) {
                    Text(stringResource(R.string.common_back))
                }
            }
        }
    }
}

@Composable
private fun PermissionNotificationPolicySetting(
    selected: PermissionNotificationPolicy,
    onSelected: (PermissionNotificationPolicy) -> Unit,
) {
    Text(
        stringResource(R.string.settings_permission_request_notifications),
        style = MaterialTheme.typography.titleSmall,
        modifier = Modifier.padding(top = 8.dp),
    )
    listOf(
        PermissionNotificationPolicy.OFF to R.string.permission_notification_off,
        PermissionNotificationPolicy.ALWAYS_NOTIFY to
            R.string.permission_notification_always_notify,
    ).forEach { (policy, textResource) ->
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .clickable { onSelected(policy) }
                .padding(vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            RadioButton(
                selected = selected == policy,
                onClick = { onSelected(policy) },
            )
            Text(stringResource(textResource))
        }
    }
    Text(
        stringResource(R.string.settings_permission_request_notifications_explanation),
        style = MaterialTheme.typography.bodySmall,
        modifier = Modifier.padding(bottom = 8.dp),
    )
}

@Composable
private fun NotificationSettingRow(
    textResource: Int,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(stringResource(textResource), modifier = Modifier.weight(1f))
        Switch(checked = checked, onCheckedChange = onCheckedChange)
    }
}

@Composable
private fun localized(text: UiText): String = stringResource(
    text.resourceId,
    *text.arguments.toTypedArray(),
)

@Composable
private fun localizedEventTitle(event: AgentEvent): String =
    when (event.actionType) {
        AgentEventSemantics.ACTION_PERMISSION_REQUIRED ->
            stringResource(R.string.event_permission_required)
        AgentEventSemantics.ACTION_INPUT_REQUIRED ->
            stringResource(R.string.event_input_required)
        AgentEventSemantics.ACTION_CONFIRMATION_REQUIRED ->
            stringResource(R.string.event_confirmation_required)
        AgentEventSemantics.ACTION_ATTENTION_REQUIRED ->
            stringResource(R.string.event_attention_required)
        else -> if (event.agent == "codex" && event.status == "completed") {
            stringResource(R.string.event_codex_completed)
        } else {
            event.title
        }
    }
