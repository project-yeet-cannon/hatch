import type { HatchSettings, HatchSettingsWriteRequest } from '../types';

/**
 * The rules the Settings page's two fields follow, pulled out of the component
 * so they can be read - and tested - without a browser.
 *
 * They are small on purpose. What is worth writing down is not "render an
 * input"; it is that a blank token field means *no change* rather than *clear
 * it*, that clearing is `''` and never `null`, and that the redacted dots the
 * server hands back are a signal about the stored value rather than a value to
 * put in the box.
 */

/** Whether a token is already stored. The server answers dots or nothing, never the token. */
export const tokenIsSet = (settings: HatchSettings): boolean => settings.claudeSubscriptionToken !== '';

/**
 * What the empty token input says.
 *
 * The dots themselves never go into the box: an operator who saved and came
 * back would otherwise be editing a placeholder, and pressing Save would store
 * eight bullet characters as their credential.
 */
export const tokenPlaceholder = (settings: HatchSettings): string =>
  tokenIsSet(settings) ? 'A token is set — type a new one to replace it' : 'Not set';

/**
 * The edit a Save of the token field sends, or null when there is nothing to
 * send.
 *
 * Blank is "leave it alone", not "clear it" - clearing has its own button,
 * because a credential should not be removable by tabbing through a form.
 */
export const tokenSave = (draft: string): HatchSettingsWriteRequest | null =>
  draft.trim() === '' ? null : { claudeSubscriptionToken: draft.trim() };

/** Removes the token. `''` rather than an omitted field, which would leave it alone. */
export const tokenClear = (): HatchSettingsWriteRequest => ({ claudeSubscriptionToken: '' });

/**
 * The edit a Save of the name field sends.
 *
 * Trimmed, and a blank draft is a clear rather than a no-op - unlike the token,
 * this field shows what is stored, so an operator who empties it is looking at
 * an empty box and asking for an empty name. Nothing here validates the name:
 * the server's read side already falls back for anything it will not use, and
 * two places deciding that would be two places to keep in step.
 */
export const nameSave = (draft: string): HatchSettingsWriteRequest => ({ localPersonName: draft.trim() });
