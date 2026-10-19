package com.nourix.routeshield

import android.content.Context
import android.net.ConnectivityManager
import android.net.DnsResolver
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import android.os.CancellationSignal
import java.net.InetAddress
import java.util.concurrent.CompletableFuture
import java.util.concurrent.Executor
import java.util.concurrent.TimeUnit

object VpnHealthNetwork {
    fun find(context:Context):Network? {
        val cm=context.getSystemService(ConnectivityManager::class.java)
        fun isVpn(network:Network)=cm.getNetworkCapabilities(network)?.hasTransport(NetworkCapabilities.TRANSPORT_VPN)==true
        return cm.activeNetwork?.takeIf(::isVpn) ?: cm.allNetworks.firstOrNull(::isVpn)
    }
    /** Cache-bypassing DNS on API 29+, bounded by the overall health round on older phones. */
    fun dnsWorks(network:Network):Boolean {
        val addresses=if(Build.VERSION.SDK_INT>=29) {
            val result=CompletableFuture<List<InetAddress>>();val cancel=CancellationSignal()
            try {
                DnsResolver.getInstance().query(network,"example.com",DnsResolver.FLAG_NO_CACHE_LOOKUP,
                    Executor {it.run()},cancel,object:DnsResolver.Callback<List<InetAddress>> {
                        override fun onAnswer(answer:List<InetAddress>,rcode:Int) {
                            if(rcode==0) result.complete(answer) else result.complete(emptyList())
                        }
                        override fun onError(error:DnsResolver.DnsException) {result.complete(emptyList())}
                    })
                result.get(2500,TimeUnit.MILLISECONDS)
            } finally {cancel.cancel()}
        } else network.getAllByName("example.com").toList()
        return addresses.any { !it.isAnyLocalAddress && !it.isLoopbackAddress && !it.isLinkLocalAddress && !it.isSiteLocalAddress && !it.isMulticastAddress }
    }
}
