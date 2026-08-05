package com.hyatin.agentbell.connection

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

class ReconnectPolicyTest {
    @Test fun emitsBoundedBackoffSequence() {
        val policy = ReconnectPolicy()
        assertArrayEquals(intArrayOf(1, 2, 5, 10, 30, 30), IntArray(6) { policy.nextDelaySeconds() })
    }

    @Test fun successResetReturnsToOneSecond() {
        val policy = ReconnectPolicy()
        policy.nextDelaySeconds()
        policy.nextDelaySeconds()
        policy.reset()
        assertEquals(1, policy.nextDelaySeconds())
    }
}
