package com.hyatin.agentbell.connection

import com.hyatin.agentbell.pairing.PrivateIpv4
import com.hyatin.agentbell.protocol.AgentBellProtocol
import okhttp3.Interceptor
import okhttp3.Response
import java.io.IOException

/**
 * Enforces the dynamic part of the cleartext policy that Android's static network
 * security XML cannot express: numeric RFC1918 hosts and known AgentBell endpoints.
 */
class PrivateLanRequestGuard : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val url = chain.request().url
        val permitted = PrivateIpv4.isPrivate(url.host) &&
            url.port in ALLOWED_PORTS &&
            url.encodedQuery == null &&
            url.encodedPath in ALLOWED_PATHS
        if (!permitted) throw IOException("agentbell_private_endpoint_required")
        return chain.proceed(chain.request())
    }

    private companion object {
        val ALLOWED_PORTS = 17864..17874
        val ALLOWED_PATHS = setOf("/api/v1/status", AgentBellProtocol.WEB_SOCKET_PATH)
    }
}
