package family.landis.aeriekiosk

import kotlin.math.min

/**
 * When the next update check happens, apart from the thing that performs it, so
 * both halves of the answer can be asserted rather than eyeballed.
 *
 * There are two independent schedules here and they answer different questions.
 * The **ladder** is "how often do we expect a new build" - deploys land right
 * after a dev session and iteration happens in focused spurts, so it checks
 * aggressively for the first hour and then settles down to a steady background
 * poll. The **backoff** is "how fast do we recover from not being able to ask",
 * which is a different question entirely and used not to be asked at all: a
 * failed check simply waited for the next ladder rung, and once a tablet had
 * been up an hour that rung is six hours away. A router reboot during a deploy
 * could cost most of a day, silently.
 *
 * They combine by taking whichever comes sooner. A failure early on does not
 * slow the ladder down, and a failure late does not have to wait it out.
 */
object UpdateSchedule {

    const val FAST_WINDOW_MS = 20 * 60 * 1000L
    const val FAST_INTERVAL_MS = 60 * 1000L
    const val MEDIUM_WINDOW_MS = 60 * 60 * 1000L
    const val MEDIUM_INTERVAL_MS = 5 * 60 * 1000L
    const val STEADY_INTERVAL_MS = 6 * 60 * 60 * 1000L

    /** First retry after a failed check. */
    const val BACKOFF_INITIAL_MS = 30 * 1000L

    /**
     * The backoff ceiling. Half an hour rather than the ladder's six: a tablet
     * that cannot reach the file server is in a state worth re-checking often,
     * and the check is one small GET.
     */
    const val BACKOFF_MAX_MS = 30 * 60 * 1000L

    /**
     * @param sinceStartMs elapsed since UpdateManager.start(), measured with
     * SystemClock.elapsedRealtime() rather than wall-clock time so a timezone
     * or NTP correction mid-window cannot throw it off.
     */
    fun ladderIntervalMs(sinceStartMs: Long): Long = when {
        sinceStartMs < FAST_WINDOW_MS -> FAST_INTERVAL_MS
        sinceStartMs < MEDIUM_WINDOW_MS -> MEDIUM_INTERVAL_MS
        else -> STEADY_INTERVAL_MS
    }

    /** Doubles from 30s, capped. Zero failures means the backoff does not apply. */
    fun failureBackoffMs(consecutiveFailures: Int): Long {
        if (consecutiveFailures <= 0) return Long.MAX_VALUE
        var delay = BACKOFF_INITIAL_MS
        repeat(consecutiveFailures - 1) {
            delay = min(delay * 2, BACKOFF_MAX_MS)
            if (delay == BACKOFF_MAX_MS) return BACKOFF_MAX_MS
        }
        return delay
    }

    fun nextIntervalMs(sinceStartMs: Long, consecutiveFailures: Int): Long =
        min(ladderIntervalMs(sinceStartMs), failureBackoffMs(consecutiveFailures))
}
