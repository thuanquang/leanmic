package com.leanmic.app

import android.content.Context
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.SystemClock
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.LinearLayout
import android.widget.TextView
import kotlin.math.log10
import kotlin.math.max
import kotlin.math.min

/** LeanMic colors; follows the system light/dark setting (same palette as the PC app). */
class Palette(val dark: Boolean) {
    val bg = if (dark) 0xFF141618.toInt() else 0xFFF3F4F6.toInt()
    val card = if (dark) 0xFF202327.toInt() else 0xFFFFFFFF.toInt()
    val border = if (dark) 0xFF2E3238.toInt() else 0xFFE3E5E9.toInt()
    val hover = if (dark) 0xFF2A2E33.toInt() else 0xFFEDEFF2.toInt()
    val text = if (dark) 0xFFECEEF1.toInt() else 0xFF1C1F23.toInt()
    val sub = if (dark) 0xFF9AA1AA.toInt() else 0xFF667080.toInt()
    val accent = if (dark) 0xFF3CC283.toInt() else 0xFF1A9A5F.toInt()
    val accentSoft = if (dark) 0xFF1C3A2C.toInt() else 0xFFD9F2E6.toInt()
    val onAccent = if (dark) 0xFF0B1F15.toInt() else Color.WHITE
    val track = if (dark) 0xFF3A3F46.toInt() else 0xFFD9DCE1.toInt()
    val warn = if (dark) 0xFFF2A93B.toInt() else 0xFFB86E00.toInt()
    val warnSoft = if (dark) 0xFF3A2E1A.toInt() else 0xFFFFF1DC.toInt()

    companion object {
        fun of(ctx: Context) = Palette(
            (ctx.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES
        )
    }
}

fun Context.dp(v: Float) = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, resources.displayMetrics)
fun Context.dpi(v: Int) = dp(v.toFloat()).toInt()

fun rounded(color: Int, radius: Float, stroke: Int = 0, strokeWidth: Int = 0) = GradientDrawable().apply {
    setColor(color)
    cornerRadius = radius
    if (strokeWidth > 0) setStroke(strokeWidth, stroke)
}

fun withAlpha(color: Int, alpha: Int) = (color and 0x00FFFFFF) or (alpha.coerceIn(0, 255) shl 24)

/** A rounded card container. */
fun Context.card(p: Palette) = LinearLayout(this).apply {
    orientation = LinearLayout.VERTICAL
    background = rounded(p.card, dp(18f), p.border, dpi(1))
    setPadding(dpi(18), dpi(14), dpi(18), dpi(14))
}

fun Context.label(text: String, p: Palette, sizeSp: Float = 15f, bold: Boolean = false, color: Int = p.text) =
    TextView(this).apply {
        this.text = text
        textSize = sizeSp
        setTextColor(color)
        if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
    }

/** Two-option segmented control (Wi-Fi | USB). */
class Segmented(ctx: Context, private val p: Palette, labels: List<String>, private val onSelect: (Int) -> Unit) :
    LinearLayout(ctx) {

    private val options = labels.mapIndexed { i, text ->
        TextView(ctx).apply {
            this.text = text
            textSize = 15f
            gravity = Gravity.CENTER
            setPadding(0, ctx.dpi(10), 0, ctx.dpi(10))
            setOnClickListener { if (isEnabled) { select(i); onSelect(i) } }
        }
    }
    var selected = 0
        private set

    init {
        orientation = HORIZONTAL
        background = rounded(p.hover, ctx.dp(14f))
        // (selected option: accent-tinted pill, see select())
        setPadding(ctx.dpi(4), ctx.dpi(4), ctx.dpi(4), ctx.dpi(4))
        options.forEach { addView(it, LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f)) }
        select(0)
    }

    fun select(i: Int) {
        selected = i
        options.forEachIndexed { k, v ->
            val on = k == i
            v.background = if (on) rounded(p.accentSoft, context.dp(11f)) else null
            v.setTextColor(if (on) p.accent else p.sub)
            v.typeface = if (on) Typeface.create("sans-serif-medium", Typeface.NORMAL) else Typeface.DEFAULT
            v.isSelected = on
            v.contentDescription = "${v.text}${if (on) ", selected" else ""}"
        }
    }

    override fun setEnabled(enabled: Boolean) {
        super.setEnabled(enabled)
        options.forEach { it.isEnabled = enabled }
        alpha = if (enabled) 1f else 0.5f
    }
}

/**
 * The big round Start/Stop button. Gray when idle, pulsing while looking for the PC,
 * green with a voice-reactive glow while live, amber on errors.
 */
class MicButton(ctx: Context, private val p: Palette) : View(ctx) {
    var state = MicService.State.IDLE
        set(v) {
            field = v
            contentDescription = when (v) {
                MicService.State.IDLE, MicService.State.ERROR -> "Start streaming the microphone"
                else -> "Stop streaming the microphone"
            }
            invalidate()
        }

    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val glyph = ctx.getDrawable(R.drawable.ic_mic)!!.mutate()
    private val start = SystemClock.uptimeMillis()
    private var shown = 0f
    private var pressedScale = 1f

    init {
        isClickable = true
        isFocusable = true
        state = MicService.State.IDLE
    }

    override fun onMeasure(w: Int, h: Int) {
        val size = context.dpi(200)
        setMeasuredDimension(resolveSize(size, w), resolveSize(size, h))
    }

    override fun setPressed(pressed: Boolean) {
        super.setPressed(pressed)
        pressedScale = if (pressed) 0.95f else 1f
        invalidate()
    }

    override fun onDraw(c: Canvas) {
        val cx = width / 2f
        val cy = height / 2f
        val r = min(width, height) * 0.29f * pressedScale
        val t = (SystemClock.uptimeMillis() - start) / 1000.0
        val s = state

        val core = when (s) {
            MicService.State.STREAMING -> p.accent
            MicService.State.ERROR -> p.warn
            MicService.State.IDLE -> p.card
            else -> p.accentSoft
        }

        when (s) {
            MicService.State.IDLE, MicService.State.ERROR -> {
                // Quiet outline ring inviting a tap.
                paint.style = Paint.Style.STROKE
                paint.strokeWidth = context.dp(2f)
                paint.color = if (s == MicService.State.ERROR) withAlpha(p.warn, 120) else p.border
                c.drawCircle(cx, cy, r + context.dp(12f), paint)
            }
            MicService.State.STREAMING -> {
                // Voice glow: ignore the quiet room floor, emphasise speech peaks.
                val lv = MicService.level
                val db = if (lv <= 0.001f) 0f else ((20 * log10(lv) + 60) / 60f).coerceIn(0f, 1f)
                val glow = ((db - 0.3f) / 0.7f).coerceIn(0f, 1f).let { it * it }
                shown = if (glow > shown) shown + (glow - shown) * 0.5f else max(glow, shown - 0.03f)
                paint.style = Paint.Style.FILL
                for (i in 2 downTo 1) {
                    val rr = r + context.dp(6f) + shown * context.dp(if (i == 2) 34f else 18f)
                    paint.color = withAlpha(p.accent, ((if (i == 2) 45 else 85) * min(1f, 0.3f + shown)).toInt())
                    c.drawCircle(cx, cy, rr, paint)
                }
            }
            else -> {
                // Searching / connecting: soft ripples.
                paint.style = Paint.Style.STROKE
                paint.strokeWidth = context.dp(2f)
                for (i in 0 until 2) {
                    val phase = ((t / 2.2 + i * 0.5) % 1.0).toFloat()
                    paint.color = withAlpha(p.accent, (110 * (1 - phase)).toInt())
                    c.drawCircle(cx, cy, r + context.dp(8f) + phase * context.dp(32f), paint)
                }
            }
        }

        paint.style = Paint.Style.FILL
        paint.color = core
        c.drawCircle(cx, cy, r, paint)
        if (s == MicService.State.IDLE) {
            paint.style = Paint.Style.STROKE
            paint.strokeWidth = context.dp(1f)
            paint.color = p.border
            c.drawCircle(cx, cy, r, paint)
        }

        val g = (r * 0.78f).toInt()
        glyph.setTint(
            when (s) {
                MicService.State.STREAMING -> p.onAccent
                MicService.State.ERROR -> Color.WHITE
                MicService.State.IDLE -> p.text
                else -> p.accent
            }
        )
        glyph.setBounds((cx - g / 2).toInt(), (cy - g / 2).toInt(), (cx + g / 2).toInt(), (cy + g / 2).toInt())
        glyph.draw(c)

        // Keep animating only while something moves.
        if (s != MicService.State.IDLE && s != MicService.State.ERROR && isAttachedToWindow) postInvalidateOnAnimation()
    }
}

/** Switch colors for the palette. */
fun switchTints(p: Palette): Pair<ColorStateList, ColorStateList> {
    val states = arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf())
    val thumb = ColorStateList(states, intArrayOf(Color.WHITE, if (p.dark) 0xFFC9CDD2.toInt() else Color.WHITE))
    val track = ColorStateList(states, intArrayOf(p.accent, p.track))
    return thumb to track
}
