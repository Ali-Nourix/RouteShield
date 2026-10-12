package com.nourix.routeshield
object ThemePalette {
    val seeds=linkedMapOf("brand" to 0xFFEC3013L,"blue" to 0xFF246BFDL,"green" to 0xFF237A48L,
        "purple" to 0xFF7950C2L,"teal" to 0xFF087F8CL,"pink" to 0xFFB83B78L,"amber" to 0xFFB37300L,"slate" to 0xFF556170L)
    fun color(key:String):Long = seeds[key] ?: seeds.getValue("brand")
}
