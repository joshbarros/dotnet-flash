FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY libs/ libs/
COPY apps/api/ apps/api/
RUN dotnet publish apps/api/Flash.Api.csproj -c Release -o /out

FROM node:22-bookworm-slim AS web-build
COPY --from=api-build /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet
ENV PATH="${PATH}:/usr/share/dotnet"
ENV NX_DAEMON=false
RUN apt-get update && apt-get install -y --no-install-recommends libicu72 libssl3 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY package*.json ./
RUN npm ci
COPY . .
RUN npx nx build web

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=api-build /out .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Flash.Api.dll"]

FROM nginxinc/nginx-unprivileged:stable-alpine AS web
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=web-build /src/dist/apps/web/browser /usr/share/nginx/html
EXPOSE 8080
USER 101
