package family.landis.aeriekiosk

import org.junit.Assert.assertEquals
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

    private val events = SunEvents(
        dawnMs = at("2026-06-21T10:00:00Z"),
        sunriseMs = at("2026-06-21T10:30:00Z"),
        sunsetMs = at("2026-06-22T00:00:00Z"),
        duskMs = at("2026-06-22T00:30:00Z"),
    )

    private val curve = BrightnessCurve(day = 1.0f, amber = 0.4f, night = 0.05f)

    @Test
    fun `is night before dawn`() {
        assertEquals(CircadianPhase.Night, circadianPhase(at("2026-06-21T09:00:00Z"), events))
    }

    @Test
    fun `is night at and after the evening transition ends`() {
        // The transition starts 35min before sunset (23:25) and runs 2h, to 01:25.
        assertEquals(CircadianPhase.Night, circadianPhase(at("2026-06-22T01:25:00Z"), events))
        assertEquals(CircadianPhase.Night, circadianPhase(at("2026-06-22T05:00:00Z"), events))
    }

    @Test
    fun `is day between sunrise and the start of the evening transition`() {
        assertEquals(CircadianPhase.Day, circadianPhase(at("2026-06-21T15:00:00Z"), events))
        assertEquals(CircadianPhase.Day, circadianPhase(at("2026-06-21T23:24:59Z"), events))
    }

    @Test
    fun `computes morning transition progress between dawn and sunrise`() {
        val phase = circadianPhase(at("2026-06-21T10:15:00Z"), events)
        assertTrue(phase is CircadianPhase.MorningTransition)
        assertEquals(0.5f, (phase as CircadianPhase.MorningTransition).progress, 1e-5f)
    }

    @Test
    fun `starts the evening transition 35 minutes before sunset`() {
        val phase = circadianPhase(at("2026-06-21T23:25:00Z"), events)
        assertTrue(phase is CircadianPhase.EveningTransition)
        assertEquals(0f, (phase as CircadianPhase.EveningTransition).progress, 1e-5f)
    }

    @Test
    fun `computes evening transition progress across its 2-hour span`() {
        val phase = circadianPhase(at("2026-06-21T23:55:00Z"), events)
        assertTrue(phase is CircadianPhase.EveningTransition)
        assertEquals(0.25f, (phase as CircadianPhase.EveningTransition).progress, 1e-5f)
    }

    @Test
    fun `uses the day and night levels unblended outside a transition`() {
        assertEquals(1.0f, brightnessFor(CircadianPhase.Day, curve), 1e-5f)
        assertEquals(0.05f, brightnessFor(CircadianPhase.Night, curve), 1e-5f)
    }

    @Test
    fun `lands exactly on the amber level at the midpoint of a transition`() {
        assertEquals(0.4f, brightnessFor(CircadianPhase.EveningTransition(0.5f), curve), 1e-5f)
        assertEquals(0.4f, brightnessFor(CircadianPhase.MorningTransition(0.5f), curve), 1e-5f)
    }

    @Test
    fun `runs a transition from the from-phase level to the to-phase level`() {
        assertEquals(1.0f, brightnessFor(CircadianPhase.EveningTransition(0f), curve), 1e-5f)
        assertEquals(0.05f, brightnessFor(CircadianPhase.EveningTransition(1f), curve), 1e-5f)
        // Morning runs the same blend in reverse.
        assertEquals(0.05f, brightnessFor(CircadianPhase.MorningTransition(0f), curve), 1e-5f)
        assertEquals(1.0f, brightnessFor(CircadianPhase.MorningTransition(1f), curve), 1e-5f)
    }

    @Test
    fun `blends through amber rather than straight from day to night`() {
        // A quarter through the evening is halfway from day to amber (0.7),
        // which a straight day-to-night lerp would have put at ~0.76.
        assertEquals(0.7f, brightnessFor(CircadianPhase.EveningTransition(0.25f), curve), 1e-5f)
    }

    // --- alignToDay: the shell's problem, not the dashboard's ---

    private val utc: ZoneId = ZoneId.of("UTC")

    @Test
    fun `leaves events for today untouched`() {
        val aligned = alignToDay(events, at("2026-06-21T15:00:00Z"), utc, 7)
        assertEquals(events, aligned)
    }

    @Test
    fun `rolls yesterday's events forward onto today`() {
        val aligned = alignToDay(events, at("2026-06-22T15:00:00Z"), utc, 7)!!
        assertEquals(at("2026-06-22T10:00:00Z"), aligned.dawnMs)
        assertEquals(at("2026-06-22T10:30:00Z"), aligned.sunriseMs)
        assertEquals(at("2026-06-23T00:00:00Z"), aligned.sunsetMs)
        assertEquals(at("2026-06-23T00:30:00Z"), aligned.duskMs)
    }

    /**
     * The regression this function exists for: without the roll, a midday
     * `now` sits past a day-old sunset's transition and reads as Night, so a
     * tablet rebooting on a dead network dims itself in full daylight.
     */
    @Test
    fun `day-old events would read as night at midday without rolling`() {
        val noonTomorrow = at("2026-06-22T15:00:00Z")
        assertEquals(CircadianPhase.Night, circadianPhase(noonTomorrow, events))
        assertEquals(CircadianPhase.Day, circadianPhase(noonTomorrow, alignToDay(events, noonTomorrow, utc, 7)!!))
    }

    @Test
    fun `discards events older than the roll-forward limit`() {
        assertNull(alignToDay(events, at("2026-06-29T15:00:00Z"), utc, 7))
    }

    @Test
    fun `rolls up to the limit inclusive`() {
        assertTrue(alignToDay(events, at("2026-06-28T15:00:00Z"), utc, 7) != null)
    }

    /** A clock correction backwards shouldn't roll events into the past. */
    @Test
    fun `discards events dated in the future`() {
        assertNull(alignToDay(events, at("2026-06-20T15:00:00Z"), utc, 7))
    }
}
