package com.camarandi.ui

import android.content.Context
import android.util.AttributeSet
import android.view.View
import android.widget.FrameLayout

/** FrameLayout que mantiene 16:9 (lo que se transmite) centrado en la pantalla. */
class AspectFrameLayout @JvmOverloads constructor(
    context: Context, attrs: AttributeSet? = null,
) : FrameLayout(context, attrs) {

    var aspect = 16f / 9f
        set(v) { field = v; requestLayout() }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val w = View.MeasureSpec.getSize(widthMeasureSpec)
        val h = View.MeasureSpec.getSize(heightMeasureSpec)
        var cw = w
        var ch = (w / aspect).toInt()
        if (ch > h) { ch = h; cw = (h * aspect).toInt() }
        super.onMeasure(
            View.MeasureSpec.makeMeasureSpec(cw, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(ch, View.MeasureSpec.EXACTLY),
        )
    }
}
