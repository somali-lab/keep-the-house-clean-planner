# syntax=docker/dockerfile:1

ARG NODE_IMAGE=node:24-bookworm-slim

# ---------- build: install all deps, build the web app ----------
FROM ${NODE_IMAGE} AS build
WORKDIR /app
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
COPY package.json package-lock.json ./
COPY packages/shared/package.json packages/shared/
COPY apps/server/package.json apps/server/
COPY apps/web/package.json apps/web/
RUN npm ci
COPY tsconfig.base.json ./
COPY version.txt ./
COPY packages/shared packages/shared
COPY apps/web apps/web
RUN npm run build --workspace apps/web

# ---------- deps: production dependencies only ----------
FROM ${NODE_IMAGE} AS deps
WORKDIR /app
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
COPY package.json package-lock.json ./
COPY packages/shared/package.json packages/shared/
COPY apps/server/package.json apps/server/
COPY apps/web/package.json apps/web/
RUN npm ci --omit=dev

# ---------- runtime: Node + the only browser the app needs ----------
FROM ${NODE_IMAGE} AS runtime

# PDF rendering is always headless, so install only Playwright's Chromium headless shell.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

WORKDIR /app
ENV NODE_ENV=production \
    PORT=3000 \
    WEB_DIST_DIR=/app/apps/web/dist

COPY --from=deps --chown=node:node /app ./
RUN ./node_modules/.bin/playwright install --with-deps --only-shell chromium >/tmp/playwright-install.log 2>&1 \
    || (cat /tmp/playwright-install.log >&2; exit 1); \
    rm -f /tmp/playwright-install.log; \
    rm -rf /var/lib/apt/lists/*
COPY --chown=node:node package.json tsconfig.base.json ./
COPY --chown=node:node packages/shared/src packages/shared/src
COPY --chown=node:node apps/server/src apps/server/src
COPY --from=build --chown=node:node /app/apps/web/dist apps/web/dist

USER node
EXPOSE 3000
CMD ["node", "apps/server/src/main.ts"]
