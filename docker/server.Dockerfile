# The PROSPECT dedicated game server. Build from the repository root:
#   docker build -f docker/server.Dockerfile -t prospect-server .
# Run: docker run -p 27420:27420/udp prospect-server --name "My server" --set <id>=<value>
# Public, with a password: add -e PROSPECT_PASSWORD=... before the image and --visibility public after it.
# Everything after the image name goes to the server (see `--help`).
# Commented defaults ship at /app/content/server/server-settings.jsonc; opt in with --settings <file>.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Project files first, so the restore layer is reused while only code changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Distribution.targets src/
COPY src/Prospect.Engine/Prospect.Engine.csproj src/Prospect.Engine/
COPY src/Prospect.Game/Prospect.Game.csproj src/Prospect.Game/
COPY src/Prospect.Server/Prospect.Server.csproj src/Prospect.Server/
RUN dotnet restore src/Prospect.Server/Prospect.Server.csproj -r linux-x64
COPY src/Prospect.Engine/ src/Prospect.Engine/
COPY src/Prospect.Game/ src/Prospect.Game/
COPY src/Prospect.Server/ src/Prospect.Server/
# content/, NOTICE.txt and LICENSES/ are copied next to the binaries by Distribution.targets.
COPY content/ content/
COPY NOTICE.txt ./
COPY LICENSES/ LICENSES/
RUN dotnet publish src/Prospect.Server/Prospect.Server.csproj -c Release -r linux-x64 --self-contained false \
    --no-restore -o /app

# The plain runtime: the server has no web stack, and no Vulkan, GLFW or display.
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
ENV DOTNET_NOLOGO=1
# Named volumes inherit this ownership; bind mounts must be writable by APP_UID (1654).
RUN mkdir /data && chown "$APP_UID:$APP_UID" /data
VOLUME ["/data"]
# Saving remains opt-in with --world /data/<world>.
USER $APP_UID
EXPOSE 27420/udp
ENTRYPOINT ["dotnet", "prospect-server.dll"]
