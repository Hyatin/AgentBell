package com.hyatin.agentbell

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
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
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.hyatin.agentbell.connection.ConnectionState
import com.hyatin.agentbell.notification.AgentBellNotificationManager
import com.hyatin.agentbell.pairing.QrScannerController
import com.hyatin.agentbell.protocol.AgentEvent
import com.hyatin.agentbell.ui.MainScreen
import com.hyatin.agentbell.ui.MainUiProjection
import com.hyatin.agentbell.ui.MainViewModel
import com.hyatin.agentbell.ui.PairedComputer

class MainActivity : ComponentActivity() {
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
            Text("AgentBell", style = MaterialTheme.typography.headlineMedium)
            Text("版本 ${BuildConfig.VERSION_NAME}", style = MaterialTheme.typography.bodySmall)
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
            }
        }
    }
}

@Composable
private fun UnpairedScreen(screen: MainScreen.Unpaired, viewModel: MainViewModel) {
    var manualUrl by remember { mutableStateOf("") }
    Text("扫描电脑上的配对二维码")
    Text("同一局域网直连，不经过云端。", style = MaterialTheme.typography.bodySmall)
    screen.errorCode?.let {
        Spacer(Modifier.height(8.dp))
        Text("配对失败：$it", color = MaterialTheme.colorScheme.error)
    }
    Spacer(Modifier.height(16.dp))
    Button(onClick = viewModel::beginScan) { Text("扫码配对") }
    Spacer(Modifier.height(20.dp))
    Text("诊断备用：手动粘贴配对URL", style = MaterialTheme.typography.labelMedium)
    OutlinedTextField(
        value = manualUrl,
        onValueChange = { manualUrl = it.take(2048) },
        label = { Text("配对URL") },
        singleLine = true,
        visualTransformation = PasswordVisualTransformation(),
        modifier = Modifier.fillMaxWidth(),
    )
    TextButton(
        enabled = manualUrl.isNotBlank(),
        onClick = { viewModel.validate(manualUrl) },
    ) { Text("验证") }
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
        Text("相机仅在扫码页面使用，不保存画面或二维码图像。")
        Button(onClick = { permission.launch(Manifest.permission.CAMERA) }) {
            Text("允许相机并扫码")
        }
    } else {
        ScannerPreview(onDecoded = viewModel::validate)
    }
    TextButton(onClick = viewModel::cancelScan) { Text("返回") }
}

@Composable
private fun ScannerPreview(onDecoded: (String) -> Unit) {
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
            .height(420.dp),
    )
    DisposableEffect(Unit) {
        onDispose { controller?.close() }
    }
}

@Composable
private fun ValidationScreen() {
    Row(verticalAlignment = Alignment.CenterVertically) {
        CircularProgressIndicator()
        Text("正在连接电脑并验证协议…", modifier = Modifier.padding(start = 12.dp))
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

    Text("电脑：${computer.deviceName}")
    Text("连接：${MainUiProjection.connectionLabel(state)}")
    Text("地址：${computer.maskedHost}:${computer.port}")
    Text("协议：${computer.protocolVersion} · 最新 sequence：${computer.lastSequence}")
    if (state is ConnectionState.Connected) Text("最近连接：${state.connectedAt}")
    Spacer(Modifier.height(12.dp))
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text("持续接收", modifier = Modifier.weight(1f))
        Switch(
            checked = computer.continuousReceiving,
            onCheckedChange = viewModel::setContinuousReceiving,
        )
    }
    if (!notificationGranted) {
        Text(
            "事件已收到，但系统通知权限未开启。WebSocket仍会继续接收。",
            color = MaterialTheme.colorScheme.error,
        )
        if (Build.VERSION.SDK_INT >= 33) {
            Button(onClick = { permission.launch(Manifest.permission.POST_NOTIFICATIONS) }) {
                Text("允许通知")
            }
        }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton(onClick = viewModel::openNotificationSettings) { Text("通知设置") }
        OutlinedButton(onClick = viewModel::openBatterySettings) { Text("电池设置") }
    }
    if (viewModel.isXiaomiFamily()) {
        Text("Xiaomi/Redmi：建议允许后台活动、电池策略设为不限制；如系统提供，请允许自启动。")
    } else {
        Text("建议允许后台活动，并将 AgentBell 电池策略设为不限制。")
    }
    Text("系统可能限制后台行为；AgentBell不会绕过系统安全策略。")
    HorizontalDivider(Modifier.padding(vertical = 12.dp))
    Text("最近事件", style = MaterialTheme.typography.titleMedium)
    LazyColumn(modifier = modifier) {
        items(MainUiProjection.recentEvents(events), key = { it.eventId }) { event ->
            EventCard(event) { viewModel.showEvent(event) }
        }
    }
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        TextButton(onClick = viewModel::copyDiagnostics) { Text("复制诊断摘要") }
        TextButton(onClick = viewModel::rePair) { Text("重新配对") }
        TextButton(onClick = viewModel::unpair) { Text("取消配对") }
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
            Text(event.title, style = MaterialTheme.typography.titleSmall)
            event.project?.let { Text(it) }
            Text(event.summary ?: "当前回合已经结束。")
            Text("sequence ${event.sequence}", style = MaterialTheme.typography.bodySmall)
        }
    }
}

@Composable
private fun EventDetailsScreen(event: AgentEvent, viewModel: MainViewModel) {
    Text(event.title, style = MaterialTheme.typography.titleLarge)
    Text("project：${event.project ?: "—"}")
    Text("summary：${event.summary ?: "当前回合已经结束。"}")
    Text("occurredAt：${event.occurredAt}")
    Text("sequence：${event.sequence}")
    Text("agent：${event.agent}")
    Text("status：${event.status}")
    TextButton(onClick = viewModel::closeDetails) { Text("返回") }
}
