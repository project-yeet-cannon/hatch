import { useEffect, useRef, useState } from 'react';
import { Link, useLocation, useParams } from 'react-router-dom';
import { redeem } from '../api/auth';
import type { RedeemFailure } from '../api/auth';
import { FailureNote } from '../components/FailureNote';
import { completeSignIn } from '../lib/completeSignIn';
import { guessDeviceName } from '../lib/deviceName';
import { inviteCodeBody, isCompleteInviteCode } from '../lib/inviteCode';
import { landingFor, returnToFromSearch } from '../lib/returnTo';

/**
 * `/apps/auth/r/:code` - what a scanned QR resolves to. Redeems on arrival,
 * with no code to type and no button to find.
 *
 * This is the route that makes in-page QR scanning optional: the admin's QR is
 * an ordinary URL, so *any* camera app on any phone can open it, and only the
 * kiosk tablets (whose GeckoView has no BarcodeDetector) need a scanner of
 * their own later.
 *
 * The device gets its guessed name without being asked. Nobody scanning a
 * printed square wants a form first, and the name is editable from the admin
 * Sessions page afterwards.
 */
export function RedeemPage() {
  const { code } = useParams();
  const location = useLocation();
  const [failure, setFailure] = useState<RedeemFailure | null>(null);
  const [attempt, setAttempt] = useState(0);
  const attempted = useRef(-1);

  const body = inviteCodeBody(code ?? '');
  const returnTo = returnToFromSearch(location.search);

  // A URL carrying something that was never a code is decided during render:
  // there is nothing to ask the server about, and nothing to wait for.
  const malformed = !isCompleteInviteCode(body);
  const reason = malformed ? ('invalid_code' as const) : failure;

  useEffect(() => {
    if (malformed) return;

    // A code is single use, and StrictMode runs this effect twice in dev: a
    // second POST would answer already_redeemed and paint a failure over a
    // sign-in that had in fact just worked. Guarding on the attempt counter
    // rather than a plain boolean is what lets "try again" through on purpose.
    if (attempted.current === attempt) return;
    attempted.current = attempt;

    void redeem(body, guessDeviceName()).then((result) => {
      if (result.ok) {
        completeSignIn(landingFor(returnTo));
        return;
      }
      setFailure(result.reason);
    });
    // No cancellation on unmount: StrictMode's remount would otherwise discard
    // the only request's result, and the guard above means nothing re-issues it.
  }, [attempt, body, malformed, returnTo]);

  if (reason === null) {
    return (
      <main className="shell">
        <div className="card panel panel-centered">
          <span className="panel-mark">Aerie</span>
          <div className="spinner" aria-hidden="true" />
          <p className="panel-lede" role="status">
            Signing this device in...
          </p>
        </div>
      </main>
    );
  }

  // Worth retrying as-is only when the code itself may still be good; an
  // expired or spent one needs a different code, which means the form.
  const retryable = reason === 'unreachable' || reason === 'rate_limited';

  return (
    <main className="shell">
      <div className="card panel">
        <header className="panel-head">
          <span className="panel-mark">Aerie</span>
          <h1>That didn't sign you in</h1>
        </header>

        <FailureNote reason={reason} />

        {retryable && (
          <button
            type="button"
            className="btn-primary"
            onClick={() => {
              setFailure(null);
              setAttempt((n) => n + 1);
            }}
          >
            Try again
          </button>
        )}

        {/* Carries ?r= across, so someone who arrived from a gated page still
            lands back on it after typing a fresh code. */}
        <Link className="panel-link" to={{ pathname: '/', search: location.search }}>
          Enter a code by hand
        </Link>
      </div>
    </main>
  );
}
