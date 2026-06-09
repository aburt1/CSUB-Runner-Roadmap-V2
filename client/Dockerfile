# Frontend container: build the Vue app, then serve it with nginx.
# nginx also reverse-proxies /api to the backend container, so the browser sees a
# single origin (no CORS) — same as the dev Vite proxy. Build context is ./client.
FROM node:22-alpine AS build
WORKDIR /client
COPY package.json package-lock.json ./
RUN npm ci
COPY . ./
RUN npm run build

FROM nginx:1.27-alpine AS final
# Template is rendered at startup with the API_URL env var (envsubst).
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /client/dist /usr/share/nginx/html
ENV API_URL=http://api:8080
EXPOSE 80
