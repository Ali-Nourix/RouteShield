package com.nourix.routeshield
import org.json.JSONArray
import org.json.JSONObject

/** Offline, exact package IDs only; nationality/name is never inferred from labels. */
object AppRoutingPolicy {
    val domesticPackages = setOf("com.digikala", "cab.snapp.passenger", "cab.snapp.passenger.play",
        "com.zoodfood.android", "com.sheypoor.mobile", "com.snapp_box.android", "ir.divar", "com.aparat")
    private fun strings(data:JSONObject,key:String):Set<String> = data.optJSONArray(key)?.let { a -> (0 until a.length()).map {a.getString(it)}.toSet() } ?: emptySet()
    fun enabled(data:JSONObject,pkg:String):Boolean {
        val manual=strings(data,"apps")
        if(data.optString("mode","all")=="include") return pkg in manual // preserve explicit older allowlists
        if(pkg in strings(data,"forceVpnApps")) return true
        if(data.optString("mode","all")=="exclude" && pkg in manual) return false
        return !(data.optBoolean("autoDomesticApps") && pkg in domesticPackages)
    }
    fun excluded(data:JSONObject,installed:Set<String>,ownPackage:String):Set<String> = installed.filter {it!=ownPackage && !enabled(data,it)}.toSet()
    fun change(data:JSONObject,pkg:String,on:Boolean) {
        val apps=(if(data.optString("mode","all")=="all") emptySet() else strings(data,"apps")).toMutableSet(); val forced=strings(data,"forceVpnApps").toMutableSet()
        if(data.optString("mode","all")=="include") { if(on) apps.add(pkg) else apps.remove(pkg) }
        else {
            data.put("mode","exclude")
            if(on) { apps.remove(pkg); forced.add(pkg) } else { apps.add(pkg); forced.remove(pkg) }
        }
        data.put("apps",JSONArray(apps.sorted())).put("forceVpnApps",JSONArray(forced.sorted()))
    }
    fun isAutomatic(data:JSONObject,pkg:String):Boolean = data.optString("mode","all")!="include" && data.optBoolean("autoDomesticApps") &&
        pkg in domesticPackages && pkg !in strings(data,"forceVpnApps") && pkg !in strings(data,"apps")
}
