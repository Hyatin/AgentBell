package com.hyatin.agentbell.notification

import android.app.NotificationManager
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.hyatin.agentbell.protocol.AgentEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.assertFalse
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class NotificationInstrumentationTest {
    @Test fun channelsHaveLowAndHighImportanceAndPermissionRefusalDoesNotCrash() {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val notifications = AgentBellNotificationManager(context)
        notifications.createChannels()
        val manager = context.getSystemService(NotificationManager::class.java)
        assertEquals(
            NotificationManager.IMPORTANCE_LOW,
            manager.getNotificationChannel(AgentBellNotificationManager.CONNECTION_CHANNEL_ID).importance,
        )
        assertEquals(
            NotificationManager.IMPORTANCE_HIGH,
            manager.getNotificationChannel(AgentBellNotificationManager.COMPLETED_CHANNEL_ID).importance,
        )
        val result = notifications.post(
            AgentEvent(
                eventId = "instrumentation-event",
                agent = "codex",
                status = "completed",
                title = "Codex 已完成当前回合",
                project = "AgentBell",
                summary = "完成 🔔",
                occurredAt = "2026-08-03T00:00:00Z",
                sequence = 1,
            ),
        )
        assertTrue(result || !notifications.hasNotificationPermission())
    }

    @Test fun clickIntentContainsOnlyHashedEventKey() {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val notifications = AgentBellNotificationManager(context)
        val rawEventId = "private-event-id-${"T".repeat(43)}"
        val intent = notifications.completionIntent(rawEventId)
        val key = intent.getStringExtra(AgentBellNotificationManager.EXTRA_EVENT_KEY)
        assertEquals(12, key?.length)
        assertFalse(intent.toUri(0).contains(rawEventId))
        assertFalse(intent.toUri(0).contains("T".repeat(43)))
        assertEquals(setOf(AgentBellNotificationManager.EXTRA_EVENT_KEY), intent.extras?.keySet())
    }
}
