import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'

// Bundles straight into the ASP.NET Core static files folder that Aerie.Api
// serves at /dashboard (see Program.cs).
export default defineConfig({
  base: '/dashboard/',
  plugins: [react()],
  build: {
    outDir: fileURLToPath(new URL('../Aerie.Api/wwwroot/dashboard', import.meta.url)),
    emptyOutDir: true,
  },
})
