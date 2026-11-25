import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

// The build goes into the API's wwwroot, so the API serves the front end from the same IIS site.
// Local development: run the API on http://localhost:5080 (Contracts:AuthMode=Header) and set DEV_ACCOUNT, e.g. DEV\jsmith.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  build: { outDir: '../api/wwwroot', emptyOutDir: true, assetsInlineLimit: 0, chunkSizeWarningLimit: 800 },
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5080',
        headers: process.env.DEV_ACCOUNT ? { 'X-Signed-In-As': process.env.DEV_ACCOUNT } : {},
      },
      '/fonts': 'http://localhost:5080',
    },
  },
  test: { environment: 'jsdom', globals: true, setupFiles: ['./src/test-setup.ts'], include: ['src/**/*.test.{ts,tsx}'] },
})
