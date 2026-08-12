package com.hyatin.agentbell.storage

import com.hyatin.agentbell.protocol.AgentEvent
import com.hyatin.agentbell.protocol.AgentEventSemantics
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

data class StoredEventState(
    val recentEventIds: List<String>,
    val recentEvents: List<AgentEvent>,
    val lastSequence: Long,
)

interface EventStateStorage {
    suspend fun read(): StoredEventState
    suspend fun write(value: StoredEventState)
    suspend fun clear()
}

sealed interface EventProcessResult {
    data class Accepted(
        val event: AgentEvent,
        val persistenceSucceeded: Boolean,
        val sequenceWasBehindWatermark: Boolean,
    ) : EventProcessResult

    data object Duplicate : EventProcessResult
    data class Suppressed(val persistenceSucceeded: Boolean) : EventProcessResult
    data class Invalid(val code: String) : EventProcessResult
}

class EventHistoryRepository(
    private val storage: EventStateStorage,
    private val credentialStore: PairingCredentialStore,
    private val shouldRetain: (AgentEvent) -> Boolean = { true },
) {
    private val mutex = Mutex()
    private val mutableEvents = MutableStateFlow<List<AgentEvent>>(emptyList())
    private var recentEventIds = LinkedHashSet<String>()
    private var lastSequence = 0L
    private var initialized = false

    val events: StateFlow<List<AgentEvent>> = mutableEvents.asStateFlow()

    suspend fun initialize() = mutex.withLock {
        if (initialized) return@withLock
        val restored = try {
            storage.read()
        } catch (_: Exception) {
            StoredEventState(emptyList(), emptyList(), 0)
        }
        recentEventIds = LinkedHashSet(restored.recentEventIds.takeLast(MAX_EVENT_IDS))
        val restoredEvents = restored.recentEvents
            .filter(::isValid)
            .filter(shouldRetain)
            .distinctBy { it.eventId }
            .sortedBy { it.sequence }
            .takeLast(MAX_EVENTS)
        mutableEvents.value = restoredEvents.sortedByDescending { it.sequence }
        lastSequence = maxOf(
            restored.lastSequence.coerceAtLeast(0),
            restoredEvents.maxOfOrNull { it.sequence } ?: 0,
        )
        if (restoredEvents.size != restored.recentEvents.size) {
            try {
                storage.write(
                    StoredEventState(recentEventIds.toList(), restoredEvents, lastSequence),
                )
            } catch (_: Exception) {
                // Retention cleanup is best-effort and cannot block receiving.
            }
        }
        initialized = true
    }

    suspend fun process(event: AgentEvent): EventProcessResult = mutex.withLock {
        if (!initialized) initializeUnsafe()
        if (!isValid(event)) return@withLock EventProcessResult.Invalid("invalid_event")
        if (!shouldRetain(event)) {
            if (event.eventId in recentEventIds) return@withLock EventProcessResult.Duplicate
            recentEventIds.add(event.eventId)
            trimRecentIds()
            lastSequence = maxOf(lastSequence, event.sequence)
            val persisted = try {
                storage.write(
                    StoredEventState(
                        recentEventIds = recentEventIds.toList(),
                        recentEvents = mutableEvents.value.sortedBy { it.sequence },
                        lastSequence = lastSequence,
                    ),
                )
                credentialStore.updateLastSequence(lastSequence)
                true
            } catch (_: Exception) {
                false
            }
            return@withLock EventProcessResult.Suppressed(persisted)
        }
        val existing = mutableEvents.value.firstOrNull { it.eventId == event.eventId }
        val isResolutionUpdate = event.resolvedAt != null &&
            (existing == null || existing.resolvedAt == null || event.sequence > existing.sequence)
        if (event.eventId in recentEventIds && !isResolutionUpdate) {
            return@withLock EventProcessResult.Duplicate
        }

        val behindWatermark = event.sequence <= lastSequence
        recentEventIds.add(event.eventId)
        trimRecentIds()
        val updatedEvents = (mutableEvents.value.filterNot { it.eventId == event.eventId } + event)
            .sortedBy { it.sequence }
            .takeLast(MAX_EVENTS)
        lastSequence = maxOf(lastSequence, event.sequence)
        mutableEvents.value = updatedEvents.sortedByDescending { it.sequence }

        val snapshot = StoredEventState(
            recentEventIds = recentEventIds.toList(),
            recentEvents = updatedEvents,
            lastSequence = lastSequence,
        )
        val persisted = try {
            storage.write(snapshot)
            credentialStore.updateLastSequence(lastSequence)
            true
        } catch (_: Exception) {
            false
        }

        EventProcessResult.Accepted(event, persisted, behindWatermark)
    }

    suspend fun lastSequence(): Long = mutex.withLock {
        if (!initialized) initializeUnsafe()
        lastSequence
    }

    suspend fun clear() = mutex.withLock {
        recentEventIds.clear()
        lastSequence = 0
        mutableEvents.value = emptyList()
        initialized = true
        storage.clear()
    }

    suspend fun removePermissionEvents() = mutex.withLock {
        if (!initialized) initializeUnsafe()
        val retained = mutableEvents.value
            .filterNot {
                it.actionType == AgentEventSemantics.ACTION_PERMISSION_REQUIRED
            }
            .sortedBy { it.sequence }
        if (retained.size == mutableEvents.value.size) return@withLock
        mutableEvents.value = retained.sortedByDescending { it.sequence }
        try {
            storage.write(
                StoredEventState(recentEventIds.toList(), retained, lastSequence),
            )
        } catch (_: Exception) {
            // A settings change must not fail because optional history cleanup failed.
        }
    }

    private suspend fun initializeUnsafe() {
        val restored = try {
            storage.read()
        } catch (_: Exception) {
            StoredEventState(emptyList(), emptyList(), 0)
        }
        recentEventIds = LinkedHashSet(restored.recentEventIds.takeLast(MAX_EVENT_IDS))
        val events = restored.recentEvents
            .filter(::isValid)
            .filter(shouldRetain)
            .distinctBy { it.eventId }
            .sortedBy { it.sequence }
            .takeLast(MAX_EVENTS)
        mutableEvents.value = events.sortedByDescending { it.sequence }
        lastSequence = maxOf(restored.lastSequence, events.maxOfOrNull { it.sequence } ?: 0)
        initialized = true
    }

    private fun trimRecentIds() {
        while (recentEventIds.size > MAX_EVENT_IDS) {
            val first = recentEventIds.firstOrNull() ?: break
            recentEventIds.remove(first)
        }
    }

    private fun isValid(event: AgentEvent): Boolean =
        event.eventId.isNotBlank() && event.eventId.length <= 256 &&
            event.agent == "codex" &&
            event.status in setOf("completed", "action_required") &&
            event.category in setOf(
                AgentEventSemantics.CATEGORY_COMPLETION,
                AgentEventSemantics.CATEGORY_ACTION_REQUIRED,
            ) &&
            event.actionType in AgentEventSemantics.ACTION_TYPES &&
            event.toolCategory in AgentEventSemantics.TOOL_CATEGORIES &&
            event.title.isNotBlank() && event.title.length <= 256 &&
            (event.resolvedAt == null ||
                event.category == AgentEventSemantics.CATEGORY_ACTION_REQUIRED) &&
            event.occurredAt.isNotBlank() && event.occurredAt.length <= 64 &&
            event.sequence > 0

    companion object {
        const val MAX_EVENT_IDS = 100
        const val MAX_EVENTS = 50
    }
}
