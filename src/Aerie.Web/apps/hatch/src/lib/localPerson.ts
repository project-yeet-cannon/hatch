/**
 * How the Settings page tells the nav strip that the name changed.
 *
 * A window event rather than a shared store or a poll. The strip and the page
 * are mounted at once and never unmount each other - a route change inside this
 * app is not a page load - so without something like this an operator who has
 * just typed their name would still be looking at "friend" in the corner until
 * they reloaded, which reads as the save not having worked.
 *
 * One event, fired after the write the server has already acknowledged, so the
 * strip re-reads rather than being handed a value: there is exactly one answer
 * to "who is sitting here", and it is the server's.
 */
export const LOCAL_PERSON_CHANGED = 'hatch:local-person-changed';

export const announceLocalPersonChanged = () => window.dispatchEvent(new Event(LOCAL_PERSON_CHANGED));
