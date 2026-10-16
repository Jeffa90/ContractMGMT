# Enterprise Contract Platform - container image (Production-capable; refuses Demo settings in Production).
# Status: Scaffolded / Not Verified unless deploy/README.md records a build for the current commit.
# Build context is the repository root:  docker build -f deploy/docker/Dockerfile -t ecp:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
# Files outside src/ that the projects embed at build time (checked by tools/check-repo.py: every such path must be copied here and not ignored).
COPY templates/packs/ templates/packs/
COPY sample-data/ sample-data/
COPY requirements/02-reference/ requirements/02-reference/
RUN dotnet publish src/Ecp.Host/Ecp.Host.csproj -c Release -o /out --no-self-contained /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .
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
