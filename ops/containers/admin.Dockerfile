FROM node:26.9.0-bookworm-slim@sha256:582460f614631b59b824ac6020533b9bf339c7fdf3a6d7db31abb6b4065f0212 AS frontend
WORKDIR /build
COPY apps/admin/package.json apps/admin/package-lock.json ./
RUN npm ci --ignore-scripts
COPY apps/admin/ ./
RUN npm run build
FROM golang:1.26.8-alpine@sha256:8ac98ca534ac3f51e1f420a1dd2c15e74c75cfa0f23f3ad27eb5d7236c349a0c AS server
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
