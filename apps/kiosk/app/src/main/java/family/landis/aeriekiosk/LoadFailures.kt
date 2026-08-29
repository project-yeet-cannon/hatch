package family.landis.aeriekiosk

import androidx.annotation.StringRes
import org.mozilla.geckoview.WebRequestError

/**
 * Why the wall is not showing the dashboard, in the vocabulary of someone
 * standing in front of it rather than of the stack that produced it.
 *
 * Six cases rather than one because they are six different things to do next:
 * a network failure is "check the router", a server error is "Aerie is unwell",
 * a content crash is "it will come back on its own". A single "Reconnecting…"
 * says none of that, and it was also shown for only some of them - see
 * MainActivity for the two paths that used to bypass it entirely.
 */
enum class KioskFailure {
    /** DNS, connection refused, offline, timeout. The house network, or the LAN path to it. */
    Network,

    /** TLS: a bad or untrusted certificate. */
    Security,

    /** The response arrived and was unreadable - corrupt content, a bad encoding. */
    Content,

    /**
     * The document "loaded" and carried an error status. Gecko treats a 502 as a
     * successful load, so nothing in the delegate chain reports this - it is
     * only ever reached by the health probe in MainActivity.
     */
    ServerError,

    /** The content process died. The view is blank and no delegate will say so. */
    ContentCrashed,

    Unknown,
}

/** The first retry waits this long; each subsequent one doubles up to the cap. */
const val RETRY_DELAY_MS_INITIAL = 2_000L

/**
 * The ceiling on the backoff. Thirty seconds because a wall tablet has nobody
 * to press the button: the ladder exists to stop a hammering retry loop, not to
 * give up, so it caps rather than terminating.
 */
const val RETRY_DELAY_MS_MAX = 30_000L

/**
 * The classification and the ladder, apart from the activity that uses them, so
 * both can be asserted rather than eyeballed. Everything here is pure: no
 * views, no handlers, no clock.
 */
object LoadFailures {

    fun fromErrorCategory(category: Int): KioskFailure = when (category) {
        WebRequestError.ERROR_CATEGORY_NETWORK, WebRequestError.ERROR_CATEGORY_PROXY -> KioskFailure.Network
        WebRequestError.ERROR_CATEGORY_SECURITY -> KioskFailure.Security
        WebRequestError.ERROR_CATEGORY_CONTENT -> KioskFailure.Content
        // URI and SAFEBROWSING are both "this address is wrong", which cannot
        // happen for a kiosk pinned to one origin by the navigation delegate -
        // so they are honestly unknown rather than dressed up as a network
        // problem someone might go and look for.
        else -> KioskFailure.Unknown
    }

    /**
     * A status from the origin health probe. 5xx is Aerie or its ingress; 4xx
     * is reported the same way deliberately - a 404 for the dashboard document
     * is a broken deploy, and telling the person at the tablet to check their
     * network for it would send them the wrong way.
     */
    fun fromHttpStatus(status: Int): KioskFailure =
        if (status in 400..599) KioskFailure.ServerError else KioskFailure.Unknown

    @StringRes
    fun messageResFor(failure: KioskFailure): Int = when (failure) {
        KioskFailure.Network -> R.string.error_network
        KioskFailure.Security -> R.string.error_security
        KioskFailure.Content -> R.string.error_content
        KioskFailure.ServerError -> R.string.error_server
        KioskFailure.ContentCrashed -> R.string.error_crashed
        KioskFailure.Unknown -> R.string.error_unknown
    }

    /** The next rung of the backoff, given the current wait. Doubles, then holds at the cap. */
    fun nextRetryDelayMs(currentMs: Long): Long = (currentMs * 2).coerceAtMost(RETRY_DELAY_MS_MAX)

    /** Short tag for the logs, so a tablet stuck retrying is greppable in OpenSearch. */
    fun logKind(failure: KioskFailure): String = failure.name.lowercase()
}
