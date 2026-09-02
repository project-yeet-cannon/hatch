import { defineConfig } from 'vite'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

// The shared page chrome, built as a plain script + stylesheet rather than as
// an app.
//
// Every other workspace under apps/ builds an index.html that *is* a page.
// This one builds a bar to put on somebody else's page: Swagger UI, which is
// Swashbuckle's document, and apps/logo, a static export. Neither can import
// from @aerie/ui, so the library comes to them as two files they reference by
// URL.
//
// Hence the fixed output names. `topbar.js` and `topbar.css` are spelled into
// Program.cs's UseSwaggerUI call and into apps/logo/index.html, so a content
// hash in either name would break both on every build. That is safe to serve:
// the static-file middleware in Program.cs hands `no-cache` (revalidate before
// reuse) to everything outside an `/assets/` path and `immutable` to
// everything inside one - so these two revalidate on each load, while the
// Manrope faces they pull in, which Vite does content-hash into assets/, are
// cached hard and forever.
//
// NOT `build.lib`, which is the obvious way to build a script-and-stylesheet
// pair and the wrong one here: library mode ignores assetsInlineLimit and
// base64s every asset into the output. That put all five Manrope subsets
// inline and made topbar.css 107KB - a 77KB gzipped stylesheet on a page whose
// only job is to carry a 48px bar. Emitted as files instead, the browser
// fetches the one subset its text needs, and caches it forever.
//
// No aerieRevision plugin: it stamps the build's git identity into an
// index.html, and this build does not produce one.
const __dirname = dirname(fileURLToPath(import.meta.url))
const projectRoot = resolve(__dirname, '../../../..')

export default defineConfig({
  // Absolute, so the url() Vite rewrites for the font resolves from whatever
  // page mounts the bar - /swagger/index.html included, which is nowhere near
  // /apps/chrome/.
  base: '/apps/chrome/',
  build: {
    outDir: resolve(projectRoot, 'src/Aerie.Api/wwwroot/apps/chrome'),
    emptyOutDir: true,
    // One stylesheet, so the host page has one <link> to add.
    cssCodeSplit: false,
    // Nothing base64'd into that stylesheet. Left at the default, Vite inlines
    // the smallest Manrope subset (cyrillic-ext) and it alone accounts for 9 of
    // topbar.css's 10KB - bytes every page pays for on every load to carry a
    // subset no page here will render. As a file it is fetched only by text
    // that needs it, and then cached immutably.
    assetsInlineLimit: 0,
    rollupOptions: {
      input: resolve(__dirname, 'src/main.ts'),
      output: {
        // IIFE, not ES: the logo page's <script> tag is a plain one, and a
        // module script would also defer past the point where the bar wants to
        // have settled the theme.
        format: 'iife',
        entryFileNames: 'topbar.js',
        chunkFileNames: 'assets/[name]-[hash].js',
        // The stylesheet is referenced by name from two host pages; everything
        // else Vite emits here is a font, which nothing names and which wants
        // the hash so it can be cached immutably.
        assetFileNames: (asset) =>
          asset.names?.some((name) => name.endsWith('.css'))
            ? 'topbar.css'
            : 'assets/[name]-[hash][extname]',
      },
    },
  },
})
