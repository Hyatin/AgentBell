package com.hyatin.agentbell.storage

import android.content.Context
import androidx.datastore.preferences.preferencesDataStore

val Context.agentBellCredentialsDataStore by preferencesDataStore(
    name = "agentbell_credentials",
)

val Context.agentBellEventsDataStore by preferencesDataStore(
    name = "agentbell_events",
)
