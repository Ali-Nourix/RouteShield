package com.nourix.routeshield.designsystem

import androidx.compose.ui.graphics.Color

/** Semantic success is distinct from the neutral offline control in every seed palette. */
object ConnectionFeedback {
    fun fill(dark:Boolean)=if(dark) Color(0xFF8DE5A4) else Color(0xFF176B3A)
    fun content(dark:Boolean)=if(dark) Color(0xFF00391A) else Color.White
}
