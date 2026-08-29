package family.landis.aeriekiosk

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.mozilla.geckoview.WebRequestError

/**
 * The classification and the backoff, which are the two things on the error
 * path that can be wrong without anyone noticing: a wall tablet showing the
 * wrong sentence still looks like it is working, and a backoff that does not
 * back off only reveals itself as a warm tablet and a busy log.
 */
class LoadFailuresTest {

    @Test
    fun `network and proxy failures read as the network`() {
        assertEquals(KioskFailure.Network, LoadFailures.fromErrorCategory(WebRequestError.ERROR_CATEGORY_NETWORK))
        assertEquals(KioskFailure.Network, LoadFailures.fromErrorCategory(WebRequestError.ERROR_CATEGORY_PROXY))
    }

    @Test
    fun `a bad certificate is its own category`() {
        assertEquals(KioskFailure.Security, LoadFailures.fromErrorCategory(WebRequestError.ERROR_CATEGORY_SECURITY))
    }

    @Test
    fun `unreadable content is not reported as a network problem`() {
        // Sending someone to look at the router for a corrupt response is worse
        // than saying nothing useful.
        assertEquals(KioskFailure.Content, LoadFailures.fromErrorCategory(WebRequestError.ERROR_CATEGORY_CONTENT))
    }

    @Test
    fun `categories that cannot happen on a pinned origin stay honestly unknown`() {
        // The navigation delegate denies anything off DASHBOARD_HOST, so a URI
        // or safebrowsing failure is not a state this app can reach - dressing
        // it up as something specific would send someone the wrong way.
        assertEquals(KioskFailure.Unknown, LoadFailures.fromErrorCategory(WebRequestError.ERROR_CATEGORY_URI))
        assertEquals(KioskFailure.Unknown, LoadFailures.fromErrorCategory(WebRequestError.ERROR_CATEGORY_SAFEBROWSING))
        assertEquals(KioskFailure.Unknown, LoadFailures.fromErrorCategory(WebRequestError.ERROR_CATEGORY_UNKNOWN))
    }

    @Test
    fun `error statuses are the server's problem, including the ones that look like ours`() {
        assertEquals(KioskFailure.ServerError, LoadFailures.fromHttpStatus(500))
        assertEquals(KioskFailure.ServerError, LoadFailures.fromHttpStatus(502))
        // A 404 for the dashboard document is a broken deploy, not a wrong
        // address - the tablet cannot have typed it.
        assertEquals(KioskFailure.ServerError, LoadFailures.fromHttpStatus(404))
    }

    @Test
    fun `success and redirects are not failures`() {
        assertEquals(KioskFailure.Unknown, LoadFailures.fromHttpStatus(200))
        assertEquals(KioskFailure.Unknown, LoadFailures.fromHttpStatus(302))
    }

    @Test
    fun `every failure has its own sentence`() {
        val messages = KioskFailure.entries.map { LoadFailures.messageResFor(it) }
        assertEquals(
            "each failure needs a distinct message, or the categories are decoration",
            KioskFailure.entries.size,
            messages.toSet().size,
        )
        assertTrue("no failure may map to a missing string", messages.none { it == 0 })
    }

    @Test
    fun `the backoff doubles`() {
        assertEquals(4_000L, LoadFailures.nextRetryDelayMs(2_000L))
        assertEquals(8_000L, LoadFailures.nextRetryDelayMs(4_000L))
        assertEquals(16_000L, LoadFailures.nextRetryDelayMs(8_000L))
    }

    @Test
    fun `the backoff caps rather than terminating`() {
        // A wall tablet has nobody to press the button, so the ladder exists to
        // stop a hammering retry loop, not to give up.
        assertEquals(RETRY_DELAY_MS_MAX, LoadFailures.nextRetryDelayMs(RETRY_DELAY_MS_MAX))
        assertEquals(RETRY_DELAY_MS_MAX, LoadFailures.nextRetryDelayMs(RETRY_DELAY_MS_MAX * 10))
    }

    @Test
    fun `the ladder climbs from the initial delay to the cap and stays there`() {
        var delay = RETRY_DELAY_MS_INITIAL
        val seen = mutableListOf(delay)
        repeat(10) {
            delay = LoadFailures.nextRetryDelayMs(delay)
            seen.add(delay)
        }

        assertEquals(listOf(2_000L, 4_000L, 8_000L, 16_000L, 30_000L, 30_000L), seen.take(6))
        assertEquals(RETRY_DELAY_MS_MAX, seen.last())
        assertNotEquals("the ladder must actually climb", seen.first(), seen.last())
    }

    @Test
    fun `log kinds are stable, lowercase and distinct`() {
        // These end up as a field people filter on in OpenSearch.
        val kinds = KioskFailure.entries.map { LoadFailures.logKind(it) }
        assertEquals(KioskFailure.entries.size, kinds.toSet().size)
        assertTrue(kinds.all { it == it.lowercase() && it.isNotBlank() })
    }
}
