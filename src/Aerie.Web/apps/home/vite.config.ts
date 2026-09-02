import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { aerieRevision } from '../../vite-plugin-aerie-revision.mjs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

// Bundles straight into the ASP.NET Core static files folder that Aerie.Api
// serves at /apps/home (see Program.cs), which is also where `/` and `/apps/`
// redirect to.
//
// It builds to apps/home/ like every other app rather than to apps/ itself,
// which would have preserved /apps/ as the picker's own URL. That alternative
// needs `emptyOutDir: false`, because emptying wwwroot/apps would wipe every
// sibling app's bundle - a trap one absent-minded edit away at all times.
// Two redirect lines are cheaper than living next to it.
const __dirname = dirname(fileURLToPath(import.meta.url))
const projectRoot = resolve(__dirname, '../../../..')

export default defineConfig({
  base: '/apps/home/',
  plugins: [react(), aerieRevision({ app: 'home' })],
  build: {
    outDir: resolve(projectRoot, 'src/Aerie.Api/wwwroot/apps/home'),
    emptyOutDir: true,
  },
})
