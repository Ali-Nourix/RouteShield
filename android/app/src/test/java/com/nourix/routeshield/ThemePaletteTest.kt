package com.nourix.routeshield
import org.junit.Assert.*
import org.junit.Test
class ThemePaletteTest {
    @Test fun unknown_seed_falls_back_to_brand() {assertEquals(ThemePalette.color("brand"),ThemePalette.color("invalid"))}
    @Test fun palette_has_eight_distinct_opaque_seeds() {assertEquals(8,ThemePalette.seeds.values.toSet().size);ThemePalette.seeds.values.forEach {assertEquals(255L,it shr 24)}}
}
