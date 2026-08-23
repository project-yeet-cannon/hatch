/**
 * The idle ladder: how long after the last touch each stage of "nobody is
 * standing here" takes effect.
 *
 * They live in one file because neither is correct on its own - each is a
 * statement about the other. The page resets first (cheap, invisible from
 * across the room) and the backlight dims well afterwards. Read in the other
 * order the wall would dim in the face of someone who is still reading it, then
 * tidy itself up once they had gone.
 *
 * **The dim rung is mirrored in Kotlin**
 * (`IDLE_DIM_AFTER_MS` in apps/kiosk/.../DisplayController.kt) because the
 * backlight is the shell's to move and the shell cannot import this file - the
 * same seam, and the same accepted duplication, as circadianTheme.ts /
 * CircadianBrightness.kt. Change one and change the other, or the panel and the
 * page start disagreeing about whether anyone is there.
 *
 * Both are per-kiosk profile values in docs/plans/kiosk_brightness.md Phase 3,
 * at which point both sides fetch them from the server and the mirroring goes
 * away. Until then a hallway and a kitchen idle on the same schedule.
 */

/**
 * How long after the last touch the display goes back to base state - cards
 * collapsed, scrolled to top. Short, because it undoes someone else's browsing
 * rather than announcing anything of its own.
 */
export const IDLE_TIMEOUT_MS = 30_000;

/**
 * How long after the last touch the backlight drops to its idle floor.
 * Generous on purpose: touch is the only presence signal the shell has until
 * Phase 4, and reading a wall display is a thing you do with your hands in your
 * pockets. A screen that dims in your face while you read it is worse than one
 * that never dimmed.
 */
export const IDLE_DIM_AFTER_MS = 120_000;

