package com.camarandi.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.os.SystemClock
import android.util.AttributeSet
import android.view.View

/**
 * Cuadrícula de encuadre (sólo en la vista previa del celular; NO se transmite) y el
 * anillo de enfoque. Líneas finas blancas al 40 % de opacidad.
 */
class GridOverlayView @JvmOverloads constructor(
    context: Context, attrs: AttributeSet? = null,
) : View(context, attrs) {

    companion object {
        val NAMES = arrayOf("Sin cuadrícula", "Regla de tercios", "Cuadrícula 4x4", "Cruz central", "Marco seguro 16:9 (90 %)")
    }

    var mode = 0
        set(v) { field = v % NAMES.size; invalidate() }

    private val density = resources.displayMetrics.density
    private val line = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        alpha = (255 * 0.4f).toInt()
        strokeWidth = 1f * density
        style = Paint.Style.STROKE
    }
    private val ring = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        strokeWidth = 1.5f * density
        style = Paint.Style.STROKE
    }

    private var focusX = -1f
    private var focusY = -1f
    private var focusUntil = 0L
    private var focusLocked = false

    fun showFocus(x: Float, y: Float, locked: Boolean) {
        focusX = x; focusY = y; focusLocked = locked
        focusUntil = SystemClock.uptimeMillis() + if (locked) 2500 else 1200
        invalidate()
    }

    override fun onDraw(c: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        when (mode) {
            1 -> grid(c, w, h, 3)
            2 -> grid(c, w, h, 4)
            3 -> {
                val s = minOf(w, h) * 0.06f
                c.drawLine(w / 2 - s, h / 2, w / 2 + s, h / 2, line)
                c.drawLine(w / 2, h / 2 - s, w / 2, h / 2 + s, line)
            }
            4 -> c.drawRect(w * 0.05f, h * 0.05f, w * 0.95f, h * 0.95f, line)
        }
        val now = SystemClock.uptimeMillis()
        if (focusX >= 0 && now < focusUntil) {
            ring.color = if (focusLocked) Color.rgb(255, 202, 40) else Color.WHITE
            val r = 34 * density
            c.drawCircle(focusX, focusY, r, ring)
            postInvalidateDelayed(100)
        }
    }

    private fun grid(c: Canvas, w: Float, h: Float, n: Int) {
        for (i in 1 until n) {
            c.drawLine(w * i / n, 0f, w * i / n, h, line)
            c.drawLine(0f, h * i / n, w, h * i / n, line)
        }
    }
}
