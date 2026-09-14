# syntax=docker/dockerfile:1

# Must match the `playwright` version in apps/server/package.json.
ARG PLAYWRIGHT_VERSION=1.63.0
ARG NODE_IMAGE=node:24-bookworm-slim
ARG MONGO_TOOLS_VERSION=100.18.0

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

# ---------- node binary source ----------
FROM ${NODE_IMAGE} AS node

# ---------- runtime: Playwright image (Chromium + deps) ----------
FROM mcr.microsoft.com/playwright:v${PLAYWRIGHT_VERSION}-noble AS runtime
ARG TARGETARCH
ARG MONGO_TOOLS_VERSION

# Node 24 (native TypeScript type stripping); /usr/local/bin precedes the image's own node.
COPY --from=node /usr/local/bin/node /usr/local/bin/node

# mongodump for nightly backups
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    case "${TARGETARCH:-amd64}" in \
      arm64) tools_arch=arm64 ;; \
      *) tools_arch=x86_64 ;; \
    esac; \
    curl -fsSL -o /tmp/mongo-tools.deb \
      "https://fastdl.mongodb.org/tools/db/mongodb-database-tools-ubuntu2404-${tools_arch}-${MONGO_TOOLS_VERSION}.deb"; \
    apt-get install -y --no-install-recommends /tmp/mongo-tools.deb; \
    rm /tmp/mongo-tools.deb; \
    rm -rf /var/lib/apt/lists/*; \
    node --version; \
    mongodump --version; \
    mkdir -p /backups /app; \
    chown pwuser:pwuser /backups /app

WORKDIR /app
ENV NODE_ENV=production \
    PORT=3000 \
    BACKUP_DIR=/backups \
    WEB_DIST_DIR=/app/apps/web/dist

COPY --from=deps --chown=pwuser:pwuser /app ./
COPY --chown=pwuser:pwuser package.json tsconfig.base.json ./
COPY --chown=pwuser:pwuser packages/shared/src packages/shared/src
COPY --chown=pwuser:pwuser apps/server/src apps/server/src
COPY --from=build --chown=pwuser:pwuser /app/apps/web/dist apps/web/dist

USER pwuser
EXPOSE 3000
CMD ["node", "apps/server/src/main.ts"]
