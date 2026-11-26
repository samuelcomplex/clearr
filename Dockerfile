# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig ./
COPY src/Clearr/Clearr.csproj src/Clearr/
COPY src/Clearr.Core/Clearr.Core.csproj src/Clearr.Core/
RUN dotnet restore src/Clearr/Clearr.csproj
COPY src/Clearr/ src/Clearr/
COPY src/Clearr.Core/ src/Clearr.Core/
ARG VERSION=0.1.0
RUN dotnet publish src/Clearr/Clearr.csproj -c Release -o /app -p:UseAppHost=false -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --chmod=755 docker/entrypoint.sh /entrypoint.sh
COPY --chmod=755 docker/healthcheck.sh /healthcheck.sh
# PUID/PGID: the user Clearr runs as and owns /config with (linuxserver.io convention).
ENV CLEARR_PORT=8585 \
    CLEARR_CONFIG_DIR=/config \
    TZ=Etc/UTC \
    PUID=1000 \
    PGID=1000 \
    HOME=/tmp \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME /config
EXPOSE 8585 8586
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 CMD ["/healthcheck.sh"]
LABEL org.opencontainers.image.title="Clearr" \
      org.opencontainers.image.description="Clears shows, seasons and movies through Sonarr/Radarr once everyone who started them in Jellyfin has finished." \
      org.opencontainers.image.source="https://github.com/samuelcomplex/clearr" \
      org.opencontainers.image.licenses="GPL-3.0-or-later"
ENTRYPOINT ["/entrypoint.sh"]
