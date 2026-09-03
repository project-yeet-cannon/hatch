/* A project's key, and the speed bump in front of changing one.

   Mirrors EfHatchProject.KeyPattern. The server decides - it checks the shape
   and the collision itself, and a disagreement here shows up as a refusal with
   a reason - but the rekey dialog needs the same rules locally, because the
   whole point of the speed bump is to say no before the request rather than
   after it. */

/** Two to six characters, starting with a letter. Mirrors EfHatchProject.KeyPattern. */
const KEY = /^[A-Z][A-Z0-9]{1,5}$/;

/** Keys are shouted in storage; typing one in lower case is not a mistake. */
export const normalizeProjectKey = (key: string): string => key.trim().toUpperCase();

export const isValidProjectKey = (key: string): boolean => KEY.test(normalizeProjectKey(key));

/**
 * Why the rekey button is still greyed, or null when it is not.
 *
 * Three gates, and the third is the speed bump itself: the operator has to type
 * the key they are about to destroy. That is not ceremony - a rekey silently
 * breaks every AER-12 written into a commit message, a branch name or a chat
 * log, and those are references this database has never seen and cannot fix.
 * Typing the old key is the moment somebody reads it and decides.
 */
export function rekeyObjection(current: string, next: string, confirmation: string): string | null {
  const wanted = normalizeProjectKey(next);

  if (wanted === '') return 'Give the project its new key.';
  if (!isValidProjectKey(wanted)) return 'A key is two to six letters or digits, starting with a letter.';
  if (wanted === normalizeProjectKey(current)) return `${current} already has that key.`;
  if (normalizeProjectKey(confirmation) !== normalizeProjectKey(current)) return `Type ${current} to confirm.`;

  return null;
}
