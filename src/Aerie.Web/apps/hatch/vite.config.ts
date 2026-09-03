import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { aerieRevision } from '../../vite-plugin-aerie-revision.mjs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

// Bundles straight into the ASP.NET Core static files folder that Aerie.Api
// serves at /apps/hatch (see Program.cs).
const __dirname = dirname(fileURLToPath(import.meta.url))
const projectRoot = resolve(__dirname, '../../../..')

export default defineConfig({
  base: '/apps/hatch/',
  plugins: [react(), aerieRevision({ app: 'hatch' })],
  build: {
    outDir: resolve(projectRoot, 'src/Aerie.Api/wwwroot/apps/hatch'),
    emptyOutDir: true,
  },
})
