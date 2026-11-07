FROM node:22-alpine AS build
WORKDIR /src

COPY modules/dashboard/package*.json modules/dashboard/
RUN cd modules/dashboard && npm ci

COPY modules/dashboard modules/dashboard

ARG VITE_MINICLOUD_API_URL
ARG VITE_MINICLOUD_APP_DOMAIN
ARG VITE_MINICLOUD_SOURCE_URL
ARG VITE_FIREBASE_API_KEY
ARG VITE_FIREBASE_AUTH_DOMAIN
ARG VITE_FIREBASE_PROJECT_ID
ARG VITE_FIREBASE_STORAGE_BUCKET
ARG VITE_FIREBASE_MESSAGING_SENDER_ID
ARG VITE_FIREBASE_APP_ID
ARG VITE_FIREBASE_MEASUREMENT_ID

RUN cd modules/dashboard && npm run build

FROM nginx:1.27-alpine AS runtime
COPY modules/dashboard/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/modules/dashboard/dist /usr/share/nginx/html
EXPOSE 8080
