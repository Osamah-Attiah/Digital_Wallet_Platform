FROM node:24.19.0-bookworm-slim@sha256:a9f5f7c91a432850b2a8a7797adf5eadb6c733ceed61167806cee7ea7fbc29df AS frontend
WORKDIR /build
COPY apps/admin/package.json apps/admin/package-lock.json ./
RUN npm ci --ignore-scripts
COPY apps/admin/ ./
RUN npm run build
FROM golang:1.27.1-alpine@sha256:8a5910f31396cd4d89662f56c68b3ae31d374308270a1c3bd96672ee5ed43414 AS server
WORKDIR /build
COPY ops/containers/web/ ./
COPY apps/admin/security-headers.json ./
COPY --from=frontend /build/dist/ dist/
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /wallet-web .
FROM scratch
ARG VERSION
ARG GIT_SHA
ARG BUILD_DATE
LABEL org.opencontainers.image.title="DigitalWallet.Admin" \
      org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$GIT_SHA \
      org.opencontainers.image.created=$BUILD_DATE
COPY --from=server /wallet-web /wallet-web
USER 1654:1654
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=4s --start-period=10s --retries=12 CMD ["/wallet-web", "--healthcheck"]
ENTRYPOINT ["/wallet-web"]
