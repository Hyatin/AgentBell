package com.hyatin.agentbell.notification

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AgentBellNotificationManagerTest {
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
}
