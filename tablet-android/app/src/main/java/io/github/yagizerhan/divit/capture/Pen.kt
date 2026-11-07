// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Divit contributors
// PenNormalizer + lock-free ring buffer (architecture §2).
// The main thread turns MotionEvents into samples and writes them to the ring; the TX thread reads. No locks, no allocation.

package io.github.yagizerhan.divit.capture

import android.view.MotionEvent
import io.github.yagizerhan.divit.protocol.Wire
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.atan
import kotlin.math.cos
import kotlin.math.min
import kotlin.math.sin
import kotlin.math.tan

/** Single-producer / single-consumer ring buffer. When full the new sample is dropped and counted (real time: we never wait). */
class SampleRing(capacityPow2: Int = 1024) {
    private val mask = capacityPow2 - 1
    val t = LongArray(capacityPow2)
    val u = FloatArray(capacityPow2)
    val v = FloatArray(capacityPow2)
    val p = FloatArray(capacityPow2)
    val tx = FloatArray(capacityPow2)
    val ty = FloatArray(capacityPow2)
    val flags = IntArray(capacityPow2)
    val seq = IntArray(capacityPow2)

    private val head = AtomicLong()   // written by the producer
    private val tail = AtomicLong()   // written by the consumer
    private var nextSeq = 0
    val dropped = AtomicInteger()
    @Volatile var consumer: Thread? = null

    init { require(capacityPow2 and mask == 0) }

    fun push(tUs: Long, uu: Float, vv: Float, pp: Float, tiltX: Float, tiltY: Float, f: Int) {
        val h = head.get()
        if (h - tail.get() > mask) { dropped.incrementAndGet(); return }
        val i = (h and mask.toLong()).toInt()
        t[i] = tUs; u[i] = uu; v[i] = vv; p[i] = pp; tx[i] = tiltX; ty[i] = tiltY; flags[i] = f
        seq[i] = nextSeq; nextSeq = (nextSeq + 1) and 0xFFFF
        head.lazySet(h + 1)
    }

    /** Notify: wake the TX thread if it is parked (lock-free). */
    fun signal() { consumer?.let { java.util.concurrent.locks.LockSupport.unpark(it) } }

    fun available(): Int = (head.get() - tail.get()).toInt()
    fun indexAt(k: Int): Int = ((tail.get() + k) and mask.toLong()).toInt()
    fun consume(n: Int) { tail.lazySet(tail.get() + n) }
    /** Drop stale samples when the connection is re-established (called by the consumer). */
    fun clear() { tail.set(head.get()) }
}

/**
 * MotionEvent → normalized samples (SPEC §3.6). Clock: MotionEvent times use the `SystemClock.uptimeNanos` base;
 * PING/PONG use the same clock.
 */
class PenNormalizer(private val ring: SampleRing) {
    var width = 1f
    var height = 1f
    private var lastU = 0.5f
    private var lastV = 0.5f

    fun isPen(e: MotionEvent): Boolean = e.getToolType(0).let {
        it == MotionEvent.TOOL_TYPE_STYLUS || it == MotionEvent.TOOL_TYPE_ERASER
    }

    /** Contact events (onTouchEvent). */
    fun onTouch(e: MotionEvent) {
        val base = baseFlags(e)
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                for (h in 0 until e.historySize) emit(e, h, base or Wire.IN_RANGE or Wire.IN_CONTACT or Wire.HISTORICAL)
                emit(e, -1, base or Wire.IN_RANGE or Wire.IN_CONTACT)
            }
            // Lift: the pen is still in range (hover). A cancel (e.g. system palm rejection) also counts as a lift.
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> emit(e, -1, base or Wire.IN_RANGE)
        }
        ring.signal()
    }

    /** Hover events (onGenericMotionEvent). */
    fun onHover(e: MotionEvent) {
        val base = baseFlags(e)
        when (e.actionMasked) {
            MotionEvent.ACTION_HOVER_ENTER, MotionEvent.ACTION_HOVER_MOVE -> {
                for (h in 0 until e.historySize) emit(e, h, base or Wire.IN_RANGE or Wire.HISTORICAL)
                emit(e, -1, base or Wire.IN_RANGE)
            }
            MotionEvent.ACTION_HOVER_EXIT -> emit(e, -1, base and Wire.ERASER) // out of range
        }
        ring.signal()
    }

    private fun baseFlags(e: MotionEvent): Int {
        var f = 0
        // The side button does not arrive as ACTION_BUTTON_PRESS; buttonState is read instead.
        if (e.buttonState and MotionEvent.BUTTON_STYLUS_PRIMARY != 0) f = f or Wire.BARREL
        if (e.getToolType(0) == MotionEvent.TOOL_TYPE_ERASER) f = f or Wire.ERASER
        return f
    }

    /** h = -1: the current sample; otherwise a historical sample. */
    private fun emit(e: MotionEvent, h: Int, flags: Int) {
        val cur = h < 0
        val x = if (cur) e.x else e.getHistoricalX(h)
        val y = if (cur) e.y else e.getHistoricalY(h)
        val tNs = if (android.os.Build.VERSION.SDK_INT >= 34) {
            if (cur) e.eventTimeNanos else e.getHistoricalEventTimeNanos(h)
        } else {
            (if (cur) e.eventTime else e.getHistoricalEventTime(h)) * 1_000_000L
        }
        val inRange = flags and Wire.IN_RANGE != 0
        val u = if (inRange) (x / width).coerceIn(0f, 1f) else lastU
        val v = if (inRange) (y / height).coerceIn(0f, 1f) else lastV
        lastU = u; lastV = v
        val pressure = if (flags and Wire.IN_CONTACT != 0) (if (cur) e.pressure else e.getHistoricalPressure(h)).coerceIn(0f, 1f) else 0f

        // Tilt: AXIS_TILT (θ, 0 = upright) + AXIS_ORIENTATION (φ) → Windows tiltX/tiltY (degrees).
        // φ is the direction the tip points (top of pen → tip): φ = 0 when the top leans toward the user.
        // Windows: tiltX + when the top leans right, tiltY + when it leans toward the user [IN-10].
        val theta = min(if (cur) e.getAxisValue(MotionEvent.AXIS_TILT) else e.getHistoricalAxisValue(MotionEvent.AXIS_TILT, h), 1.553f) // < 89°
        val phi = if (cur) e.getAxisValue(MotionEvent.AXIS_ORIENTATION) else e.getHistoricalAxisValue(MotionEvent.AXIS_ORIENTATION, h)
        val tanT = tan(theta)
        val tiltX = Math.toDegrees(atan(-tanT * sin(phi)).toDouble()).toFloat()
        val tiltY = Math.toDegrees(atan(tanT * cos(phi)).toDouble()).toFloat()

        ring.push(tNs / 1000, u, v, pressure, if (inRange) tiltX else 0f, if (inRange) tiltY else 0f, flags)
    }
}
