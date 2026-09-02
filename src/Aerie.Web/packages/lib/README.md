# @aerie/lib

The logic two apps share. Not a design system and not a grab bag: a module
lands here when the *same* module already existed in two apps and had started to
drift.

That is not hypothetical. `scale.ts` and `cameraStream.ts` were duplicated in
`apps/admin` and `apps/dashboard` and both copies had diverged — same filename,
different contents, and nothing in the build that would ever have said so. That
duplication is the concrete debt the workspace conversion was for, and this
package is where it is paid.

Two rules keep it from becoming a dumping ground:

- **Framework-free.** Nothing here imports React. Anything that renders belongs
  in `@aerie/ui`; anything that knows about one app's API belongs in that app.
- **Per-module entry points**, not a barrel. A consumer takes
  `@aerie/lib/scale` and gets nothing else. `@aerie/ui` exports a barrel because
  a component's CSS import is a side effect it wants in the bundle anyway; there
  is no such reason here, and a barrel would put the camera protocol into the
  bundle of an app that only wanted to draw an axis.

The tests live next to the code and run with `npm test --workspace @aerie/lib`.
