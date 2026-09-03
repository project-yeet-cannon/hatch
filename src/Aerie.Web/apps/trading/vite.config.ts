import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { aerieRevision } from '../../vite-plugin-aerie-revision.mjs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

// The one app in this workspace that does NOT build into
// src/Aerie.Api/wwwroot/apps/. docs/plans/trading.md Phase 7: "this SPA is
// served by the trading service, not by Aerie.Api, so it does not join
// wwwroot/apps/". It bundles into the trading package instead, beside the
// FastAPI module that serves it (aerie_trading/control/spa.py), because that
// image's build context is src/Aerie.Trading and Docker refuses a COPY that
// escapes it - the extraction seam expressed in a path.
//
// `base: '/'` for the same reason: the trading service serves this app at the
// root of its own host, not under /apps/<name>/.
const __dirname = dirname(fileURLToPath(import.meta.url))
const projectRoot = resolve(__dirname, '../../../..')

export default defineConfig({
  base: '/',
  plugins: [react(), aerieRevision({ app: 'trading' })],
  server: {
    proxy: {
      // The trading control plane, run locally with
      // `python -m aerie_trading.control`. Its port is the container's, so
      // `npm run dev` talks to the same address the pod listens on.
      '/api': 'http://localhost:8080',
    },
  },
  build: {
    outDir: resolve(projectRoot, 'src/Aerie.Trading/aerie_trading/control/static'),
    emptyOutDir: true,
  },
})
