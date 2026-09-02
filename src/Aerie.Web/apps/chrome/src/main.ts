/* The entry point, and all of it.
   ---------------------------------------------------------------------------
   The bar itself is @aerie/ui/standalone/topbar - it belongs to the library,
   beside the React component whose stylesheet it shares. This app is only the
   build: the thing that turns that module into a plain .js and .css pair a
   page can reference with a <script src> and a <link href>.

   Importing it is the whole program. The module auto-mounts from
   `window.aerieTopBar`, so a host page configures the bar by setting that
   global rather than by calling anything. */
import '@aerie/ui/standalone/topbar';
