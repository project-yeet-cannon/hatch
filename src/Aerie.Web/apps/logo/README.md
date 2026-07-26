# Aerie Logo

A static export from Claude Design (`Parametric Bird Mark.dc.html` + `support.js`) showing the parametric bird
wordmark: a single generative outline built from a handful of numeric parameters (head size, neck, tilt, beak,
tail, plumpness, wing), rendered as an SVG hero mark, a wordmark lockup, a legibility ladder, and a family of
named variants.

## Development

Unlike `apps/dashboard` and `apps/admin`, this isn't a Vite/React project — it's a self-contained static page.
`support.js` is a generated runtime (bundled React/ReactDOM loaded from a CDN at runtime) that interprets the
`<x-dc>` template in `index.html`. There's no build step: edit `index.html` directly and open it in a browser,
or serve the folder and visit `/apps/logo/`.

Do not hand-edit `support.js` — it's generated output (see its header comment) and should be replaced wholesale
if the design is re-exported from Claude Design.
