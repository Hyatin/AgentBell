package com.hyatin.agentbell.notification

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.hyatin.agentbell.MainActivity
import com.hyatin.agentbell.connection.CompletionNotificationSink
import com.hyatin.agentbell.protocol.AgentEvent
import java.security.MessageDigest

class AgentBellNotificationManager(
    private val context: Context,
) : CompletionNotificationSink {
    private val manager = context.getSystemService(NotificationManager::class.java)

    fun createChannels() {
        manager.createNotificationChannel(
            NotificationChannel(
                CONNECTION_CHANNEL_ID,
                "AgentBell连接服务",
                NotificationManager.IMPORTANCE_LOW,
            ).apply {
                description = "保持与可信局域网内 AgentBell Desktop 的连接"
                setShowBadge(false)
            },
        )
        manager.createNotificationChannel(
            NotificationChannel(
                COMPLETED_CHANNEL_ID,
                "Codex任务完成",
                NotificationManager.IMPORTANCE_HIGH,
            ).apply {
                description = "Codex 当前回合完成提醒"
                enableVibration(true)
            },
        )
    }

    fun hasNotificationPermission(): Boolean =
        Build.VERSION.SDK_INT < 33 ||
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) ==
            PackageManager.PERMISSION_GRANTED

    fun connectionNotification(deviceName: String?, connected: Boolean): Notification {
        val text = when {
            deviceName.isNullOrBlank() -> "AgentBell正在准备连接"
            connected -> "AgentBell已连接到 $deviceName"
            else -> "AgentBell正在重新连接 $deviceName"
        }
        return NotificationCompat.Builder(context, CONNECTION_CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setContentTitle("AgentBell连接服务")
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setContentIntent(mainPendingIntent(0, null))
            .build()
    }

    override fun post(event: AgentEvent): Boolean {
        if (!hasNotificationPermission()) return false
        val title = event.project?.takeIf { it.isNotBlank() }
            ?.let { "Codex已完成 · ${truncate(it, 80)}" }
            ?: "Codex已完成"
        val body = event.summary?.takeIf { it.isNotBlank() }
            ?.let { truncate(it, 320) }
            ?: "当前回合已经结束。"
        val eventKey = stableEventKey(event.eventId)
        val notification = NotificationCompat.Builder(context, COMPLETED_CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_download_done)
            .setContentTitle(title)
            .setContentText(body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setAutoCancel(true)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setContentIntent(mainPendingIntent(stableNotificationId(event.eventId), eventKey))
            .build()
        manager.notify(stableNotificationId(event.eventId), notification)
        return true
    }

    private fun mainPendingIntent(requestCode: Int, eventKey: String?): PendingIntent {
        return PendingIntent.getActivity(
            context,
            requestCode,
            mainIntent(eventKey),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }

    internal fun completionIntent(eventId: String): Intent =
        mainIntent(stableEventKey(eventId))

    private fun mainIntent(eventKey: String?): Intent =
        Intent(context, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
            if (eventKey != null) putExtra(EXTRA_EVENT_KEY, eventKey)
        }

    companion object {
        const val CONNECTION_CHANNEL_ID = "agentbell_connection"
        const val COMPLETED_CHANNEL_ID = "agentbell_codex_completed"
        const val CONNECTION_NOTIFICATION_ID = 17863
        const val EXTRA_EVENT_KEY = "agentbell_event_key"

        fun stableNotificationId(eventId: String): Int {
            val digest = MessageDigest.getInstance("SHA-256")
                .digest(eventId.toByteArray(Charsets.UTF_8))
            return ((digest[0].toInt() and 0xff) shl 24 or
                ((digest[1].toInt() and 0xff) shl 16) or
                ((digest[2].toInt() and 0xff) shl 8) or
                (digest[3].toInt() and 0xff)) and Int.MAX_VALUE
        }

        fun stableEventKey(eventId: String): String = MessageDigest.getInstance("SHA-256")
            .digest(eventId.toByteArray(Charsets.UTF_8))
            .take(6)
            .joinToString("") { "%02x".format(it) }

        fun truncate(value: String, maximumCodePoints: Int): String {
            if (value.codePointCount(0, value.length) <= maximumCodePoints) return value
            val end = value.offsetByCodePoints(0, maximumCodePoints)
            return value.substring(0, end)
        }
    }
}
