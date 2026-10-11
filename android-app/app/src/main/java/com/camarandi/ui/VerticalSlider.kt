package com.camarandi.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.util.AttributeSet
import android.view.MotionEvent
import android.view.View

/** Slider vertical simple (0..1), arriba = máximo. Pensado para el pulgar izquierdo. */
class VerticalSlider @JvmOverloads constructor(
    context: Context, attrs: AttributeSet? = null,
) : View(context, attrs) {

    var value = 0f
        set(v) { field = v.coerceIn(0f, 1f); invalidate() }
    var onChange: ((Float) -> Unit)? = null
    var onTouchActivity: (() -> Unit)? = null

    private val d = resources.displayMetrics.density
    private val track = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.argb(110, 255, 255, 255); strokeWidth = 3 * d; strokeCap = Paint.Cap.ROUND }
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.rgb(255, 202, 40); strokeWidth = 3 * d; strokeCap = Paint.Cap.ROUND }
    private val knob = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE }

    private val pad get() = 14 * d

    override fun onDraw(c: Canvas) {
        val x = width / 2f
        val top = pad
        val bottom = height - pad
        val y = bottom - (bottom - top) * value
        c.drawLine(x, top, x, bottom, track)
        c.drawLine(x, y, x, bottom, fill)
        c.drawCircle(x, y, 9 * d, knob)
    }

    override fun onTouchEvent(e: MotionEvent): Boolean {
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                parent?.requestDisallowInterceptTouchEvent(true)
                val top = pad
                val bottom = height - pad
                value = ((bottom - e.y) / (bottom - top))
                onChange?.invoke(value)
                onTouchActivity?.invoke()
                return true
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                onTouchActivity?.invoke()
                return true
            }
        }
        return super.onTouchEvent(e)
    }
}
