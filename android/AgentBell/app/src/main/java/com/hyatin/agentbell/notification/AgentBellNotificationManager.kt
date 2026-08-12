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
import com.hyatin.agentbell.R
import com.hyatin.agentbell.connection.CompletionNotificationSink
import com.hyatin.agentbell.protocol.AgentEvent
import com.hyatin.agentbell.protocol.AgentEventSemantics
import java.security.MessageDigest

class AgentBellNotificationManager(
    private val context: Context,
    private val preferences: NotificationPreferences =
        SharedPreferencesNotificationPreferences(context),
) : CompletionNotificationSink {
    private val manager = context.getSystemService(NotificationManager::class.java)

    fun createChannels() {
        manager.createNotificationChannel(
            NotificationChannel(
                CONNECTION_CHANNEL_ID,
                context.getString(R.string.notification_connection_channel),
                NotificationManager.IMPORTANCE_LOW,
            ).apply {
                description = context.getString(
                    R.string.notification_connection_channel_description,
                )
                setShowBadge(false)
            },
        )
        manager.createNotificationChannel(
            NotificationChannel(
                ACTION_REQUIRED_CHANNEL_ID,
                context.getString(R.string.notification_action_required_channel),
                ACTION_REQUIRED_IMPORTANCE,
            ).apply {
                description = context.getString(
                    R.string.notification_action_required_channel_description,
                )
                enableVibration(true)
                setBypassDnd(false)
            },
        )
        manager.createNotificationChannel(
            NotificationChannel(
                COMPLETED_CHANNEL_ID,
                context.getString(R.string.notification_completed_channel),
                NotificationManager.IMPORTANCE_HIGH,
            ).apply {
                description = context.getString(
                    R.string.notification_completed_channel_description,
                )
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
            deviceName.isNullOrBlank() ->
                context.getString(R.string.notification_connection_preparing)
            connected -> context.getString(
                R.string.notification_connection_connected,
                deviceName,
            )
            else -> context.getString(
                R.string.notification_connection_reconnecting,
                deviceName,
            )
        }
        return NotificationCompat.Builder(context, CONNECTION_CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setContentTitle(context.getString(R.string.notification_connection_channel))
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setContentIntent(mainPendingIntent(0, null))
            .build()
    }

    override fun post(event: AgentEvent): Boolean {
        if (event.resolvedAt != null &&
            event.category == AgentEventSemantics.CATEGORY_ACTION_REQUIRED
        ) {
            cancelActiveAction(event)
            return false
        }
        if (event.category == AgentEventSemantics.CATEGORY_COMPLETION) {
            cancelActiveAction(event)
        }
        if (!hasNotificationPermission()) return false
        val currentPreferences = preferences.current()
        if (!shouldNotify(event, currentPreferences)) return false

        if (event.category == AgentEventSemantics.CATEGORY_ACTION_REQUIRED) {
            return postActionRequired(event)
        }

        val title = event.project?.takeIf { it.isNotBlank() }
            ?.let {
                context.getString(
                    R.string.notification_completed_with_project,
                    truncate(it, 80),
                )
            }
            ?: context.getString(R.string.notification_completed)
        val body = event.summary?.takeIf { it.isNotBlank() }
            ?.let { truncate(it, 320) }
            ?: context.getString(R.string.event_turn_ended)
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

    private fun postActionRequired(event: AgentEvent): Boolean {
        val (titleResource, bodyResource, genericBodyResource) = when (event.actionType) {
            AgentEventSemantics.ACTION_PERMISSION_REQUIRED -> Triple(
                R.string.notification_permission_required_title,
                R.string.notification_permission_required_body,
                R.string.notification_permission_required_body_generic,
            )
            AgentEventSemantics.ACTION_INPUT_REQUIRED -> Triple(
                R.string.notification_input_required_title,
                R.string.notification_input_required_body,
                R.string.notification_input_required_body_generic,
            )
            AgentEventSemantics.ACTION_CONFIRMATION_REQUIRED -> Triple(
                R.string.notification_confirmation_required_title,
                R.string.notification_confirmation_required_body,
                R.string.notification_confirmation_required_body_generic,
            )
            else -> Triple(
                R.string.notification_attention_required_title,
                R.string.notification_attention_required_body,
                R.string.notification_attention_required_body_generic,
            )
        }
        val body = event.project?.takeIf { it.isNotBlank() }
            ?.let { context.getString(bodyResource, truncate(it, 80)) }
            ?: context.getString(genericBodyResource)
        val notificationId = actionNotificationId(event)
        val eventKey = stableEventKey(event.eventId)
        val notification = NotificationCompat.Builder(context, ACTION_REQUIRED_CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_notify_error)
            .setContentTitle(context.getString(titleResource))
            .setContentText(body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setAutoCancel(true)
            .setOnlyAlertOnce(true)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_REMINDER)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setContentIntent(mainPendingIntent(notificationId, eventKey))
            .build()
        manager.notify(notificationId, notification)
        return true
    }

    private fun cancelActiveAction(completion: AgentEvent) {
        if (!completion.turnIdHash.isNullOrBlank()) {
            manager.cancel(actionNotificationId(completion))
        }
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
        const val ACTION_REQUIRED_CHANNEL_ID = "agentbell_action_required"
        const val ACTION_REQUIRED_IMPORTANCE = NotificationManager.IMPORTANCE_HIGH
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

        fun actionNotificationId(event: AgentEvent): Int = stableNotificationId(
            "action-turn:${event.turnIdHash ?: stableEventKey(event.eventId)}",
        )

        fun shouldNotify(
            event: AgentEvent,
            preferences: NotificationPreferencesState,
        ): Boolean = event.resolvedAt == null && when (event.actionType) {
            AgentEventSemantics.ACTION_PERMISSION_REQUIRED ->
                preferences.permissionNotificationPolicy ==
                    PermissionNotificationPolicy.ALWAYS_NOTIFY
            AgentEventSemantics.ACTION_INPUT_REQUIRED,
            AgentEventSemantics.ACTION_CONFIRMATION_REQUIRED,
            -> preferences.notifyActionRequired && preferences.replyAndConfirmationRequests
            AgentEventSemantics.ACTION_ATTENTION_REQUIRED -> preferences.notifyActionRequired
            else -> preferences.notifyTaskCompletion
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
