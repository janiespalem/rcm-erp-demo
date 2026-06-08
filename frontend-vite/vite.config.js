import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
  plugins: [vue({ template: { transformAssetUrls: false } })],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': 'http://localhost:8000',
      '/uploads': 'http://localhost:8000',
      '/static': 'http://localhost:8000',
      '/drawings': 'http://localhost:8000',
      '/kalkulator-lego': 'http://localhost:8000',
    },
  },
})
