import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';
import { VitePWA } from 'vite-plugin-pwa';
import { isCacheableApiRequest, profilePartitionedKey } from './src/offline/apiCache.ts';
import { buildAppVersion, buildReleaseDate, sourceRef } from './src/versionModel.ts';

const releaseVersion = readFileSync(new URL('../../version.txt', import.meta.url), 'utf8');
const officialBuild = process.env.APP_OFFICIAL_BUILD === 'true';
const appVersion = buildAppVersion(releaseVersion, officialBuild);

export default defineConfig({
  define: {
    __APP_VERSION__: JSON.stringify(appVersion),
    __APP_OFFICIAL_BUILD__: JSON.stringify(officialBuild),
    __APP_RELEASE_DATE__: JSON.stringify(buildReleaseDate(process.env.APP_RELEASE_DATE, officialBuild)),
    __APP_SOURCE_REF__: JSON.stringify(sourceRef(releaseVersion, officialBuild)),
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
        // Last known data when offline (requirements 7.3), kept per profile: the actor is chosen by the X-Profile-Id
        // header, which is not part of the URL. The rule and the key live in src/offline/apiCache.ts; workbox copies
        // those functions into the service worker, so they must stay self-contained.
        runtimeCaching: [
          {
            urlPattern: isCacheableApiRequest,
            handler: 'NetworkFirst',
            options: {
              cacheName: 'api-get',
              networkTimeoutSeconds: 4,
              expiration: { maxEntries: 200, maxAgeSeconds: 7 * 24 * 60 * 60 },
              plugins: [{ cacheKeyWillBeUsed: profilePartitionedKey }],
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
