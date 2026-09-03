/** The sentence to put on screen for whatever a rejected promise carried. */
export function message(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
