import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

// Bundles straight into the ASP.NET Core static files folder that Aerie.Api
// serves at /apps/dashboard (see Program.cs).
const __dirname = dirname(fileURLToPath(import.meta.url))
const projectRoot = resolve(__dirname, '../../../..')

export default defineConfig({
  base: '/apps/dashboard/',
  plugins: [react()],
  server: {
    proxy: {
      // Aerie.Api (see Properties/launchSettings.json) - needed for dev-only
      // pages like dev-theme.html that call the API directly from `npm run dev`.
      '/api': 'http://localhost:5197',
    },
  },
  build: {
    outDir: resolve(projectRoot, 'src/Aerie.Api/wwwroot/apps/dashboard'),
    emptyOutDir: true,
  },
})
