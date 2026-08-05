package com.hyatin.agentbell.storage

import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.longPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import com.hyatin.agentbell.protocol.AgentEvent
import kotlinx.coroutines.flow.first
import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

class DataStoreEventStateStorage(
    private val dataStore: DataStore<Preferences>,
) : EventStateStorage {
    override suspend fun read(): StoredEventState {
        val preferences = dataStore.data.first()
        return try {
            StoredEventState(
                recentEventIds = parseIds(preferences[Keys.EVENT_IDS].orEmpty()),
                recentEvents = parseEvents(preferences[Keys.EVENTS].orEmpty()),
                lastSequence = (preferences[Keys.LAST_SEQUENCE] ?: 0).coerceAtLeast(0),
            )
        } catch (_: JSONException) {
            StoredEventState(emptyList(), emptyList(), 0)
        }
    }

    override suspend fun write(value: StoredEventState) {
        dataStore.edit { preferences ->
            preferences[Keys.EVENT_IDS] = JSONArray(value.recentEventIds).toString()
            preferences[Keys.EVENTS] = eventsToJson(value.recentEvents).toString()
            preferences[Keys.LAST_SEQUENCE] = value.lastSequence
        }
    }

    override suspend fun clear() {
        dataStore.edit { it.clear() }
    }

    private fun parseIds(json: String): List<String> {
        if (json.isBlank()) return emptyList()
        val array = JSONArray(json)
        return buildList {
            for (index in 0 until array.length()) {
                val value = array.opt(index) as? String ?: continue
                if (value.isNotBlank() && value.length <= 256) add(value)
            }
        }.takeLast(EventHistoryRepository.MAX_EVENT_IDS)
    }

    private fun parseEvents(json: String): List<AgentEvent> {
        if (json.isBlank()) return emptyList()
        val array = JSONArray(json)
        return buildList {
            for (index in 0 until array.length()) {
                val item = array.optJSONObject(index) ?: continue
                val eventId = item.optionalString("eventId", 256) ?: continue
                val agent = item.optionalString("agent", 32) ?: continue
                val status = item.optionalString("status", 32) ?: continue
                val title = item.optionalString("title", 256) ?: continue
                val occurredAt = item.optionalString("occurredAt", 64) ?: continue
                val sequence = item.strictLong("sequence") ?: continue
                add(
                    AgentEvent(
                        eventId = eventId,
                        agent = agent,
                        status = status,
                        title = title,
                        project = item.nullableString("project", 256),
                        summary = item.nullableString("summary", 1024),
                        occurredAt = occurredAt,
                        sequence = sequence,
                    ),
                )
            }
        }.takeLast(EventHistoryRepository.MAX_EVENTS)
    }

    private fun eventsToJson(events: List<AgentEvent>): JSONArray = JSONArray().apply {
        events.takeLast(EventHistoryRepository.MAX_EVENTS).forEach { event ->
            put(
                JSONObject()
                    .put("eventId", event.eventId)
                    .put("agent", event.agent)
                    .put("status", event.status)
                    .put("title", event.title)
                    .put("project", event.project ?: JSONObject.NULL)
                    .put("summary", event.summary ?: JSONObject.NULL)
                    .put("occurredAt", event.occurredAt)
                    .put("sequence", event.sequence),
            )
        }
    }

    private fun JSONObject.optionalString(name: String, max: Int): String? {
        val value = opt(name) as? String ?: return null
        return value.trim().takeIf { it.isNotEmpty() && it.length <= max }
    }

    private fun JSONObject.nullableString(name: String, max: Int): String? {
        if (!has(name) || isNull(name)) return null
        return optionalString(name, max)
    }

    private fun JSONObject.strictLong(name: String): Long? = when (val value = opt(name)) {
        is Int -> value.toLong()
        is Long -> value
        else -> null
    }

    private object Keys {
        val EVENT_IDS = stringPreferencesKey("recent_event_ids")
        val EVENTS = stringPreferencesKey("recent_events")
        val LAST_SEQUENCE = longPreferencesKey("last_sequence")
    }
}
