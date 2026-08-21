import { clientLogger } from '../lib/clientLogger';

/** One enrolled device, as `AuthGrantDto` sends it (src/Aerie.Api/Models/Auth/Dtos.cs). */
export interface AuthGrant {
  id: string;
  label: string;
  kind: string;
  createdAt: string;
  lastSeenAt: string | null;
  lastSeenIp: string | null;
  userAgent: string | null;
  isCurrent: boolean;
}

/**
 * Why a redemption didn't produce a session. The first three are
 * `AuthRedemption`'s error constants off the wire; the last two are conditions
 * only the client can see.
 */
export type RedeemFailure =
  | 'invalid_code'
  | 'expired'
  | 'already_redeemed'
  | 'rate_limited'
  | 'unreachable';

export type RedeemResult = { ok: true; grant: AuthGrant } | { ok: false; reason: RedeemFailure };

/**
 * Turns a code into a session cookie, or into a reason there isn't one.
 *
 * Nothing here throws. Every outcome a person can hit - a mistyped code, a
 * fifteen-minute-old one, a flaky tablet radio - is a sentence this app has to
 * be able to say, so they all arrive by the same door and the caller renders
 * one of them.
 */
export async function redeem(code: string, label: string): Promise<RedeemResult> {
  let response: Response;

  try {
    response = await fetch('/api/auth/redeem', {
      method: 'POST',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      body: JSON.stringify({ code, label }),
    });
  } catch (error) {
    clientLogger.error('Redemption could not reach the API', {
      reason: error instanceof Error ? error.message : String(error),
    });
    return { ok: false, reason: 'unreachable' };
  }

  if (response.ok) {
    const grant = (await response.json()) as AuthGrant;
    // The code itself is never logged - only that this one worked, and what the
    // device ended up called.
    clientLogger.info('Device enrolled', { grantId: grant.id, label: grant.label });
    return { ok: true, grant };
  }

  const reason = await failureFrom(response);
  clientLogger.warn('Redemption refused', { reason, status: response.status });
  return { ok: false, reason };
}

/**
 * The gate's rate limiter answers 429 with no body (Program.cs sets
 * `RejectionStatusCode` and nothing else), and a 5xx or an HTML error page has
 * no `error` field either - so the body is read defensively and anything
 * unrecognised falls back to the reason that suggests trying again.
 */
async function failureFrom(response: Response): Promise<RedeemFailure> {
  if (response.status === 429) return 'rate_limited';

  try {
    const body = (await response.json()) as { error?: string };
    if (body.error === 'invalid_code' || body.error === 'expired' || body.error === 'already_redeemed') {
      return body.error;
    }
  } catch {
    // Not JSON. Nothing to learn from it.
  }

  return response.status >= 500 ? 'unreachable' : 'invalid_code';
}
