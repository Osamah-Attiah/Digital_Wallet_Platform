FROM golang:1.27.1-alpine@sha256:8a5910f31396cd4d89662f56c68b3ae31d374308270a1c3bd96672ee5ed43414 AS build
WORKDIR /build
COPY ops/containers/probe/main.go ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /probe main.go
FROM otel/opentelemetry-collector-contrib:0.162.0@sha256:39923a8e431bd1f57be82411999d389fcfe40857492e4365456d97a4c1f74be6
ARG VERSION
ARG GIT_SHA
ARG BUILD_DATE
LABEL org.opencontainers.image.version=$VERSION org.opencontainers.image.revision=$GIT_SHA org.opencontainers.image.created=$BUILD_DATE
COPY --from=build /probe /probe
USER 10001:10001
HEALTHCHECK --interval=10s --timeout=4s --retries=12 CMD ["/probe", "http://127.0.0.1:13133/"]
