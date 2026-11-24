# Build (plattformunabhängiges IL, daher auf der Build-Plattform)
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY StudIPDownloader/StudIPDownloader.csproj StudIPDownloader/
RUN dotnet restore StudIPDownloader/StudIPDownloader.csproj
COPY StudIPDownloader/ StudIPDownloader/
RUN dotnet publish StudIPDownloader/StudIPDownloader.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
COPY --chmod=755 docker/entrypoint.sh /entrypoint.sh

# SYNC_INTERVAL: Pause zwischen zwei Läufen (sleep-Format, z.B. 30m, 6h, 1d). 0 = nur einmal ausführen.
ENV SYNC_INTERVAL=6h \
    CONFIG=/config/config.ini \
    DATA=/data

RUN mkdir -p /config /data && chown "$APP_UID" /config /data
VOLUME ["/config", "/data"]
USER $APP_UID

ENTRYPOINT ["/entrypoint.sh"]
