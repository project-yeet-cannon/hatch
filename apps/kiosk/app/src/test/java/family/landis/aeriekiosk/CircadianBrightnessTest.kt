package family.landis.aeriekiosk

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.OffsetDateTime
import java.time.ZoneId

/**
 * The phase cases mirror circadianTheme.test.ts one for one, on the same
 * fixture times, because these two implementations agreeing is the whole
 * contract between the backlight and the palette (see CircadianBrightness.kt).
 * A case added there belongs here too.
 */
class CircadianBrightnessTest {

    private fun at(iso: String): Long = OffsetDateTime.parse(iso).toInstant().toEpochMilli()

    /** Dawn 05:30, sunrise 06:00, sunset 20:00, dusk 20:30 - the web fixture. */
    private val events = SunEvents(
        dawnMs = at("2026-06-21T05:30:00Z"),
        sunriseMs = at("2026-06-21T06:00:00Z"),
        sunsetMs = at("2026-06-21T20:00:00Z"),
        duskMs = at("2026-06-21T20:30:00Z"),
    )

    /** Every sun event at once, as polar latitudes can produce. */
    private val degenerate = SunEvents(
        dawnMs = at("2026-06-21T12:00:00Z"),
        sunriseMs = at("2026-06-21T12:00:00Z"),
        sunsetMs = at("2026-06-21T12:00:00Z"),
        duskMs = at("2026-06-21T12:00:00Z"),
    )

    private fun phaseAt(iso: String) = circadianPhase(at(iso), events)

    // --- the table, which has to stay in step with tokens.ts ---

    @Test
    fun `names the same keyframes in the same order as the palette table`() {
        assertEquals(
            listOf(
                "deepNight", "lateNight", "firstLight", "dawnGlow", "sunriseGlow", "morning",
                "day", "dayHold", "goldenHour", "sunsetGlow", "afterglow", "duskFall",
                "night", "deepNightEnd",
            ),
            CIRCADIAN_TIMELINE.map { it.name },
        )
    }

    @Test
    fun `opens and closes on the same level, so midnight is not a seam`() {
        val first = CIRCADIAN_TIMELINE.first()
        val last = CIRCADIAN_TIMELINE.last()
        assertEquals(first.brightness, last.brightness, 0f)
        assertEquals(first.idleBrightness, last.idleBrightness, 0f)
    }

    @Test
    fun `anchors the palette's two inversions to civil dawn and civil dusk`() {
        val times = timelineTimes(events)
        assertEquals(events.dawnMs, times[CIRCADIAN_TIMELINE.indexOfFirst { it.name == "dawnGlow" }])
        assertEquals(events.duskMs, times[CIRCADIAN_TIMELINE.indexOfFirst { it.name == "duskFall" }])
    }

    @Test
    fun `holds keyframe times non-decreasing when the sun events collapse`() {
        val times = timelineTimes(degenerate)
        for (i in 1 until times.size) {
            assertTrue("times[$i] < times[${i - 1}]", times[i] >= times[i - 1])
        }
    }

    // --- phase, case for case with circadianTheme.test.ts ---

    @Test
    fun `clamps to the first keyframe in the small hours before it`() {
        val phase = phaseAt("2026-06-21T00:30:00Z")
        assertEquals(0, phase.index)
        assertEquals(0f, phase.progress, 1e-5f)
    }

    @Test
    fun `is mid-day and flat through the afternoon`() {
        assertEquals("day", phaseAt("2026-06-21T13:00:00Z").name)
        assertEquals("dayHold", phaseAt("2026-06-21T17:20:00Z").name)
        // day and dayHold carry the same level, so the whole span between them
        // is flat rather than merely starting and ending at full.
        assertEquals(1.0f, brightnessFor(phaseAt("2026-06-21T13:00:00Z"), idle = false), 1e-5f)
        assertEquals(1.0f, brightnessFor(phaseAt("2026-06-21T16:00:00Z"), idle = false), 1e-5f)
    }

    @Test
    fun `is still on the dark side in the last instant before civil dawn`() {
        assertEquals("lateNight", phaseAt("2026-06-21T05:29:59Z").name)
    }

    @Test
    fun `lands on the far side of the inversion at civil dawn itself`() {
        assertEquals("dawnGlow", phaseAt("2026-06-21T05:30:00Z").name)
    }

    @Test
    fun `is still on the light side in the last instant before civil dusk`() {
        assertEquals("sunsetGlow", phaseAt("2026-06-21T20:29:59Z").name)
    }

    @Test
    fun `lands on the far side of the inversion at civil dusk itself`() {
        assertEquals("duskFall", phaseAt("2026-06-21T20:30:00Z").name)
    }

    @Test
    fun `runs the golden hour into sunset over the 45 minutes before it`() {
        val phase = phaseAt("2026-06-21T19:37:30Z")
        assertEquals("goldenHour", phase.name)
        assertEquals(0.5f, phase.progress, 1e-5f)
    }

    // --- brightness ---

    @Test
    fun `blends between the two keyframes it sits between`() {
        // Halfway from goldenHour (0.68) to sunsetGlow (0.40).
        assertEquals(0.54f, brightnessFor(phaseAt("2026-06-21T19:37:30Z"), idle = false), 1e-5f)
    }

    @Test
    fun `bottoms out at a true zero when idle at night`() {
        assertEquals(0f, brightnessFor(phaseAt("2026-06-21T02:00:00Z"), idle = true), 0f)
        assertTrue(brightnessFor(phaseAt("2026-06-21T02:00:00Z"), idle = false) > 0f)
    }

    @Test
    fun `never leaves the zero-to-one range across a whole day`() {
        val dayStart = at("2026-06-21T00:00:00Z")
        for (minute in 0 until 24 * 60) {
            val phase = circadianPhase(dayStart + minute * 60_000L, events)
            for (idle in listOf(true, false)) {
                val level = brightnessFor(phase, idle)
                assertTrue("minute $minute idle=$idle -> $level", level in 0f..1f)
            }
        }
    }

    // --- alignToDay: the shell's problem, not the dashboard's ---

    private val utc: ZoneId = ZoneId.of("UTC")

    @Test
    fun `leaves events for today untouched`() {
        assertEquals(events, alignToDay(events, at("2026-06-21T15:00:00Z"), utc, 7))
    }

    @Test
    fun `rolls yesterday's events forward onto today`() {
        val aligned = alignToDay(events, at("2026-06-22T15:00:00Z"), utc, 7)!!
        assertEquals(at("2026-06-22T05:30:00Z"), aligned.dawnMs)
        assertEquals(at("2026-06-22T06:00:00Z"), aligned.sunriseMs)
        assertEquals(at("2026-06-22T20:00:00Z"), aligned.sunsetMs)
        assertEquals(at("2026-06-22T20:30:00Z"), aligned.duskMs)
    }

    /**
     * The regression this function exists for: without the roll, a midday
     * `now` sits past the end of a day-old timeline and reads as the night
     * floor, so a tablet rebooting on a dead network dims itself in full
     * daylight.
     */
    @Test
    fun `day-old events would read as night at midday without rolling`() {
        val noonTomorrow = at("2026-06-22T13:00:00Z")
        assertEquals("deepNightEnd", circadianPhase(noonTomorrow, events).name)
        assertEquals("day", circadianPhase(noonTomorrow, alignToDay(events, noonTomorrow, utc, 7)!!).name)
    }

    @Test
    fun `discards events older than the roll-forward limit`() {
        assertNull(alignToDay(events, at("2026-06-29T15:00:00Z"), utc, 7))
    }

    @Test
    fun `rolls up to the limit inclusive`() {
        assertNotNull(alignToDay(events, at("2026-06-28T15:00:00Z"), utc, 7))
    }

    /** A clock correction backwards shouldn't roll events into the past. */
    @Test
    fun `discards events dated in the future`() {
        assertNull(alignToDay(events, at("2026-06-20T15:00:00Z"), utc, 7))
    }
}
