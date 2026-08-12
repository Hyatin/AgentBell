package com.hyatin.agentbell.notification

import android.content.Context

enum class PermissionNotificationPolicy(val persistedValue: String) {
    OFF("off"),
    ALWAYS_NOTIFY("always_notify");

    companion object {
        fun fromPersistedValue(value: String?): PermissionNotificationPolicy =
            entries.firstOrNull { it.persistedValue == value } ?: OFF

        fun migrate(value: String?, legacyEnabled: Boolean?): PermissionNotificationPolicy {
            // The old boolean could enable false-positive alerts, so both legacy values migrate off.
            if (value == null && legacyEnabled != null) return OFF
            return fromPersistedValue(value)
        }
    }
}

data class NotificationPreferencesState(
    val notifyTaskCompletion: Boolean = true,
    val notifyActionRequired: Boolean = true,
    val permissionNotificationPolicy: PermissionNotificationPolicy =
        PermissionNotificationPolicy.OFF,
    val replyAndConfirmationRequests: Boolean = true,
)

interface NotificationPreferences {
    fun current(): NotificationPreferencesState
    fun update(value: NotificationPreferencesState)
}

class SharedPreferencesNotificationPreferences(context: Context) : NotificationPreferences {
    private val preferences = context.getSharedPreferences(FILE_NAME, Context.MODE_PRIVATE)

    override fun current(): NotificationPreferencesState {
        val legacyValue = if (preferences.contains(LEGACY_KEY_PERMISSION)) {
            preferences.getBoolean(LEGACY_KEY_PERMISSION, false)
        } else {
            null
        }
        val policy = PermissionNotificationPolicy.migrate(
            preferences.getString(KEY_PERMISSION_POLICY, null),
            legacyValue,
        )
        if (!preferences.contains(KEY_PERMISSION_POLICY) || legacyValue != null) {
            preferences.edit()
                .putString(KEY_PERMISSION_POLICY, policy.persistedValue)
                .remove(LEGACY_KEY_PERMISSION)
                .apply()
        }
        return NotificationPreferencesState(
            notifyTaskCompletion = preferences.getBoolean(KEY_TASK_COMPLETION, true),
            notifyActionRequired = preferences.getBoolean(KEY_ACTION_REQUIRED, true),
            permissionNotificationPolicy = policy,
            replyAndConfirmationRequests = preferences.getBoolean(KEY_REPLY_CONFIRMATION, true),
        )
    }

    override fun update(value: NotificationPreferencesState) {
        preferences.edit()
            .putBoolean(KEY_TASK_COMPLETION, value.notifyTaskCompletion)
            .putBoolean(KEY_ACTION_REQUIRED, value.notifyActionRequired)
            .putString(KEY_PERMISSION_POLICY, value.permissionNotificationPolicy.persistedValue)
            .remove(LEGACY_KEY_PERMISSION)
            .putBoolean(KEY_REPLY_CONFIRMATION, value.replyAndConfirmationRequests)
            .apply()
    }

    private companion object {
        const val FILE_NAME = "agentbell_notification_preferences"
        const val KEY_TASK_COMPLETION = "notify_task_completion"
        const val KEY_ACTION_REQUIRED = "notify_action_required"
        const val KEY_PERMISSION_POLICY = "permission_notification_policy"
        const val LEGACY_KEY_PERMISSION = "notify_permission_requests"
        const val KEY_REPLY_CONFIRMATION = "notify_reply_confirmation_requests"
    }
}
