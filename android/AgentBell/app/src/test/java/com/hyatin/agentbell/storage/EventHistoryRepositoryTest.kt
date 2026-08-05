package com.hyatin.agentbell.storage

import com.hyatin.agentbell.InMemoryEventStateStorage
import com.hyatin.agentbell.InMemoryPairingCredentialStore
import com.hyatin.agentbell.testEvent
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class EventHistoryRepositoryTest {
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
}
