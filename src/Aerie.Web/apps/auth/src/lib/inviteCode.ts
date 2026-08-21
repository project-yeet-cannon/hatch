/**
 * The client half of `AuthTokens.NormalizeInviteCode`
 * (src/Aerie.Api/Services/Auth/AuthTokens.cs). Every rule here mirrors that
 * file, because the two disagreeing means a code the field accepts and the
 * server rejects - the failure that reads as "the code doesn't work" while both
 * halves are behaving as written.
 *
 * The rules, in the order the server applies them:
 *   1. separators (`-`, ` `, `_`) drop out and everything uppercases;
 *   2. a leading `AERIE` prefix comes off *before* the Crockford fold, since
 *      "AERIE" contains an I and folding first would turn it into "AER1E";
 *   3. I/L -> 1 and O -> 0, so a code heard as "eye" and typed as I still lands;
 *   4. what's left has to be eight characters of Crockford's alphabet.
 */

/** Crockford's alphabet: 0-9 then A-Z without I, L, O and U. */
export const INVITE_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

/** Characters in a code body, excluding the prefix and the dashes. */
export const INVITE_CODE_LENGTH = 8;

/** What a formatted code leads with, so eight characters on a screen say what they are for. */
export const INVITE_PREFIX = 'AERIE';

const GROUP_SIZE = INVITE_CODE_LENGTH / 2;

/**
 * Whatever was typed, pasted or scanned, reduced towards the bare eight
 * characters the server hashes - truncated to that length but *not* required to
 * have reached it, because this runs on every keystroke.
 *
 * Anything outside the alphabet is dropped rather than rejected: the field
 * shows the result, so a dropped character is visible feedback that it wasn't
 * one of the 32, and there is no such thing as a half-typed code that is
 * "invalid" yet.
 */
export function inviteCodeBody(input: string): string {
  const stripped = input.replace(/[-\s_]/g, '').toUpperCase();

  // Someone typing the prefix out by hand passes through "A", "AE", "AER"...
  // on the way. Treating those as an empty body keeps the field from folding
  // the I in "AERI" into a 1 and stranding them with "AER1".
  if (stripped.length <= INVITE_PREFIX.length && INVITE_PREFIX.startsWith(stripped)) return '';

  const body = stripped.startsWith(INVITE_PREFIX) ? stripped.slice(INVITE_PREFIX.length) : stripped;

  return [...body]
    .map((c) => (c === 'I' || c === 'L' ? '1' : c === 'O' ? '0' : c))
    .filter((c) => INVITE_ALPHABET.includes(c))
    .slice(0, INVITE_CODE_LENGTH)
    .join('');
}

/** Whether a body is long enough to be worth posting. Says nothing about whether the server knows it. */
export function isCompleteInviteCode(body: string): boolean {
  return body.length === INVITE_CODE_LENGTH;
}

/**
 * "K3M9P2QT" -> "K3M9-P2QT", and the partial forms on the way there, for the
 * value the input actually displays. The `AERIE-` prefix is chrome beside the
 * field rather than text inside it - it is fixed, so making someone carry it
 * past the cursor buys nothing.
 */
export function formatInviteBody(body: string): string {
  return body.length <= GROUP_SIZE ? body : `${body.slice(0, GROUP_SIZE)}-${body.slice(GROUP_SIZE)}`;
}

/** The whole code as a person says it: "AERIE-K3M9-P2QT". */
export function formatInviteCode(body: string): string {
  return `${INVITE_PREFIX}-${formatInviteBody(body)}`;
}
