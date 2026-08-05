package com.hyatin.agentbell.service

import android.content.Context
import android.content.Intent
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.rule.ServiceTestRule
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ConnectionServiceInstrumentationTest {
    @get:Rule val serviceRule = ServiceTestRule()

    @Test fun unpairedServiceStartsAndStopsWithoutCrashLoop() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        serviceRule.startService(Intent(context, AgentBellConnectionService::class.java))
        AgentBellConnectionService.stop(context)
    }
}
