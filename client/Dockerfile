# Frontend container: build the Vue app, then serve it with nginx.
# nginx also reverse-proxies /api to the backend container, so the browser sees a
# single origin (no CORS) — same as the dev Vite proxy. Build context is ./client.
FROM node:22-alpine AS build
WORKDIR /client
COPY package.json package-lock.json ./
RUN npm ci
COPY . ./
# Vite inlines VITE_* values at build time, so production config must be passed
# as build args (not runtime env). Defaults keep SSO off and dev-login disabled.
ARG VITE_AZURE_AD_CLIENT_ID=""
ARG VITE_AZURE_AD_TENANT_ID=""
ARG VITE_AZURE_AD_REDIRECT_URI=""
ARG VITE_ALLOW_DEV_LOGIN="false"
ENV VITE_AZURE_AD_CLIENT_ID=$VITE_AZURE_AD_CLIENT_ID \
    VITE_AZURE_AD_TENANT_ID=$VITE_AZURE_AD_TENANT_ID \
    VITE_AZURE_AD_REDIRECT_URI=$VITE_AZURE_AD_REDIRECT_URI \
    VITE_ALLOW_DEV_LOGIN=$VITE_ALLOW_DEV_LOGIN
RUN npm run build

# Unprivileged nginx: runs as a non-root user (uid 101) and listens on 8080.
# The image still renders templates from /etc/nginx/templates at startup.
FROM nginxinc/nginx-unprivileged:1.27-alpine AS final
# Template is rendered at startup with the API_URL env var (envsubst).
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /client/dist /usr/share/nginx/html
ENV API_URL=http://api:8080
EXPOSE 8080
# Serve the SPA shell as the liveness signal (busybox wget ships in the alpine image).
# Use 127.0.0.1 explicitly: "localhost" resolves to ::1 first, but nginx binds IPv4.
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD wget -q --spider http://127.0.0.1:8080/ || exit 1
