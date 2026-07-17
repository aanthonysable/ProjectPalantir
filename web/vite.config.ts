import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5251',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api/, ''),
        // Large knowledge uploads (multi-GB PLC / zip)
        timeout: 0,
        proxyTimeout: 0,
      },
      '/hubs': {
        target: 'http://localhost:5251',
        ws: true,
        changeOrigin: true,
      },
    },
  },
})
