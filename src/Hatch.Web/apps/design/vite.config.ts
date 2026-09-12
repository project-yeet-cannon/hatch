import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { hatchRevision } from '../../vite-plugin-hatch-revision.mjs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

// Bundles straight into the ASP.NET Core static files folder that Hatch.Api
// serves at /apps/design (see Program.cs).
const __dirname = dirname(fileURLToPath(import.meta.url))
const projectRoot = resolve(__dirname, '../../../..')

export default defineConfig({
  base: '/apps/design/',
  plugins: [react(), hatchRevision({ app: 'design' })],
  build: {
    outDir: resolve(projectRoot, 'src/Hatch.Api/wwwroot/apps/design'),
    emptyOutDir: true,
  },
})
