package com.hyatin.agentbell.notification

import com.hyatin.agentbell.protocol.AgentEvent
import com.hyatin.agentbell.protocol.AgentEventSemantics
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AgentBellNotificationManagerTest {
    @Test fun actionChannelIsSeparateHighImportanceAndCompletionChannelIsPreserved() {
        assertEquals("agentbell_action_required", AgentBellNotificationManager.ACTION_REQUIRED_CHANNEL_ID)
        assertEquals(4, AgentBellNotificationManager.ACTION_REQUIRED_IMPORTANCE)
        assertEquals("agentbell_codex_completed", AgentBellNotificationManager.COMPLETED_CHANNEL_ID)
    }

    @Test fun notificationIdIsStablePerEventId() {
        assertEquals(
            AgentBellNotificationManager.stableNotificationId("event-1"),
            AgentBellNotificationManager.stableNotificationId("event-1"),
        )
        assertNotEquals(
            AgentBellNotificationManager.stableNotificationId("event-1"),
            AgentBellNotificationManager.stableNotificationId("event-2"),
        )
    }

    @Test fun eventKeyDoesNotExposeEventId() {
        val key = AgentBellNotificationManager.stableEventKey("codex:private-value")
        assertEquals(12, key.length)
        assertTrue(!key.contains("private-value"))
    }

    @Test fun summaryTruncationDoesNotSplitEmojiSurrogatePair() {
        val result = AgentBellNotificationManager.truncate("a".repeat(319) + "🔔" + "tail", 320)
        assertTrue(result.endsWith("🔔"))
        assertEquals(320, result.codePointCount(0, result.length))
    }

    @Test fun actionNotificationIdIsStablePerTurnAndDiffersAcrossTurns() {
        val first = actionEvent("event-1", "turnhash00001")
        val retry = actionEvent("event-2", "turnhash00001")
        val different = actionEvent("event-3", "turnhash00002")

        assertEquals(
            AgentBellNotificationManager.actionNotificationId(first),
            AgentBellNotificationManager.actionNotificationId(retry),
        )
        assertNotEquals(
            AgentBellNotificationManager.actionNotificationId(first),
            AgentBellNotificationManager.actionNotificationId(different),
        )
    }

    @Test fun notificationPreferencesSuppressOnlyMatchingDisplayCategories() {
        val disabledPermission = NotificationPreferencesState(
            permissionNotificationPolicy = PermissionNotificationPolicy.OFF,
        )
        val permission = actionEvent("permission", "turnhash00001")
        val completion = permission.copy(
            eventId = "completion",
            status = "completed",
            category = AgentEventSemantics.CATEGORY_COMPLETION,
            actionType = AgentEventSemantics.ACTION_NONE,
        )

        assertTrue(!AgentBellNotificationManager.shouldNotify(permission, disabledPermission))
        assertTrue(AgentBellNotificationManager.shouldNotify(completion, disabledPermission))
    }

    @Test fun permissionAlwaysNotifyIsIndependentOfGenericActionSetting() {
        val preferences = NotificationPreferencesState(
            notifyActionRequired = false,
            permissionNotificationPolicy = PermissionNotificationPolicy.ALWAYS_NOTIFY,
        )

        assertTrue(
            AgentBellNotificationManager.shouldNotify(
                actionEvent("permission", "turnhash00001"),
                preferences,
            ),
        )
    }

    @Test fun permissionPolicyDefaultsOffAndLegacyTrueMigratesOff() {
        assertEquals(
            PermissionNotificationPolicy.OFF,
            NotificationPreferencesState().permissionNotificationPolicy,
        )
        assertEquals(
            PermissionNotificationPolicy.OFF,
            PermissionNotificationPolicy.migrate(null, legacyEnabled = true),
        )
    }

    @Test fun replySettingDoesNotDisableGenericAttention() {
        val preferences = NotificationPreferencesState(replyAndConfirmationRequests = false)
        val input = actionEvent("input", "turnhash00001").copy(
            actionType = AgentEventSemantics.ACTION_INPUT_REQUIRED,
        )
        val attention = input.copy(actionType = AgentEventSemantics.ACTION_ATTENTION_REQUIRED)

        assertTrue(!AgentBellNotificationManager.shouldNotify(input, preferences))
        assertTrue(AgentBellNotificationManager.shouldNotify(attention, preferences))
    }

    @Test fun resolvedPermissionDoesNotNotifyAndKeepsTheOriginalNotificationIdentity() {
        val permission = actionEvent("permission", "turnhash00001")
        val resolved = permission.copy(
            sequence = 2,
            resolvedAt = "2026-08-06T00:00:02Z",
        )

        assertTrue(
            !AgentBellNotificationManager.shouldNotify(
                resolved,
                NotificationPreferencesState(),
            ),
        )
        assertEquals(
            AgentBellNotificationManager.actionNotificationId(permission),
            AgentBellNotificationManager.actionNotificationId(resolved),
        )
    }

    private fun actionEvent(eventId: String, turnHash: String) = AgentEvent(
        eventId = eventId,
        agent = "codex",
        status = "action_required",
        title = "safe",
        category = AgentEventSemantics.CATEGORY_ACTION_REQUIRED,
        actionType = AgentEventSemantics.ACTION_PERMISSION_REQUIRED,
        toolCategory = "command",
        project = "AgentBell",
        summary = null,
        turnIdHash = turnHash,
        occurredAt = "2026-08-06T00:00:00Z",
        sequence = 1,
    )
}
