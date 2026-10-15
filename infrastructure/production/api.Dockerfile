# API and migration images (STORY-045, DEC-049). Build context: the repository root.
#   target `api`     - the ASP.NET Core API, listening on 8080 as the non-root `app` user.
#   target `migrate` - an EF Core migrations bundle that applies pending migrations once and exits.
# Both read Database__ConnectionString from the environment; no secret is baked into an image.

ARG DOTNET_SDK=mcr.microsoft.com/dotnet/sdk:8.0.425
ARG DOTNET_ASPNET=mcr.microsoft.com/dotnet/aspnet:8.0.31-bookworm-slim

FROM ${DOTNET_SDK} AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src/backend
COPY backend/global.json backend/Directory.Build.props backend/Directory.Packages.props backend/ChogGames.sln ./
COPY backend/.config ./.config
COPY backend/src ./src
RUN dotnet restore src/ChogGames.Api/ChogGames.Api.csproj \
 && dotnet restore src/ChogGames.Infrastructure/ChogGames.Infrastructure.csproj \
 && dotnet tool restore
RUN dotnet publish src/ChogGames.Api/ChogGames.Api.csproj -c Release --no-restore -o /out/api /p:UseAppHost=false
# The design-time factory reads Database__ConnectionString when the bundle runs.
RUN dotnet ef migrations bundle --configuration Release \
      --project src/ChogGames.Infrastructure --startup-project src/ChogGames.Infrastructure \
      --output /out/migrate/efbundle

FROM ${DOTNET_ASPNET} AS migrate
WORKDIR /app
COPY --from=build /out/migrate ./
USER $APP_UID
ENTRYPOINT ["./efbundle"]

FROM ${DOTNET_ASPNET} AS api
WORKDIR /app
COPY --from=build /out/api ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
# The image has no curl or wget; bash's /dev/tcp is enough for a readiness probe.
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=6 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "ChogGames.Api.dll"]
