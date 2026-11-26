# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Clearr/Clearr.csproj src/Clearr/
RUN dotnet restore src/Clearr/Clearr.csproj
COPY src/Clearr/ src/Clearr/
ARG VERSION=0.1.0
RUN dotnet publish src/Clearr/Clearr.csproj -c Release -o /app -p:UseAppHost=false -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV CLEARR_PORT=8585 \
    CLEARR_CONFIG_DIR=/config \
    TZ=Etc/UTC
VOLUME /config
EXPOSE 8585 8586
LABEL org.opencontainers.image.title="Clearr" \
      org.opencontainers.image.description="Clears shows, seasons and movies through Sonarr/Radarr once everyone who started them in Jellyfin has finished." \
      org.opencontainers.image.source="https://github.com/samuelcomplex/clearr"
ENTRYPOINT ["dotnet", "Clearr.dll"]
