# Aerie Admin

Administrative dashboard for configuring basic system assumptions and managing persisted data.

## Features

- **System Configuration**: Set timezone, temperature units, API host, and other system-wide settings
- **Data Management**: Export, import, and clear system datasets
- **User Management**: Create and manage system users with role-based access
- **Audit Logging**: Track all system changes and administrative actions

## Development

### Prerequisites

- Node.js >= 18
- npm or yarn

### Setup

```bash
npm install
```

### Running

```bash
npm run dev
```

The app will be available at `http://localhost:5173` in development mode.

### Building

```bash
npm run build
```

The build output is placed in `dist/` and will be served by the ASP.NET Core API at `/apps/admin/`.

### Linting

```bash
npm run lint
```

## API Endpoints

The Admin app relies on the following API endpoints:

- `GET/PUT /api/admin/config` - System configuration
- `GET/POST/DELETE /api/admin/users` - User management
- `POST /api/admin/data/refresh` - Refresh all datasets
- `GET /api/admin/data/export/:dataset` - Export dataset
- `DELETE /api/admin/data/:dataset` - Clear dataset
- `GET /api/admin/audit-log` - Retrieve audit log entries
- `GET /api/admin/audit-log/export` - Export audit log as CSV

## Architecture

The Admin app is a React SPA built with Vite. It communicates with the Aerie API to perform administrative tasks.

### Key Components

- `ConfigPanel` - System configuration editor
- `DataManagementPanel` - Dataset management and maintenance
- `UsersPanel` - User creation and management
- `AuditLogPanel` - Audit log viewer and exporter

## Styling

The app uses CSS variables for theming and supports both light and dark modes via CSS media queries.
