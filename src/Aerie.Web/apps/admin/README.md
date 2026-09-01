# Aerie Admin

Admin UI for the device-mapping domain described in `docs/device-architecture.md` (Phase 3). Lets you manage Zones, Devices and their Channels, import newly-discovered Home Assistant devices, and edit site-wide settings — replacing what used to be the `Dashboard` appsettings section.

## Pages

- **Zones** (`/zones`) — list, create, edit, delete, and reorder zones; set each zone's comfort band.
- **Devices** (`/devices`) — list devices, assign to a zone, enable/disable, edit device fields, and manage each device's channels (add/edit/delete).
- **Discovery** (`/discovery`) — shows Home Assistant devices not yet mapped to an Aerie `Device`, with a suggested name/kind/channel grouping; one click creates the `Device` and its `DeviceChannel`s.
- **People** (`/people`) — the household's members: name, photo, and an unenforced admin flag. Starts empty. Each row shows how many enrolled devices are that person's; the link itself is edited on **Sessions**, since a session has at most one person and a person has any number of sessions.
- **Settings** (`/settings`) — edit the well-known `SiteSetting` scalars (`TimeZone`, `Latitude`, `Longitude`, `WeatherEntity`, `ComfortToleranceF`, `DefaultComfortLowF`, `DefaultComfortHighF`).

## Development

### Prerequisites

- Node.js >= 22

### Setup

Aerie.Web is a single npm workspace with one lockfile at `src/Aerie.Web`, so the
install is the workspace's rather than this app's — run it once, from anywhere
in the tree, and every app is installed:

```bash
npm install
```

### Running

```bash
npm run dev
```

The dev server serves static assets only — API calls (`fetch('/api/...')`) need a same-origin backend. Build and run `Aerie.Api` (see the repo `Makefile`'s `run` target) and open the built app at `/apps/admin/` to exercise real API calls.

### Building

```bash
npm run build
```

The build output is placed in `../../../Aerie.Api/wwwroot/apps/admin` and served by the ASP.NET Core API at `/apps/admin/`.

### Linting

```bash
npm run lint
```

## API endpoints

- `GET/POST /api/zones`, `GET/PUT/DELETE /api/zones/{id}` — Zone CRUD
- `GET/POST /api/devices`, `GET/PUT/DELETE /api/devices/{id}` — Device CRUD (assigning a device to a zone is a `PUT` with `zoneId` set)
- `POST /api/devices/{id}/channels`, `PUT/DELETE /api/devices/{id}/channels/{channelId}` — DeviceChannel CRUD
- `GET/PUT/DELETE /api/settings/{key}`, `GET /api/settings` — SiteSetting CRUD (`PUT` upserts)
- `GET /api/discovery/unmapped` — Home Assistant devices with no matching `Device.HaDeviceId`, with suggested name/kind/channels
- `GET/POST /api/people`, `GET/PUT/DELETE /api/people/{id}` — Person CRUD
- `GET/PUT/DELETE /api/people/{id}/photo` — the photo, as raw image bytes rather than a multipart form. The server sniffs the type from the bytes and ignores the request's `Content-Type`; the client downscales first (`src/lib/avatar.ts`) as a courtesy, not as a control
- `GET /api/people/{id}/sessions` — that person's enrolled devices, read-only
- `PUT /api/auth/grants/{id}/person` — claims a device for a person, or unclaims it with a null. The only write path for that link

## Architecture

A standalone Vite/React SPA (same conventions as `apps/dashboard`: no shared component library, plain `fetch` for API calls, no state management library). Routing is `react-router-dom` (`BrowserRouter` with `basename="/apps/admin"`), which is otherwise unused elsewhere in this repo. `src/api/client.ts` holds one small `fetchJson` helper plus typed functions per resource; `src/types.ts` hand-mirrors the API's DTOs (`Aerie.Api/Models/DeviceMapping/Dtos.cs`).

## Styling

The app uses CSS variables for theming (`theme.css`) and supports both light and dark modes via `prefers-color-scheme`. Shared layout/table/badge classes live in `App.css`.
