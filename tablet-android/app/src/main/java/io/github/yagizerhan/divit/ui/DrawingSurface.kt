// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// DrawingSurface + PenCapture (architecture §2): full-screen drawing surface. Only the pen is accepted; fingers are
// ignored (IN-04). Unbuffered dispatch: requested once attached to the window and at the start of every gesture.

package io.github.yagizerhan.divit.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Rect
import android.view.InputDevice
import android.view.MotionEvent
import android.view.View
import io.github.yagizerhan.divit.capture.PenNormalizer
import io.github.yagizerhan.divit.capture.SampleRing
import io.github.yagizerhan.divit.protocol.Incoming

/** Where the pen is, for the status pill (fades while the pen is near). */
enum class PenPresence { NONE, HOVER, CONTACT, OUT_OF_RANGE }

class DrawingSurface(
    context: Context,
    ring: SampleRing,
    private val onSurface: (wPx: Int, hPx: Int, wMm: Float, hMm: Float) -> Unit,
) : View(context) {

    private val normalizer = PenNormalizer(ring)
    var area: Incoming.HostStatus? = null
        set(v) { field = v; invalidate() }

    /** For the UI (read at most ~10 Hz). */
    @Volatile var penPresence = PenPresence.NONE; private set

    private val deadPaint = Paint().apply { color = Color.argb(150, 0, 0, 0) }
    private val framePaint = Paint().apply { color = Color.argb(90, 120, 170, 255); style = Paint.Style.STROKE; strokeWidth = 3f }

    init {
        setBackgroundColor(Color.rgb(24, 26, 30))
        isFocusable = false
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        // Called from init it had no effect; it must be called once the view is attached to the window.
        requestUnbufferedDispatch(InputDevice.SOURCE_CLASS_POINTER)
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        normalizer.width = w.toFloat()
        normalizer.height = h.toFloat()
        val dm = resources.displayMetrics
        onSurface(w, h, w / dm.xdpi * 25.4f, h / dm.ydpi * 25.4f)
        // Keep edge gestures (back / notifications) out of the drawing: exclude the left and right edges.
        val edge = (48 * dm.density).toInt()
        systemGestureExclusionRects = listOf(Rect(0, 0, edge, h), Rect(w - edge, 0, w, h))
    }

    override fun onTouchEvent(e: MotionEvent): Boolean {
        if (!normalizer.isPen(e)) return false        // finger: ignore (palm rejection)
        if (e.actionMasked == MotionEvent.ACTION_DOWN) requestUnbufferedDispatch(e)
        normalizer.onTouch(e)
        penPresence = when (e.actionMasked) { MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> PenPresence.HOVER; else -> PenPresence.CONTACT }
        return true
    }

    override fun onGenericMotionEvent(e: MotionEvent): Boolean {
        if (!normalizer.isPen(e)) return super.onGenericMotionEvent(e)
        // Requesting only on HOVER_ENTER did not free hover from 90 Hz batching (it was enough for contact).
        // A hover has no clear "gesture", so the request is renewed on every hover event.
        if (e.actionMasked != MotionEvent.ACTION_HOVER_EXIT) requestUnbufferedDispatch(e)
        normalizer.onHover(e)
        penPresence = if (e.actionMasked == MotionEvent.ACTION_HOVER_EXIT) PenPresence.OUT_OF_RANGE else PenPresence.HOVER
        return true
    }

    override fun onDraw(canvas: Canvas) {
        // The area the host uses (after aspect-ratio cropping): darken the rest, outline its border.
        val a = area ?: return
        val l = a.left * width; val t = a.top * height; val r = a.right * width; val b = a.bottom * height
        canvas.drawRect(0f, 0f, width.toFloat(), t, deadPaint)
        canvas.drawRect(0f, b, width.toFloat(), height.toFloat(), deadPaint)
        canvas.drawRect(0f, t, l, b, deadPaint)
        canvas.drawRect(r, t, width.toFloat(), b, deadPaint)
        canvas.drawRect(l, t, r, b, framePaint)
    }
}
