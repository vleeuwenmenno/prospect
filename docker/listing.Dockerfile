# The PROSPECT listing server. Build from the repository root:
#   docker build -f docker/listing.Dockerfile -t prospect-listing .
# Configure with environment variables (Listing__TrustedProxies=..., see docs/listing-server.md).

# Build managed code on the builder CPU; portable publish includes SQLite native assets for amd64 and arm64.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Project files first, so the restore layer is reused while only code changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Prospect.Listing/Prospect.Listing.csproj src/Prospect.Listing/
RUN dotnet restore src/Prospect.Listing/Prospect.Listing.csproj
COPY src/Prospect.Listing/ src/Prospect.Listing/
RUN dotnet publish src/Prospect.Listing/Prospect.Listing.csproj -c Release --no-restore -p:PlatformTarget=AnyCPU -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1
# Optional accounts persist SQLite and its WAL in a volume owned by the runtime user.
RUN mkdir /data && chown $APP_UID:$APP_UID /data
VOLUME /data
USER $APP_UID
EXPOSE 8080
# The runtime image has no curl or wget: the service's own binary asks it for /healthz.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "prospect-listing.dll", "--health-check"]
ENTRYPOINT ["dotnet", "prospect-listing.dll"]
