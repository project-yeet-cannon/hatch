/**
 * Stamps the build's `aerie-revision` into every SPA's index.html, and makes
 * the page send it back on every API call it makes.
 *
 * See docs/plans/version.md. Two things about this plugin are load-bearing and
 * neither is obvious from what it emits.
 *
 * **It writes into index.html, never into the bundle.** The obvious
 * implementation is a Vite `define` that inlines the sha as a constant, and it
 * would be wrong: Vite content-hashes everything under assets/, so a sha in the
 * bundle changes that bundle's hash on *every* commit. Services/AppVersionService.cs
 * derives a frontend's identity from exactly those hashed filenames, precisely
 * so that a backend-only deploy - by far the common case - leaves it unchanged
 * and the wall tablets don't reload for a change they can't see. A `define`
 * here would turn every deploy into a full-fleet reload. index.html carries no
 * hash, is served `no-cache` (Program.cs), and is already the file
 * AppVersionService reads; a <meta> in it perturbs neither that service's
 * extraction (which matches /apps/<app>/assets/ URLs) nor the client's (which
 * reads script[src]/link[href]).
 *
 * **The interceptor lives here rather than in each app's source.** There are
 * six SPAs with six copies of clientLogger.ts and no shared frontend package;
 * a runtime module would have become a seventh sextuplet. Emitting the
 * behaviour from the one place that already has the value keeps it single-
 * sourced, and `head-prepend` gets it in front of the ui-logs ping that
 * index.html itself fires - so even that first request carries the header.
 *
 * The values come from the environment (Dockerfile.api passes build args
 * through; publish.yml computes them) rather than from git, so a local
 * `npm run build` needs no repository and honestly reports `dev`.
 */

/** What a local build reports. Deliberately not a git call - see the header. */
const DEV_REVISION = 'dev'
const DEV_SEQUENCE = '0'

export interface AerieRevisionOptions {
  /** App directory name under wwwroot/apps, e.g. "dashboard". Diagnostics only. */
  app: string
}

/**
 * Vite's own `Plugin` and `HtmlTagDescriptor` types, restated structurally
 * rather than imported. This file sits above the six apps and so above the six
 * node_modules trees - `import type { Plugin } from 'vite'` resolves from none
 * of them. Structural typing means the object still has to satisfy Vite's real
 * `PluginOption` at every call site, which is the check that actually matters;
 * this only has to be narrow enough not to widen `injectTo` to `string`.
 */
interface HtmlTag {
  tag: string
  attrs?: Record<string, string | boolean | undefined>
  children?: string
  injectTo?: 'head' | 'body' | 'head-prepend' | 'body-prepend'
}

interface RevisionPlugin {
  name: string
  transformIndexHtml(): HtmlTag[]
}

export function aerieRevision({ app }: AerieRevisionOptions): RevisionPlugin {
  const revision = process.env.AERIE_REVISION || DEV_REVISION
  const sequence = process.env.AERIE_SEQUENCE || DEV_SEQUENCE

  return {
    name: 'aerie-revision',
    transformIndexHtml() {
      return [
        {
          tag: 'meta',
          attrs: { name: 'aerie-revision', content: revision },
          injectTo: 'head-prepend',
        },
        {
          tag: 'meta',
          attrs: { name: 'aerie-sequence', content: sequence },
          injectTo: 'head-prepend',
        },
        {
          tag: 'meta',
          attrs: { name: 'aerie-app', content: app },
          injectTo: 'head-prepend',
        },
        {
          tag: 'script',
          children: bootstrap(revision, sequence),
          injectTo: 'head-prepend',
        },
      ]
    },
  }
}

/**
 * Reads the stamped values into globals the app's own code can pick up (the
 * same shape as window.__uiLogSessionId, which index.html already sets for
 * clientLogger.ts), then wraps fetch so outbound API calls identify the build
 * that made them.
 *
 * Same-origin only, and that is correctness rather than caution: a custom
 * header on a cross-origin request triggers a CORS preflight, turning one round
 * trip into two on a request that was working fine. Every Aerie app reaches its
 * API same-origin anyway (see docs/reverse-proxy-architecture.md).
 *
 * Written as plain, conservative JS on purpose: it is injected verbatim into
 * index.html, which no build step transpiles, so whatever is written here is
 * what a browser parses. The kiosk tablets exist because their stock WebView
 * (Chrome 74) could not parse the dashboard's bundle at all - this is not the
 * file in which to discover the next such limit.
 */
function bootstrap(revision: string, sequence: string): string {
  return `
(function () {
  try {
    window.__aerieRevision = ${JSON.stringify(revision)};
    window.__aerieSequence = ${JSON.stringify(sequence)};

    var nativeFetch = window.fetch;
    if (typeof nativeFetch !== 'function') return;

    window.fetch = function (input, init) {
      try {
        var url = typeof input === 'string' ? input : (input && input.url);
        // Relative URLs resolve against the document and are same-origin by
        // construction; an absolute one has to be checked.
        if (url && new URL(url, location.href).origin === location.origin) {
          var headers = new Headers((init && init.headers) || (typeof input !== 'string' && input && input.headers) || undefined);
          headers.set('Aerie-Client-Revision', window.__aerieRevision);
          headers.set('Aerie-Client-Sequence', window.__aerieSequence);
          // The kiosk shell tells the page its own revision (apps/kiosk); it is
          // a separate artifact from this bundle and drifts independently, so
          // it travels as its own header rather than overwriting this one.
          if (window.__aerieShellRevision) {
            headers.set('Aerie-Shell-Revision', window.__aerieShellRevision);
          }

          // A shallow copy rather than a mutation: init belongs to the caller,
          // and a caller that reuses one options object across several fetches
          // must not accumulate our headers into it.
          var next = {};
          for (var key in init) {
            if (Object.prototype.hasOwnProperty.call(init, key)) next[key] = init[key];
          }
          next.headers = headers;
          init = next;
        }
      } catch (e) {
        // A header is never worth failing a request over.
      }
      return nativeFetch.call(this, input, init);
    };
  } catch (e) {}
})();
`.trim()
}
