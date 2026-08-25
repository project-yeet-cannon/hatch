import { handledUnauthorized } from '../lib/signIn';
import type { PanelSource, PanelState } from '../types';

/**
 * Panels over HTTP — the wall's half of src/Aerie.Api/Controllers/PanelsController.cs.
 *
 * Three calls and no more: read the open panel's state, and the two writes a
 * control can make. Both writes are item-scoped rather than channel-scoped,
 * which is the point of the endpoint shape — a tablet in a hallway can act on
 * what an admin deliberately put on a panel and cannot address an arbitrary
 * channel by id.
 *
 * Failures are surfaced with the server's own words the way gatherClient does,
 * because the reasons here are worth reading: a 400 is a guard refusing the
 * write (an override in force, a channel turned read-only since the panel was
 * built) and a 502 is Home Assistant unreachable. "Couldn't turn on" alone
 * leaves someone tapping a button that will never work.
 */

const BASE = '/api/panels';

/** The controller answers a rejection with a plain-text reason; ProblemDetails JSON is machine noise and falls back to the status line. */
async function failureFrom(res: Response): Promise<Error> {
  let detail = '';
  try {
    detail = (await res.text()).trim().replace(/^"|"$/g, '');
  } catch {
    // Body already consumed or the connection dropped; the status still says
    // something useful.
  }
  if (detail.startsWith('{') || detail.length === 0) detail = `${res.status} ${res.statusText}`;
  return new Error(detail);
}

async function post(path: string, body: unknown): Promise<void> {
  const res = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  // A refusal navigates and this promise never settles, so no caller renders an
  // error over a page that is already leaving (see lib/signIn.ts).
  if (handledUnauthorized(res)) return await new Promise<void>(() => {});
  if (!res.ok) throw await failureFrom(res);
  // Both writes answer 204 on success; there is nothing to parse.
}

export class ApiPanelSource implements PanelSource {
  async getState(panelId: string): Promise<PanelState> {
    const res = await fetch(`${BASE}/${panelId}/state`, { headers: { Accept: 'application/json' } });
    if (handledUnauthorized(res)) return await new Promise<PanelState>(() => {});
    if (!res.ok) throw await failureFrom(res);
    return (await res.json()) as PanelState;
  }

  setPower(panelId: string, itemId: string, on: boolean): Promise<void> {
    return post(`/${panelId}/items/${itemId}/power`, { on });
  }

  setSetpoint(panelId: string, itemId: string, valueF: number): Promise<void> {
    return post(`/${panelId}/items/${itemId}/setpoint`, { valueF });
  }
}
