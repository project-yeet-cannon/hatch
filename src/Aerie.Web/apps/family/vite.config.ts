import { defineConfig } from 'vite'
import type { Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import { createHash } from 'node:crypto'
import { readFileSync, readdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

// Bundles straight into the ASP.NET Core static files folder that Aerie.Api
// serves at /apps/family (see Program.cs).
const __dirname = dirname(fileURLToPath(import.meta.url))
const projectRoot = resolve(__dirname, '../../../..')
const base = '/apps/family/'

/**
 * Emits the service worker with a real precache list baked in.
 *
 * The alternative is vite-plugin-pwa, which brings Workbox along for a policy
 * this app states in ~40 lines of `src/sw.js`. What it *does* give you that a
 * hand-written worker can't is the list of hashed asset filenames, which only
 * exists after the bundle is built - so that one piece is what this plugin
 * supplies, and nothing else.
 *
 * Without it the first offline launch fails: the worker isn't controlling the
 * page during the very first visit, so nothing that visit loaded ends up in
 * the cache. Precaching at install time is what makes "airplane mode still
 * opens the shell" true after one visit rather than two.
 */
function serviceWorker(): Plugin {
  const templatePath = resolve(__dirname, 'src/sw.js')
  const publicDir = resolve(__dirname, 'public')

  return {
    name: 'aerie-family-sw',
    apply: 'build',
    // After vite:build-html, which emits index.html during generateBundle too.
    enforce: 'post',
    generateBundle(_options, bundle) {
      const template = readFileSync(templatePath, 'utf8')
      const publicFiles = readdirSync(publicDir)
      // index.html is listed explicitly as well as read off the bundle: it is
      // the one file the shell cannot work without, so it shouldn't depend on
      // another plugin's emit having already happened.
      const files = [...new Set(['index.html', ...Object.keys(bundle), ...publicFiles])].sort()

      // Cache name doubles as the deploy fence - a changed name means the new
      // worker builds a fresh cache and drops the old one on activate. Hashed
      // bundle filenames cover source changes; public/ files aren't hashed, so
      // their contents go into the digest directly.
      const digest = createHash('sha256').update(template)
      for (const file of files) digest.update(file)
      for (const file of publicFiles) digest.update(readFileSync(resolve(publicDir, file)))

      this.emitFile({
        type: 'asset',
        fileName: 'sw.js',
        source: template
          .replace('__BUILD_ID__', digest.digest('hex').slice(0, 12))
          .replace('__PRECACHE__', JSON.stringify(files.map((f) => base + f), null, 2))
          .replaceAll('__BASE__', base),
      })
    },
  }
}

export default defineConfig({
  base,
  plugins: [react(), serviceWorker()],
  server: {
    // Aerie.Api (see Properties/launchSettings.json) - the shell is all API
    // data, so `npm run dev` is useless without the real backend behind it.
    proxy: {
      '/api': 'http://localhost:5197',
    },
  },
  build: {
    outDir: resolve(projectRoot, 'src/Aerie.Api/wwwroot/apps/family'),
    emptyOutDir: true,
  },
})
