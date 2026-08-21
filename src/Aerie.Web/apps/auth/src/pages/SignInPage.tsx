import { useState } from 'react';
import type { FormEvent } from 'react';
import { useLocation } from 'react-router-dom';
import { redeem } from '../api/auth';
import type { RedeemFailure } from '../api/auth';
import { FailureNote } from '../components/FailureNote';
import { completeSignIn } from '../lib/completeSignIn';
import { guessDeviceName } from '../lib/deviceName';
import { formatInviteBody, inviteCodeBody, isCompleteInviteCode, INVITE_PREFIX } from '../lib/inviteCode';
import { landingFor, returnToFromSearch } from '../lib/returnTo';

/**
 * The one screen: a code, a name for this device, and a button.
 *
 * Everything about the code field exists to survive being used across a room -
 * the value is normalized on every keystroke (uppercased, dashed, Crockford
 * folded), so there is no way to type something the server will disagree with
 * about, and a paste of the whole "AERIE-K3M9-P2QT" lands as readily as eight
 * typed characters.
 */
export function SignInPage() {
  const [body, setBody] = useState('');
  // Lazily, and once: re-guessing on every render would fight the person as
  // soon as they started editing it.
  const [label, setLabel] = useState(guessDeviceName);
  const [failure, setFailure] = useState<RedeemFailure | null>(null);
  const [busy, setBusy] = useState(false);

  const returnTo = returnToFromSearch(useLocation().search);

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    if (busy) return;

    // The button is disabled until the code is eight characters, so this only
    // catches a submit from the keyboard - it still has to say something.
    if (!isCompleteInviteCode(body)) {
      setFailure('invalid_code');
      return;
    }

    setBusy(true);
    setFailure(null);

    const result = await redeem(body, label.trim() || guessDeviceName());

    if (result.ok) {
      // Deliberately still busy: the browser is on its way out of this page and
      // re-enabling the button would only offer a second, doomed redemption.
      completeSignIn(landingFor(returnTo));
      return;
    }

    setFailure(result.reason);
    setBusy(false);
  }

  return (
    <main className="shell">
      <form className="card panel" onSubmit={onSubmit}>
        <header className="panel-head">
          <span className="panel-mark">Aerie</span>
          <h1>Sign in this device</h1>
          <p className="panel-lede">
            Enter the invite code you were given. Once it works, this device stays signed in - you
            will not be asked again.
          </p>
        </header>

        <div className="field">
          <label className="field-label" htmlFor="code">
            Invite code
          </label>
          {/* The prefix is fixed, so it is chrome beside the field rather than
              five characters to carry past the cursor. It is aria-hidden and
              restated in the hint below, which is what a screen reader reads. */}
          <div className="code-field">
            <span className="code-prefix" aria-hidden="true">
              {INVITE_PREFIX}-
            </span>
            <input
              id="code"
              className="code-input"
              value={formatInviteBody(body)}
              onChange={(event) => setBody(inviteCodeBody(event.target.value))}
              // No maxLength: a pasted "AERIE-K3M9-P2QT" is fifteen characters,
              // and the browser would truncate it before onChange ever saw it.
              autoFocus
              autoComplete="one-time-code"
              autoCapitalize="characters"
              autoCorrect="off"
              spellCheck={false}
              enterKeyHint="go"
              aria-describedby="code-hint"
              placeholder="K3M9-P2QT"
            />
          </div>
          <p className="field-hint" id="code-hint">
            Eight characters, like AERIE-K3M9-P2QT. Capitals and dashes are filled in for you.
          </p>
        </div>

        <div className="field">
          <label className="field-label" htmlFor="label">
            Name this device
          </label>
          <input
            id="label"
            value={label}
            onChange={(event) => setLabel(event.target.value)}
            maxLength={64}
            aria-describedby="label-hint"
            placeholder="This device"
          />
          <p className="field-hint" id="label-hint">
            Whoever manages Aerie sees this in the list of signed-in devices. "Ada's iPhone" beats
            "iPhone".
          </p>
        </div>

        <button type="submit" className="btn-primary" disabled={busy || !isCompleteInviteCode(body)}>
          {busy ? 'Signing in...' : 'Sign in'}
        </button>

        {failure && <FailureNote reason={failure} />}
      </form>
    </main>
  );
}
