package com.hyatin.agentbell.security

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

class ManifestSecurityTest {
    @Test fun manifestContainsOnlyReviewedPermissions() {
        val manifest = File("src/main/AndroidManifest.xml").readText()
        val permissions = Regex("<uses-permission android:name=\"([^\"]+)\"")
            .findAll(manifest)
            .map { it.groupValues[1] }
            .toSet()
        assertEquals(
            setOf(
                "android.permission.INTERNET",
                "android.permission.ACCESS_NETWORK_STATE",
                "android.permission.CHANGE_NETWORK_STATE",
                "android.permission.CAMERA",
                "android.permission.POST_NOTIFICATIONS",
                "android.permission.FOREGROUND_SERVICE",
                "android.permission.FOREGROUND_SERVICE_CONNECTED_DEVICE",
            ),
            permissions,
        )
        assertFalse(manifest.contains("ACCESS_FINE_LOCATION"))
        assertFalse(manifest.contains("SYSTEM_ALERT_WINDOW"))
        assertFalse(manifest.contains("REQUEST_IGNORE_BATTERY_OPTIMIZATIONS"))
    }

    @Test fun serviceIsPrivateAndUsesConnectedDeviceType() {
        val manifest = File("src/main/AndroidManifest.xml").readText()
        assertTrue(manifest.contains("android:foregroundServiceType=\"connectedDevice\""))
        val service = Regex("<service[\\s\\S]*?AgentBellConnectionService[\\s\\S]*?/>")
            .find(manifest)?.value.orEmpty()
        assertTrue(service.contains("android:exported=\"false\""))
        assertTrue(service.contains("android:stopWithTask=\"false\""))
    }
}
