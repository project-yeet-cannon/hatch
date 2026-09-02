# Aerie Logo

A static export from Claude Design (`Parametric Bird Mark.dc.html` + `support.js`) showing the parametric bird
wordmark: a single generative outline built from a handful of numeric parameters (head size, neck, tilt, beak,
tail, plumpness, wing), rendered as an SVG hero mark, a wordmark lockup, a legibility ladder, and a family of
named variants.

The page is live, not a static sheet. A parameters panel drives every mark on it: sliders grouped into Head,
Body, and Weight; four ink swatches; an editable wordmark; perch and show-family toggles; Reset; and a Download
SVG button that serializes the current mark. The outline itself is evaluated as a signed-distance field
(smooth-min union of body ellipse, neck cone, head, beak, and tail), traced with marching squares, resampled,
and smoothed into a single closed Bezier loop — so every parameter change re-solves the whole silhouette.

## Development

Unlike `apps/dashboard` and `apps/admin`, this isn't a Vite/React project — it's a self-contained static page.
`support.js` is a generated runtime (bundled React/ReactDOM loaded from a CDN at runtime) that interprets the
`<x-dc>` template in `index.html`. There's no build step: edit `index.html` directly and open it in a browser,
or serve the folder and visit `/apps/logo/`.

## The top bar

`index.html` carries three hand-added lines in its `<head>` that pull in the
shared Aerie bar from `/apps/chrome/` (built by `apps/chrome`). They are the
only part of the file that is not Claude Design's output, and a re-export
overwrites the file wholesale — put them back when that happens.

The bar is configured with `theme: 'light'`: this page is a fixed cream design
with no dark answer, so the bar pins to light and renders no theme control.
Everything below the bar is untouched.

Do not hand-edit `support.js` — it's generated output (see its header comment) and should be replaced wholesale
if the design is re-exported from Claude Design.
