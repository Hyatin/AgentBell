package com.hyatin.agentbell.connection

import com.hyatin.agentbell.CollectingNotificationSink
import com.hyatin.agentbell.InMemoryEventStateStorage
import com.hyatin.agentbell.InMemoryPairingCredentialStore
import com.hyatin.agentbell.diagnostics.BoundedAgentBellDiagnostics
import com.hyatin.agentbell.storage.EventHistoryRepository
import com.hyatin.agentbell.testCredential
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class AgentBellWebSocketClientTest {
    private lateinit var server: MockWebServer
    private lateinit var scope: CoroutineScope
    private lateinit var http: OkHttpClient
    private val clients = mutableListOf<AgentBellWebSocketClient>()

    @Before fun setUp() {
        server = MockWebServer()
        server.start()
        scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        http = OkHttpClient.Builder().build()
    }

    @After fun tearDown() {
        clients.forEach(AgentBellWebSocketClient::stop)
        scope.cancel()
        http.dispatcher.executorService.shutdownNow()
        server.shutdown()
    }

    @Test fun bearerHelloAndResumeUseM2ProtocolWithoutQueryToken() {
        val resume = CountDownLatch(1)
        var clientMessage: String? = null
        server.enqueue(webSocketResponse(onOpen = { it.send(hello()) }) { _, text ->
            clientMessage = text
            resume.countDown()
        })
        val eventStorage = InMemoryEventStateStorage()
        val client = createClient(eventStorage = eventStorage)

        client.start()

        val request = server.takeRequest(3, TimeUnit.SECONDS)!!
        assertEquals("Bearer ${"A".repeat(43)}", request.getHeader("Authorization"))
        assertEquals("/ws/v1/events", request.requestUrl?.encodedPath)
        assertNull(request.requestUrl?.query)
        assertTrue(resume.await(3, TimeUnit.SECONDS))
        val resumeMessage = requireNotNull(clientMessage)
        assertEquals(0, JSONObject(resumeMessage).getLong("lastSequence"))
        assertEquals("resume", JSONObject(resumeMessage).getString("type"))
    }

    @Test fun realtimeAndReplayEventsShareDedupePathAndReplyToPing() {
        val pong = CountDownLatch(1)
        val notifications = CollectingNotificationSink()
        server.enqueue(webSocketResponse(onOpen = { it.send(hello()) }) { socket, text ->
            when (JSONObject(text).getString("type")) {
                "resume" -> {
                    val event = event("same-event", 1, "中文完成 👩🏽‍💻")
                    socket.send(event)
                    socket.send(event)
                    socket.send("{not-json")
                    socket.send("""{"type":"future"}""")
                    socket.send("""{"type":"ping","timestamp":1234}""")
                }
                "pong" -> pong.countDown()
            }
        })
        val storage = InMemoryEventStateStorage()
        val client = createClient(storage, notifications)

        client.start()

        assertTrue(pong.await(3, TimeUnit.SECONDS))
        assertEquals(1, notifications.events.size)
        assertEquals("中文完成 👩🏽‍💻", notifications.events.single().summary)
        assertEquals(1, storage.state.lastSequence)
        assertEquals(listOf("same-event"), storage.state.recentEventIds)
    }

    @Test fun actionRequiredEventUsesTheSameDedupeHistoryAndNotificationPath() {
        val pong = CountDownLatch(1)
        val notifications = CollectingNotificationSink()
        server.enqueue(webSocketResponse(onOpen = { it.send(hello()) }) { socket, text ->
            when (JSONObject(text).getString("type")) {
                "resume" -> {
                    val event = actionEvent("codex-action:00112233445566778899aabb", 1)
                    socket.send(event)
                    socket.send(event)
                    socket.send("""{"type":"ping","timestamp":5678}""")
                }
                "pong" -> pong.countDown()
            }
        })
        val storage = InMemoryEventStateStorage()
        val client = createClient(storage, notifications)

        client.start()

        assertTrue(pong.await(3, TimeUnit.SECONDS))
        val event = notifications.events.single()
        assertEquals("action_required", event.category)
        assertEquals("permission_required", event.actionType)
        assertEquals("command", event.toolCategory)
        assertNull(event.summary)
        assertEquals(listOf("codex-action:00112233445566778899aabb"), storage.state.recentEventIds)
    }

    @Test fun protocolMismatchIsTerminalAndDoesNotReconnect() {
        server.enqueue(webSocketResponse(onOpen = { it.send(hello(protocolVersion = 2)) }))
        val states = ConnectionStateRepository()
        val terminal = stateLatch(states) { it is ConnectionState.ProtocolMismatch }
        val client = createClient(states = states)

        client.start()

        assertTrue(terminal.await(3, TimeUnit.SECONDS))
        server.takeRequest(3, TimeUnit.SECONDS)
        assertEquals(1, server.requestCount)
        assertNull(server.takeRequest(500, TimeUnit.MILLISECONDS))
    }

    @Test fun forbiddenResponseIsTerminalAndDoesNotRetryForever() {
        server.enqueue(MockResponse().setResponseCode(403))
        val states = ConnectionStateRepository()
        val terminal = stateLatch(states) { it is ConnectionState.Unauthorized }
        val client = createClient(states = states)

        client.start()

        assertTrue(terminal.await(3, TimeUnit.SECONDS))
        server.takeRequest(3, TimeUnit.SECONDS)
        assertEquals(1, server.requestCount)
        assertNull(server.takeRequest(500, TimeUnit.MILLISECONDS))
    }

    @Test fun serverCloseSchedulesOneBackoffThenReconnects() {
        val delayObserved = CountDownLatch(1)
        val connectedTwice = CountDownLatch(2)
        server.enqueue(webSocketResponse(onOpen = { socket ->
            connectedTwice.countDown()
            socket.send(hello())
            socket.close(1012, "desktop_restart")
        }))
        server.enqueue(webSocketResponse(onOpen = { socket ->
            connectedTwice.countDown()
            socket.send(hello())
        }))
        val client = createClient(
            reconnectDelay = ReconnectDelay {
                assertEquals(1, it)
                delayObserved.countDown()
            },
        )

        client.start()

        assertTrue(delayObserved.await(3, TimeUnit.SECONDS))
        assertTrue(connectedTwice.await(3, TimeUnit.SECONDS))
        assertEquals(2, server.requestCount)
    }

    @Test fun stopPreventsReconnectAfterSocketCloses() {
        val opened = CountDownLatch(1)
        var serverSocket: WebSocket? = null
        server.enqueue(webSocketResponse(onOpen = { socket ->
            serverSocket = socket
            socket.send(hello())
            opened.countDown()
        }))
        val client = createClient(reconnectDelay = ReconnectDelay { error("must_not_retry") })
        client.start()
        assertTrue(opened.await(3, TimeUnit.SECONDS))
        server.takeRequest(3, TimeUnit.SECONDS)

        client.stop()
        serverSocket?.close(1000, "done")

        assertNull(server.takeRequest(500, TimeUnit.MILLISECONDS))
    }

    @Test fun noNetworkPausesConnectionAndRecoveryConnectsImmediatelyOnce() {
        val connected = CountDownLatch(1)
        server.enqueue(webSocketResponse(onOpen = { socket ->
            socket.send(hello())
            connected.countDown()
        }))
        val states = ConnectionStateRepository()
        val client = createClient(states = states)

        client.onNetworkLost()
        client.start()
        client.start()
        assertTrue(states.state.value is ConnectionState.NoNetwork)
        assertNull(server.takeRequest(300, TimeUnit.MILLISECONDS))

        client.onNetworkAvailable()
        client.onNetworkAvailable()
        assertTrue(connected.await(3, TimeUnit.SECONDS))
        assertEquals(1, server.requestCount)
    }

    private fun createClient(
        eventStorage: InMemoryEventStateStorage = InMemoryEventStateStorage(),
        notifications: CollectingNotificationSink = CollectingNotificationSink(),
        states: ConnectionStateRepository = ConnectionStateRepository(),
        reconnectDelay: ReconnectDelay = ReconnectDelay { },
    ): AgentBellWebSocketClient {
        val repository = EventHistoryRepository(eventStorage, InMemoryPairingCredentialStore())
        val credential = testCredential(host = server.hostName, port = server.port)
        return AgentBellWebSocketClient(
            credential,
            http,
            repository,
            notifications,
            states,
            BoundedAgentBellDiagnostics(),
            scope,
            reconnectDelay,
        ).also(clients::add)
    }

    private fun stateLatch(
        states: ConnectionStateRepository,
        predicate: (ConnectionState) -> Boolean,
    ): CountDownLatch {
        val latch = CountDownLatch(1)
        scope.launch { states.state.collect { if (predicate(it)) latch.countDown() } }
        return latch
    }

    private fun webSocketResponse(
        onOpen: (WebSocket) -> Unit,
        onMessage: (WebSocket, String) -> Unit = { _, _ -> },
    ): MockResponse = MockResponse().withWebSocketUpgrade(
        object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) = onOpen(webSocket)
            override fun onMessage(webSocket: WebSocket, text: String) = onMessage(webSocket, text)
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(code, null)
            }
        },
    )

    private fun hello(protocolVersion: Int = 1): String =
        """{"type":"hello","protocolVersion":$protocolVersion,"serverVersion":"0.2.0","deviceName":"测试电脑","deviceId":"device-id","latestSequence":9,"serverTime":"2026-08-03T00:00:00Z"}"""

    private fun event(eventId: String, sequence: Long, summary: String): String =
        """{"type":"event","payload":{"eventId":"$eventId","agent":"codex","status":"completed","title":"Codex 已完成当前回合","project":"AgentBell","summary":"$summary","occurredAt":"2026-08-03T00:00:00Z","sequence":$sequence}}"""

    private fun actionEvent(eventId: String, sequence: Long): String =
        """{"type":"event","payload":{"eventId":"$eventId","agent":"codex","status":"action_required","title":"Codex action required","category":"action_required","actionType":"permission_required","toolCategory":"command","project":"AgentBell","summary":null,"threadIdHash":"001122334455","turnIdHash":"66778899aabb","occurredAt":"2026-08-06T00:00:00Z","sequence":$sequence}}"""
}
