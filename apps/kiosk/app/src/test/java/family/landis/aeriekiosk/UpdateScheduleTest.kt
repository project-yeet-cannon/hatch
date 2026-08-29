package family.landis.aeriekiosk

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The ladder and the backoff, and specifically the way they combine - which is
 * the part that was missing and the part whose absence was invisible. A failed
 * check used to wait for the next ladder rung, and once a tablet had been up an
 * hour that rung is six hours away, so a router reboot during a deploy could
 * cost most of a day with nothing to show for it but a stale wall.
 */
class UpdateScheduleTest {

    private val minute = 60 * 1000L
    private val hour = 60 * minute

    @Test
    fun `the ladder tapers from launch`() {
        assertEquals(UpdateSchedule.FAST_INTERVAL_MS, UpdateSchedule.ladderIntervalMs(0))
        assertEquals(UpdateSchedule.FAST_INTERVAL_MS, UpdateSchedule.ladderIntervalMs(19 * minute))
        assertEquals(UpdateSchedule.MEDIUM_INTERVAL_MS, UpdateSchedule.ladderIntervalMs(21 * minute))
        assertEquals(UpdateSchedule.MEDIUM_INTERVAL_MS, UpdateSchedule.ladderIntervalMs(59 * minute))
        assertEquals(UpdateSchedule.STEADY_INTERVAL_MS, UpdateSchedule.ladderIntervalMs(2 * hour))
        assertEquals(UpdateSchedule.STEADY_INTERVAL_MS, UpdateSchedule.ladderIntervalMs(40 * 24 * hour))
    }

    @Test
    fun `the ladder steps exactly at its window boundaries`() {
        assertEquals(UpdateSchedule.MEDIUM_INTERVAL_MS, UpdateSchedule.ladderIntervalMs(UpdateSchedule.FAST_WINDOW_MS))
        assertEquals(UpdateSchedule.STEADY_INTERVAL_MS, UpdateSchedule.ladderIntervalMs(UpdateSchedule.MEDIUM_WINDOW_MS))
    }

    @Test
    fun `no failures means the backoff does not apply`() {
        assertEquals(Long.MAX_VALUE, UpdateSchedule.failureBackoffMs(0))
        assertEquals(UpdateSchedule.STEADY_INTERVAL_MS, UpdateSchedule.nextIntervalMs(2 * hour, consecutiveFailures = 0))
    }

    @Test
    fun `the backoff doubles from thirty seconds and caps at half an hour`() {
        assertEquals(30_000L, UpdateSchedule.failureBackoffMs(1))
        assertEquals(60_000L, UpdateSchedule.failureBackoffMs(2))
        assertEquals(120_000L, UpdateSchedule.failureBackoffMs(3))
        assertEquals(UpdateSchedule.BACKOFF_MAX_MS, UpdateSchedule.failureBackoffMs(20))
        assertEquals(UpdateSchedule.BACKOFF_MAX_MS, UpdateSchedule.failureBackoffMs(2000))
    }

    @Test
    fun `a failure hours in does not have to wait out the six-hour rung`() {
        // This is the whole point of the phase. Before, the answer here was six
        // hours regardless of how many times in a row the check had failed.
        val steady = UpdateSchedule.nextIntervalMs(6 * hour, consecutiveFailures = 1)
        assertEquals(30_000L, steady)
        assertTrue(steady < UpdateSchedule.STEADY_INTERVAL_MS)
        assertTrue(UpdateSchedule.nextIntervalMs(6 * hour, 50) < UpdateSchedule.STEADY_INTERVAL_MS)
    }

    @Test
    fun `a failure at launch does not slow the ladder down`() {
        // The two combine by whichever comes sooner, so the backoff can only
        // ever make a check happen earlier - never later.
        for (failures in 0..10) {
            assertTrue(
                "backoff must not delay a check the ladder already wanted",
                UpdateSchedule.nextIntervalMs(0, failures) <= UpdateSchedule.FAST_INTERVAL_MS,
            )
        }
    }

    @Test
    fun `a sustained outage settles at the backoff cap, not the ladder`() {
        var failures = 0
        var elapsed = 0L
        // Simulate a day of a file server that never answers.
        while (elapsed < 24 * hour) {
            failures += 1
            elapsed += UpdateSchedule.nextIntervalMs(elapsed, failures)
        }
        assertEquals(UpdateSchedule.BACKOFF_MAX_MS, UpdateSchedule.nextIntervalMs(elapsed, failures))
        // Roughly 24h/30min of attempts rather than 24h/6h = four.
        assertTrue("expected tens of attempts, got $failures", failures > 40)
    }

    @Test
    fun `recovery returns to the ladder immediately`() {
        assertEquals(
            UpdateSchedule.STEADY_INTERVAL_MS,
            UpdateSchedule.nextIntervalMs(8 * hour, consecutiveFailures = 0),
        )
    }
}
