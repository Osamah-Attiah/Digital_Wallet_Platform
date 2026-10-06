# Rebuild upstream 0.34.1 with the patched gRPC dependency. UI assets remain upstream release assets.
FROM golang:1.27.1-alpine@sha256:8a5910f31396cd4d89662f56c68b3ae31d374308270a1c3bd96672ee5ed43414 AS build
ADD --checksum=sha256:5c5c72ec6b4c4746d523f2ed1994dfec4b94f7457e530ee6fb16cfec158e545e https://codeload.github.com/prometheus/alertmanager/tar.gz/73c6bfe7393929211294c1954f30d8ed78e4d0ad /tmp/source.tar.gz
ADD --checksum=sha256:881fe0f7beb9573f35be47dd233af6220e72c8db72c03bcd1780402eb8dc1307 https://github.com/prometheus/alertmanager/releases/download/v0.34.1/alertmanager-web-ui-0.34.1.tar.gz /tmp/ui.tar.gz
WORKDIR /src
RUN tar -xzf /tmp/source.tar.gz --strip-components=1 -C /src && tar -xzf /tmp/ui.tar.gz -C /src/ui/app
RUN go get google.golang.org/grpc@v1.83.2 && go mod verify
ARG TARGETARCH
RUN CGO_ENABLED=0 GOOS=linux GOARCH=${TARGETARCH} go build -trimpath -ldflags="-s -w -X github.com/prometheus/common/version.Version=0.34.1-wallet-grpc1.83.2 -X github.com/prometheus/common/version.Revision=73c6bfe7393929211294c1954f30d8ed78e4d0ad" -o /out/alertmanager ./cmd/alertmanager && CGO_ENABLED=0 GOOS=linux GOARCH=${TARGETARCH} go build -trimpath -ldflags="-s -w" -o /out/amtool ./cmd/amtool
FROM prom/alertmanager:v0.34.1@sha256:e9733bafb1bdef9b00e25a21f8f99dc26a22224bf16641ad754d1649f4c3357a
COPY --from=build /out/alertmanager /bin/alertmanager
COPY --from=build /out/amtool /bin/amtool
USER 65534:65534
