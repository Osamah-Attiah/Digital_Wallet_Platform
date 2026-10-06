FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /build
COPY global.json Directory.Build.props ./
COPY src/ src/
COPY ops/postgres/runtime-grants.sql ops/postgres/runtime-grants.sql
RUN dotnet restore src/DigitalWallet.Worker/DigitalWallet.Worker.csproj --locked-mode
RUN dotnet publish src/DigitalWallet.Worker/DigitalWallet.Worker.csproj -c Release --no-restore -o /publish -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false -warnaserror \
    && rm -f /publish/appsettings.Development.json \
    && mkdir /reports && touch /reports/.initialized && chown -R 1654:1654 /reports
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd
ARG VERSION
ARG GIT_SHA
ARG BUILD_DATE
LABEL org.opencontainers.image.title="DigitalWallet.Worker" \
      org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$GIT_SHA \
      org.opencontainers.image.created=$BUILD_DATE
WORKDIR /app
COPY --from=build /publish/ ./
COPY --from=build --chown=1654:1654 /reports/ /var/reports/
USER 1654:1654
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=12 CMD ["dotnet", "DigitalWallet.Worker.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "DigitalWallet.Worker.dll"]
