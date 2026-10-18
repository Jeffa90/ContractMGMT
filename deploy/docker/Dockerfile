# Enterprise Contract Platform - container image (Production-capable; refuses Demo settings in Production).
# Status: Scaffolded / Not Verified unless deploy/README.md records a build for the current commit.
# Build context is the repository root:  docker build -f deploy/docker/Dockerfile -t ecp:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
# Files outside src/ that the build reads (checked by tools/check-repo.py: every such path must be copied here and not ignored).
# templates/packs/ holds the pack SOURCES; the host build turns them into read-only .ecpkg files under /app/packs (external assets, not embedded resources).
COPY templates/packs/ templates/packs/
COPY sample-data/ sample-data/
COPY requirements/02-reference/ requirements/02-reference/
# upgrades/ holds the frozen migration baselines, the migration review register and the upgrade manifest; the Recovery module embeds them as resources (docs/UPGRADES.md).
COPY upgrades/ upgrades/
RUN dotnet publish src/Ecp.Host/Ecp.Host.csproj -c Release -o /out --no-self-contained /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .
# /app/packs holds the shipped solution packs (unsigned: no key is held in this repository). Production needs operator-signed packs and a trusted key (docs/PACK_SIGNING.md).
# Non-root user; persistent state lives only under /var/ecp (mount a volume), never in the image.
RUN mkdir -p /var/ecp && chown -R $APP_UID:$APP_UID /var/ecp
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Ecp__DataDirectory=/var/ecp \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
VOLUME ["/var/ecp"]
ENTRYPOINT ["dotnet", "Ecp.Host.dll"]
