import { useState } from 'react';
import { Button, Card, Field, PageHeader } from '@aerie/ui';
import { getHatchSettings, putHatchSettings } from '../api/client';
import { message } from '../lib/errors';
import { nameSave, tokenClear, tokenIsSet, tokenPlaceholder, tokenSave } from '../lib/hatchSettings';
import { announceLocalPersonChanged } from '../lib/localPerson';
import { useLoaded } from '../lib/useLoaded';
import type { HatchSettings, HatchSettingsWriteRequest } from '../types';

/**
 * The two things a Hatch install of its own configures.
 *
 * Here rather than on the admin app's Settings page because an install with no
 * admin app - which is every install that is only somebody's tracker - still
 * has to be able to set them. They are the same site settings under the same
 * keys, so a token set on the old page is already set on this one.
 *
 * Two fields written out rather than driven from a table of field descriptors
 * the way the admin page does it: two of them do not earn the abstraction, and
 * they do not behave alike anyway - one is a credential that is never read
 * back, and the other is a name that always is. The rules each follows are in
 * lib/hatchSettings.ts, which is where they are tested.
 */
export function SettingsPage() {
  const { data: settings, setData, error, setError } = useLoaded<HatchSettings>(getHatchSettings);
  const [token, setToken] = useState('');
  const [name, setName] = useState<string | null>(null);
  // Which button is mid-request, so one card's Save does not spin the other's.
  const [busy, setBusy] = useState<'token' | 'name' | null>(null);

  async function save(what: 'token' | 'name', request: HatchSettingsWriteRequest) {
    setBusy(what);
    try {
      // The response is the settings as they now read, so there is nothing to
      // reload - and the token input is emptied rather than filled with what
      // came back, which is dots.
      setData(await putHatchSettings(request));
      setToken('');
      setError(null);
      // The nav strip is mounted alongside this page and a route change here is
      // not a page load, so it is told rather than left to find out.
      if (what === 'name') announceLocalPersonChanged();
    } catch (err) {
      setError(message(err));
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="hatch-page">
      <PageHeader
        title="Settings"
        description="What this installation of Hatch knows about the account it runs on and the person it runs for."
      />

      {error && <p className="text-danger">{error}</p>}

      {settings && (
        <Card>
          <h2 className="hatch-section-title">Claude subscription token</h2>
          <Field
            label="Token"
            hint="An OAuth token for your own Claude subscription. It is what lets the nav strip say how much headroom the account has left, and it is entirely optional - without one Hatch simply has no battery. Stored obfuscated, and never read back."
          >
            <input
              type="password"
              value={token}
              placeholder={tokenPlaceholder(settings)}
              onChange={(e) => setToken(e.target.value)}
            />
          </Field>
          <div className="hatch-form-actions">
            <Button
              variant="primary"
              loading={busy === 'token'}
              disabled={busy !== null || tokenSave(token) === null}
              onClick={() => void save('token', tokenSave(token)!)}
            >
              Save token
            </Button>
            {/* Its own button rather than "save a blank field", so a credential
                cannot be removed by tabbing through a form and pressing Save. */}
            <Button
              variant="danger"
              disabled={busy !== null || !tokenIsSet(settings)}
              onClick={() => void save('token', tokenClear())}
            >
              Remove token
            </Button>
          </div>
        </Card>
      )}

      {/* Absent wherever the wall is up, which is every cluster install: there
          a person is a person because they enrolled, and their name is already
          on everything they write. A box that could not change that would be a
          box that lied. */}
      {settings?.localPersonNameApplies && (
        <Card>
          <h2 className="hatch-section-title">Your name</h2>
          <Field
            label="Name"
            hint="What Hatch signs everything this browser does with - every comment, every move, every event. Leave it empty and Hatch uses a default and says so in the nav."
          >
            <input
              value={name ?? settings.localPersonName}
              placeholder="Nobody has said"
              onChange={(e) => setName(e.target.value)}
            />
          </Field>
          <div className="hatch-form-actions">
            <Button
              variant="primary"
              loading={busy === 'name'}
              disabled={busy !== null || name === null || name.trim() === settings.localPersonName}
              onClick={() => void save('name', nameSave(name ?? '')).then(() => setName(null))}
            >
              Save name
            </Button>
          </div>
        </Card>
      )}
    </div>
  );
}
