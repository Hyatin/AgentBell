package com.hyatin.agentbell.storage

import com.hyatin.agentbell.InMemoryEventStateStorage
import com.hyatin.agentbell.InMemoryPairingCredentialStore
import com.hyatin.agentbell.testEvent
import com.hyatin.agentbell.notification.AgentBellNotificationManager
import com.hyatin.agentbell.notification.NotificationPreferencesState
import com.hyatin.agentbell.protocol.AgentEventSemantics
import com.hyatin.agentbell.protocol.ProtocolMessageCodec
import com.hyatin.agentbell.protocol.ProtocolParseResult
import com.hyatin.agentbell.protocol.ServerMessage
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class EventHistoryRepositoryTest {
    @Test fun current07CompletionAndActionFixturesParseStoreAndReachNotificationPolicy() = runTest {
        val completionJson =
            """{"type":"event","payload":{"eventId":"codex:fixture-thread:fixture-turn-1","agent":"codex","status":"completed","category":"completion","actionType":"none","toolCategory":"none","title":"Codex turn completed","project":"AgentBell","summary":"Synthetic completion.","threadIdHash":"fixturethread","turnIdHash":"fixtureturn1","toolUseIdHash":null,"occurredAt":"2026-08-07T00:00:00Z","sequence":41,"resolvedAt":null}}"""
        val actionJson =
            """{"type":"event","payload":{"eventId":"codex-action:fixture-action","agent":"codex","status":"action_required","category":"action_required","actionType":"input_required","toolCategory":"none","title":"Codex action required","project":"AgentBell","summary":null,"threadIdHash":"fixturethread","turnIdHash":"fixtureturn2","toolUseIdHash":null,"occurredAt":"2026-08-07T00:00:01Z","sequence":42,"resolvedAt":null}}"""
        val completion = parse07Event(completionJson)
        val action = parse07Event(actionJson)
        val storage = InMemoryEventStateStorage()
        val repository = EventHistoryRepository(storage, InMemoryPairingCredentialStore())
        repository.initialize()

        assertTrue(repository.process(completion) is EventProcessResult.Accepted)
        assertTrue(repository.process(action) is EventProcessResult.Accepted)

        assertEquals(listOf(action.eventId, completion.eventId), repository.events.value.map { it.eventId })
        assertEquals(listOf(completion.eventId, action.eventId), storage.state.recentEventIds)
        val preferences = NotificationPreferencesState()
        assertTrue(AgentBellNotificationManager.shouldNotify(completion, preferences))
        assertTrue(AgentBellNotificationManager.shouldNotify(action, preferences))
        assertEquals(
            AgentBellNotificationManager.actionNotificationId(action),
            AgentBellNotificationManager.actionNotificationId(action.copy()),
        )
    }

    @Test fun permissionEventIsSuppressedFromHistoryButAdvancesResumeWatermarkWhenPolicyIsOff() =
        runTest {
            val storage = InMemoryEventStateStorage()
            val repository = EventHistoryRepository(
                storage,
                InMemoryPairingCredentialStore(),
            ) { event -> event.actionType != AgentEventSemantics.ACTION_PERMISSION_REQUIRED }
            repository.initialize()
            val permission = testEvent("permission", 4).copy(
                status = "action_required",
                category = AgentEventSemantics.CATEGORY_ACTION_REQUIRED,
                actionType = AgentEventSemantics.ACTION_PERMISSION_REQUIRED,
                toolCategory = "command",
            )

            val result = repository.process(permission)

            assertTrue(result is EventProcessResult.Suppressed)
            assertTrue(repository.events.value.isEmpty())
            assertTrue(storage.state.recentEvents.isEmpty())
            assertEquals(listOf("permission"), storage.state.recentEventIds)
            assertEquals(4, repository.lastSequence())
            assertTrue(repository.process(permission) is EventProcessResult.Duplicate)
        }

    @Test fun newEventIsStoredAndUpdatesSequence() = runTest {
        val eventStorage = InMemoryEventStateStorage()
        val credentialStore = InMemoryPairingCredentialStore()
        val repository = EventHistoryRepository(eventStorage, credentialStore)
        repository.initialize()

        val result = repository.process(testEvent("event-1", 1))

        assertTrue(result is EventProcessResult.Accepted)
        assertEquals(1, credentialStore.lastSequence)
        assertEquals(listOf("event-1"), eventStorage.state.recentEventIds)
    }

    @Test fun duplicateEventIdIsIgnoredAcrossRealtimeAndResume() = runTest {
        val storage = InMemoryEventStateStorage()
        val repository = EventHistoryRepository(storage, InMemoryPairingCredentialStore())
        repository.initialize()
        repository.process(testEvent("same", 2))
        val duplicate = repository.process(testEvent("same", 2))
        assertTrue(duplicate is EventProcessResult.Duplicate)
        assertEquals(1, storage.writeCount)
    }

    @Test fun unknownIdBehindWatermarkIsAcceptedOnceWithoutMovingWatermarkBack() = runTest {
        val repository = EventHistoryRepository(
            InMemoryEventStateStorage(),
            InMemoryPairingCredentialStore(),
        )
        repository.initialize()
        repository.process(testEvent("newer", 10))
        val result = repository.process(testEvent("late-unknown", 7)) as EventProcessResult.Accepted
        assertTrue(result.sequenceWasBehindWatermark)
        assertEquals(10, repository.lastSequence())
    }

    @Test fun retains100IdsAnd50Events() = runTest {
        val storage = InMemoryEventStateStorage()
        val repository = EventHistoryRepository(storage, InMemoryPairingCredentialStore())
        repository.initialize()
        for (index in 1..120) repository.process(testEvent("event-$index", index.toLong()))
        assertEquals(100, storage.state.recentEventIds.size)
        assertEquals(50, storage.state.recentEvents.size)
        assertEquals(120, storage.state.lastSequence)
        assertEquals(120, repository.events.value.first().sequence)
    }

    @Test fun restartRestoresDedupeAndSequence() = runTest {
        val storage = InMemoryEventStateStorage()
        val pairing = InMemoryPairingCredentialStore()
        EventHistoryRepository(storage, pairing).apply {
            initialize()
            process(testEvent("persisted", 42))
        }
        val restored = EventHistoryRepository(storage, pairing)
        restored.initialize()
        assertTrue(restored.process(testEvent("persisted", 42)) is EventProcessResult.Duplicate)
        assertEquals(42, restored.lastSequence())
    }

    @Test fun persistenceFailureStillKeepsInMemoryDedupe() = runTest {
        val storage = InMemoryEventStateStorage().apply { failWrites = true }
        val repository = EventHistoryRepository(storage, InMemoryPairingCredentialStore())
        repository.initialize()
        val first = repository.process(testEvent("memory-only", 1)) as EventProcessResult.Accepted
        val duplicate = repository.process(testEvent("memory-only", 1))
        assertFalse(first.persistenceSucceeded)
        assertTrue(duplicate is EventProcessResult.Duplicate)
    }

    @Test fun concurrentSameEventIsAcceptedOnce() = runTest {
        val storage = InMemoryEventStateStorage()
        val repository = EventHistoryRepository(storage, InMemoryPairingCredentialStore())
        repository.initialize()
        val results = (1..30).map {
            async { repository.process(testEvent("concurrent", 1)) }
        }.awaitAll()
        assertEquals(1, results.count { it is EventProcessResult.Accepted })
        assertEquals(29, results.count { it is EventProcessResult.Duplicate })
    }

    @Test fun resolvedUpdateReplacesPublishedPermissionAndAdvancesWatermark() = runTest {
        val storage = InMemoryEventStateStorage()
        val pairing = InMemoryPairingCredentialStore()
        val repository = EventHistoryRepository(storage, pairing)
        repository.initialize()
        val permission = testEvent("permission", 4).copy(
            status = "action_required",
            category = "action_required",
            actionType = "permission_required",
            toolCategory = "command",
            toolUseIdHash = "abcdef123456",
        )

        assertTrue(repository.process(permission) is EventProcessResult.Accepted)
        val resolved = permission.copy(
            sequence = 5,
            resolvedAt = "2026-08-06T00:00:02Z",
        )
        assertTrue(repository.process(resolved) is EventProcessResult.Accepted)

        assertEquals(1, repository.events.value.size)
        assertEquals("2026-08-06T00:00:02Z", repository.events.value.single().resolvedAt)
        assertEquals(5, repository.lastSequence())
        assertEquals(5, storage.state.lastSequence)
        assertTrue(repository.process(resolved) is EventProcessResult.Duplicate)
    }

    private fun parse07Event(json: String) =
        (((ProtocolMessageCodec.parseServerMessage(json) as ProtocolParseResult.Success).message)
            as ServerMessage.Event).payload
}
