# Web image (STORY-045, DEC-049). Build context: the repository root.
# Builds the Angular app with Nx, then serves it with nginx, which also proxies /api (REST and the
# SignalR hub) to the API container, so the app and the API share one origin (DEC-013).

ARG NODE=node:22.23.3-alpine3.24
ARG NGINX=nginx:1.30.6-alpine3.24

FROM ${NODE} AS build
ENV NX_DAEMON=false NX_NO_CLOUD=true NX_SKIP_NX_CACHE=true
WORKDIR /src
COPY package.json package-lock.json .npmrc .nvmrc ./
RUN npm ci --no-audit --no-fund
COPY nx.json tsconfig.base.json eslint.config.mjs ./
COPY apps/web ./apps/web
RUN npx nx build web --configuration=production --output-style=static

FROM ${NGINX} AS web
COPY infrastructure/production/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/apps/web/browser /usr/share/nginx/html
EXPOSE 80
HEALTHCHECK --interval=10s --timeout=5s --start-period=10s --retries=6 \
  CMD ["wget", "-q", "-O", "/dev/null", "http://127.0.0.1/"]
