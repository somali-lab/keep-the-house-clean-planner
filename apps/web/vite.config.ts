import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';
import { VitePWA } from 'vite-plugin-pwa';
import { buildAppVersion } from './src/versionModel.ts';

const releaseVersion = readFileSync(new URL('../../version.txt', import.meta.url), 'utf8');
const appVersion = buildAppVersion(releaseVersion, process.env.APP_OFFICIAL_BUILD === 'true');

export default defineConfig({
  define: {
    __APP_VERSION__: JSON.stringify(appVersion),
  },
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  plugins: [
    react(),
    tailwindcss(),
    VitePWA({
      registerType: 'prompt',
      includeAssets: ['icon.svg'],
      manifest: {
        name: 'Keep the House Clean',
        short_name: 'KTHC Planner',
        lang: 'en',
        start_url: '/',
        display: 'standalone',
        background_color: '#fbf6ee',
        theme_color: '#c0643f',
        icons: [{ src: 'icon.svg', sizes: 'any', type: 'image/svg+xml', purpose: 'any' }],
      },
      workbox: {
        navigateFallbackDenylist: [/^\/api\//],
        // Last known data when offline. Exports (PDF/JSON) are never cached.
        runtimeCaching: [
          {
            urlPattern: ({ url, request }) =>
              request.method === 'GET' && url.pathname.startsWith('/api/') && !url.pathname.startsWith('/api/export/'),
            handler: 'NetworkFirst',
            options: {
              cacheName: 'api-get',
              networkTimeoutSeconds: 4,
              expiration: { maxEntries: 200, maxAgeSeconds: 7 * 24 * 60 * 60 },
            },
          },
        ],
      },
    }),
  ],
  server: {
    proxy: {
      '/api': 'http://localhost:3000',
    },
  },
});
