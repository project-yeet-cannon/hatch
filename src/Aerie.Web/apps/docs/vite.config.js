import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
// Bundles straight into the ASP.NET Core static files folder that Aerie.Api
// serves at /apps/docs (see Program.cs).
const __dirname = dirname(fileURLToPath(import.meta.url));
const projectRoot = resolve(__dirname, '../../../..');
export default defineConfig({
    base: '/apps/docs/',
    plugins: [react()],
    build: {
        outDir: resolve(projectRoot, 'src/Aerie.Api/wwwroot/apps/docs'),
        emptyOutDir: true,
    },
});
