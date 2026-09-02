import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Badge, Button, Card, PageHeader, Table, Text } from '@aerie/ui';
import {
  deleteCalendarAccount,
  getCalendarAccounts,
  getSettings,
  refreshCalendars,
  syncCalendarEvents,
  updateCalendar,
} from '../api/client';
import { formatAge } from '../lib/format';
import type { CalendarAccount, Calendar as FamilyCalendar } from '../types';

/**
 * Which Google accounts the family calendar draws from, and which of their
 * calendars are actually on the wall.
 *
 * Connecting is a full-page redirect rather than a fetch: the browser leaves
 * for Google's consent screen and comes back to this same path carrying
 * ?connected= or ?error=. That is why the connect controls are links and not
 * buttons, and why the first thing this page does on mount is read its own
 * query string and then clear it - a reload should not re-announce a connection
 * that happened ten minutes ago.
 *
 * Every other control here is an ordinary fetch against CalendarsController.
 */
export function CalendarsPage() {
  const [accounts, setAccounts] = useState<CalendarAccount[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  // Whether the operator has entered the Google client credentials yet. The
  // start endpoint answers a bare 400 Problem to a browser that navigates to it
  // without them, which would be a page of raw JSON where a person expected
  // Google - so the button is replaced by the reason it wouldn't work.
  const [oauthConfigured, setOauthConfigured] = useState(true);
  const [syncing, setSyncing] = useState(false);

  const [searchParams, setSearchParams] = useSearchParams();
  const [outcome] = useState<Outcome | null>(() => outcomeFrom(new URLSearchParams(window.location.search)));

  useEffect(() => {
    load();

    getSettings().then(
      (settings) => {
        const isSet = (key: string) => (settings.find((s) => s.key === key)?.value.length ?? 0) > 0;
        setOauthConfigured(isSet('GoogleClientId') && isSet('GoogleClientSecret'));
      },
      // A settings fetch that fails says nothing about whether OAuth is
      // configured, so it leaves the optimistic default alone.
      () => setOauthConfigured(true),
    );
  }, []);

  // Strip the redirect's markers once they've been read, so this page's URL is
  // shareable and a refresh is just a refresh.
  useEffect(() => {
    if (!searchParams.has('connected') && !searchParams.has('error')) return;

    const next = new URLSearchParams(searchParams);
    next.delete('connected');
    next.delete('error');
    setSearchParams(next, { replace: true });
  }, [searchParams, setSearchParams]);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      setAccounts(await getCalendarAccounts());
    } catch (err) {
      setError(messageOf(err));
    } finally {
      setLoading(false);
    }
  }

  /** Applies one calendar's new admin-owned state and folds the server's answer back in, so a rejected color reverts visibly. */
  async function saveCalendar(calendar: FamilyCalendar, changes: Partial<FamilyCalendar>) {
    const next = { ...calendar, ...changes };
    setAccounts((prev) => prev.map((a) => ({ ...a, calendars: a.calendars.map((c) => (c.id === next.id ? next : c)) })));

    setError(null);
    setNotice(null);
    try {
      const saved = await updateCalendar(next.id, {
        included: next.included,
        colorOverride: next.colorOverride,
        sortOrder: next.sortOrder,
      });
      setAccounts((prev) =>
        prev.map((a) => ({ ...a, calendars: a.calendars.map((c) => (c.id === saved.id ? saved : c)) })),
      );
    } catch (err) {
      setError(messageOf(err));
      // The optimistic edit above is now a lie about what's stored; the server's
      // list is the only thing that knows the truth.
      await load();
    }
  }

  async function refresh(account: CalendarAccount) {
    setError(null);
    setNotice(null);
    try {
      const result = await refreshCalendars(account.id);
      setNotice(
        `${account.accountEmail}: ${result.added} added, ${result.updated} updated, ${result.removed} removed.`,
      );
    } catch (err) {
      // The account row carries the same failure as LastSyncError, so reloading
      // below shows it in place as well as here.
      setError(messageOf(err));
    }
    await load();
  }

  /**
   * Pulls events for every included calendar right now. The job does this every
   * five minutes anyway; this is for the minute after somebody ticks a calendar
   * on and wants to see it reach the wall.
   */
  async function syncEvents() {
    setSyncing(true);
    setError(null);
    setNotice(null);
    try {
      const result = await syncCalendarEvents();
      setNotice(
        `Synced ${result.calendars} calendar(s): ${result.written} event(s) written, ${result.removed} removed` +
          (result.failedAccounts > 0 ? `, ${result.failedAccounts} account(s) failed — see the accounts below.` : '.'),
      );
    } catch (err) {
      setError(messageOf(err));
    }
    // Even on success: a sync stamps lastSyncedAt and clears or sets
    // lastSyncError on every account it touched.
    await load();
    setSyncing(false);
  }

  async function remove(account: CalendarAccount) {
    if (
      !confirm(
        `Disconnect ${account.accountEmail}? Aerie stops reading its calendars and forgets which ones were included.`,
      )
    )
      return;

    setError(null);
    setNotice(null);
    try {
      await deleteCalendarAccount(account.id);
      await load();
    } catch (err) {
      setError(messageOf(err));
    }
  }

  return (
    <div>
      <PageHeader
        title="Calendars"
        actions={
          <>
            {oauthConfigured ? (
              // A plain anchor, not a fetch and not a router Link: this leaves the
              // SPA for Google entirely.
              <Button as="a" variant="primary" href={CONNECT_PATH}>
                Connect a Google account
              </Button>
            ) : (
              <Button as={Link} to="/settings">
                Set Google credentials first
              </Button>
            )}
            <Button onClick={syncEvents} disabled={syncing}>
              {syncing ? 'Syncing…' : 'Sync events now'}
            </Button>
            <Button onClick={load}>Refresh</Button>
          </>
        }
      />

      {outcome?.kind === 'connected' && <Text tone="success" className="mb-2">Connected {outcome.email}.</Text>}
      {outcome?.kind === 'error' && <Text tone="danger" className="mb-2">Couldn’t connect that account: {outcome.message}</Text>}

      {error && <Text tone="danger" className="mb-2">{error}</Text>}
      {notice && <Text tone="muted" className="mb-2">{notice}</Text>}
      {loading && <Text tone="muted">Loading…</Text>}

      {!loading && accounts.length === 0 && (
        <Card className="mb-2">
          <p>
            No accounts are connected. <strong>Connect a Google account</strong> sends you to Google’s consent screen
            and brings back the list of calendars it can see — every one of them off until you say otherwise.
          </p>
        </Card>
      )}

      {!loading &&
        accounts.map((account) => (
          <AccountCard
            key={account.id}
            account={account}
            onRefresh={() => refresh(account)}
            onRemove={() => remove(account)}
            onSaveCalendar={saveCalendar}
          />
        ))}

      <SetupNote />
    </div>
  );
}

/** Where both halves of the connect flow start: the first connection, and the reconnect a dead grant needs. */
const CONNECT_PATH = '/api/calendar/oauth/start';

function AccountCard({
  account,
  onRefresh,
  onRemove,
  onSaveCalendar,
}: {
  account: CalendarAccount;
  onRefresh: () => Promise<void>;
  onRemove: () => Promise<void>;
  onSaveCalendar: (calendar: FamilyCalendar, changes: Partial<FamilyCalendar>) => Promise<void>;
}) {
  const [refreshing, setRefreshing] = useState(false);
  const [removing, setRemoving] = useState(false);

  async function run(action: () => Promise<void>, setBusy: (busy: boolean) => void) {
    setBusy(true);
    try {
      await action();
    } finally {
      setBusy(false);
    }
  }

  const included = account.calendars.filter((c) => c.included).length;

  return (
    <Card className="mb-2">
      <div className="flex between" style={{ alignItems: 'flex-start' }}>
        <div>
          <div className="flex gap-1" style={{ alignItems: 'center' }}>
            <strong>{account.accountEmail}</strong>
            {account.displayName && <Text as="span" tone="muted">{account.displayName}</Text>}
            {account.needsReauth && <Badge>Needs reconnecting</Badge>}
          </div>
          <Text tone="muted" className="mt-1 mb-1">
            Connected {formatAge(account.connectedAt)} · Last synced{' '}
            {account.lastSyncedAt ? formatAge(account.lastSyncedAt) : 'never'} · {included} of{' '}
            {account.calendars.length} included
          </Text>
        </div>
        <div className="flex gap-1">
          <Button disabled={refreshing} onClick={() => run(onRefresh, setRefreshing)}>
            {refreshing ? 'Refreshing…' : 'Refresh calendars'}
          </Button>
          <Button variant="danger" disabled={removing} onClick={() => run(onRemove, setRemoving)}>
            {removing ? 'Removing…' : 'Remove'}
          </Button>
        </div>
      </div>

      {account.needsReauth && (
        <Text tone="danger" className="mb-2">
          Google no longer accepts this account’s authorization, so its calendars have stopped updating.{' '}
          <a href={CONNECT_PATH}>Reconnect it</a> — the calendars below keep their settings.
        </Text>
      )}

      {account.lastSyncError && <Text tone="danger" className="mb-2">Last sync failed: {account.lastSyncError}</Text>}

      {account.calendars.length === 0 ? (
        <Text tone="muted">
          No calendars discovered for this account yet. <strong>Refresh calendars</strong> asks Google again.
        </Text>
      ) : (
        <Table>
          <thead>
            <tr>
              <th style={{ width: '1%' }}>Show</th>
              <th>Calendar</th>
              <th>Time zone</th>
              <th style={{ width: '1%' }}>Color</th>
            </tr>
          </thead>
          <tbody>
            {account.calendars.map((calendar) => (
              <CalendarRow key={calendar.id} calendar={calendar} onSave={onSaveCalendar} />
            ))}
          </tbody>
        </Table>
      )}
    </Card>
  );
}

function CalendarRow({
  calendar,
  onSave,
}: {
  calendar: FamilyCalendar;
  onSave: (calendar: FamilyCalendar, changes: Partial<FamilyCalendar>) => Promise<void>;
}) {
  // A color input fires change continuously while the picker is being dragged,
  // so the swatch tracks a local draft and only the committed value is written.
  const [draftColor, setDraftColor] = useState<string | null>(null);
  const effectiveColor = draftColor ?? calendar.colorOverride ?? calendar.providerColor;

  return (
    <tr>
      <td>
        <input
          type="checkbox"
          aria-label={`Show ${calendar.name} on the dashboard`}
          checked={calendar.included}
          onChange={(e) => onSave(calendar, { included: e.target.checked })}
        />
      </td>
      <td>
        <div className="flex gap-1" style={{ alignItems: 'center' }}>
          <span
            aria-hidden
            style={{
              width: 10,
              height: 10,
              borderRadius: '50%',
              flex: 'none',
              background: effectiveColor ?? 'var(--line)',
            }}
          />
          <span title={calendar.providerCalendarId}>{calendar.name}</span>
          {calendar.isPrimary && <Badge>Primary</Badge>}
        </div>
      </td>
      <Text as="td" tone="muted">{calendar.timeZone ?? '—'}</Text>
      <td>
        <div className="flex gap-1" style={{ alignItems: 'center' }}>
          <input
            type="color"
            aria-label={`Color for ${calendar.name}`}
            value={toSwatch(effectiveColor)}
            onChange={(e) => setDraftColor(e.target.value)}
            onBlur={async () => {
              if (draftColor === null || draftColor === calendar.colorOverride) return;
              await onSave(calendar, { colorOverride: draftColor });
              setDraftColor(null);
            }}
          />
          {calendar.colorOverride !== null && (
            <Button
              title="Go back to the color Google reports for this calendar"
              onClick={async () => {
                setDraftColor(null);
                await onSave(calendar, { colorOverride: null });
              }}
            >
              Reset
            </Button>
          )}
        </div>
      </td>
    </tr>
  );
}

/**
 * What the operator has to do in their own Google Cloud project before any of
 * this works. It lives on the page rather than only in the docs because it is
 * read exactly once, at the moment the connect button is first pressed - and
 * because the Testing-mode expiry below presents as the calendar quietly going
 * stale a week later, which is not a symptom anyone traces back to a checkbox
 * in a console they visited once.
 */
function SetupNote() {
  const redirectUri = `${window.location.origin}/api/calendar/oauth/callback`;

  return (
    <Card>
      <details>
        <summary>
          <strong>Setting up the Google connection</strong>
        </summary>
        <Text as="ol" tone="muted" className="mt-2">
          <li>
            In a Google Cloud project of your own, enable the <strong>Google Calendar API</strong> and create an{' '}
            <strong>OAuth client ID</strong> of type “Web application”.
          </li>
          <li>
            Give that client the authorized redirect URI <code>{redirectUri}</code>. Google matches it exactly, so if a
            proxy rewrites the address this page is served from, set <code>GoogleOAuthRedirectUri</code> on{' '}
            <Link to="/settings">Settings</Link> to the address you registered.
          </li>
          <li>
            Put the client ID and secret into <code>GoogleClientId</code> and <code>GoogleClientSecret</code> on{' '}
            <Link to="/settings">Settings</Link>.
          </li>
          <li>
            Publish the consent screen to <strong>Production</strong>. While it sits in <em>Testing</em>, Google expires
            the stored authorization after <strong>7 days</strong> and the calendar goes stale every week. Leaving it
            unverified is fine at household scale — Google allows that with a warning screen, up to 100 users.
          </li>
        </Text>
      </details>
    </Card>
  );
}

type Outcome = { kind: 'connected'; email: string } | { kind: 'error'; message: string };

/** The connect flow's verdict, as CalendarOAuthController wrote it into this page's query string. */
function outcomeFrom(params: URLSearchParams): Outcome | null {
  const connected = params.get('connected');
  if (connected) return { kind: 'connected', email: connected };

  const error = params.get('error');
  if (error) return { kind: 'error', message: explainOAuthError(error) };

  return null;
}

/**
 * Turns a callback error code into something the operator can act on. Google's
 * own codes and the controller's arrive through the same parameter, so both are
 * here; anything unrecognized is shown as-is rather than swallowed.
 */
function explainOAuthError(code: string): string {
  switch (code) {
    case 'access_denied':
      return 'the consent screen was declined.';
    case 'missing_state':
    case 'missing_code':
    case 'unknown_state':
      return 'the reply from Google didn’t match a connection attempt made here. Start again from this page.';
    case 'expired_state':
      return 'the connection attempt took too long. Start again — a fresh one is good for ten minutes.';
    case 'no_refresh_token':
      return 'Google didn’t issue a long-lived authorization, so the connection would have stopped working. Try again, and accept the consent screen rather than skipping it.';
    case 'no_account_email':
      return 'Google didn’t say which account was authorized.';
    case 'token_exchange_failed':
      return 'Google rejected the exchange. Check that the client ID, secret, and redirect URI on Settings match the ones in your Cloud console.';
    default:
      return code;
  }
}

const messageOf = (err: unknown) => (err instanceof Error ? err.message : String(err));

/**
 * A color input only accepts "#rrggbb", so the three-digit form and a missing
 * color both have to become something concrete. The fallback is Google's own
 * default calendar blue, which is what the swatch would have shown anyway for a
 * calendar whose color hasn't been discovered.
 */
function toSwatch(color: string | null): string {
  if (color === null) return '#4285f4';
  if (/^#[0-9a-fA-F]{3}$/.test(color)) {
    const [, r, g, b] = color;
    return `#${r}${r}${g}${g}${b}${b}`;
  }
  return /^#[0-9a-fA-F]{6}$/.test(color) ? color : '#4285f4';
}
