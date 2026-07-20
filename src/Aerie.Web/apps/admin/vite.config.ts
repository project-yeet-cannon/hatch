import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

// Bundles straight into the ASP.NET Core static files folder that Aerie.Api
// serves at /apps/admin (see Program.cs).
const __dirname = dirname(fileURLToPath(import.meta.url))
const projectRoot = resolve(__dirname, '../../../..')

export default defineConfig({
  base: '/apps/admin/',
  plugins: [react()],
  build: {
    outDir: resolve(projectRoot, 'src/Aerie.Api/wwwroot/apps/admin'),
    emptyOutDir: true,
  },
})
