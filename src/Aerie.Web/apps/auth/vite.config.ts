import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

// Bundles straight into the ASP.NET Core static files folder that Aerie.Api
// serves at /apps/auth (see Program.cs).
const __dirname = dirname(fileURLToPath(import.meta.url))
const projectRoot = resolve(__dirname, '../../../..')

export default defineConfig({
  base: '/apps/auth/',
  plugins: [react()],
  server: {
    // Aerie.Api (see Properties/launchSettings.json). Redemption is the only
    // thing this app does, so `npm run dev` is useless without the real
    // backend behind it.
    proxy: {
      '/api': 'http://localhost:5197',
    },
  },
  build: {
    outDir: resolve(projectRoot, 'src/Aerie.Api/wwwroot/apps/auth'),
    emptyOutDir: true,
  },
})
