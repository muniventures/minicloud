FROM node:22-alpine AS build
WORKDIR /src

COPY modules/dashboard/package*.json modules/dashboard/
RUN cd modules/dashboard && npm ci

COPY modules/dashboard modules/dashboard

RUN cd modules/dashboard && npm run build

FROM nginx:1.27-alpine AS runtime
RUN apk add --no-cache nodejs
COPY modules/dashboard/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/modules/dashboard/dist /usr/share/nginx/html
COPY modules/dashboard/dashboard-startup.mjs /usr/local/bin/dashboard-startup.mjs
COPY LICENSE /usr/share/nginx/html/LICENSE
COPY LICENSES /usr/share/nginx/html/licenses
ENTRYPOINT ["node", "/usr/local/bin/dashboard-startup.mjs"]
EXPOSE 8080
