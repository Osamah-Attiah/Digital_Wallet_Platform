# Keep the upstream server and signed Prometheus plugin. Remove unused vulnerable data sources.
FROM alpine:3.24.2@sha256:294b683cb724975bec92580e1e685676bd4b50bda910ddb8c51d4cabeaec77e6 AS plugins
ADD --checksum=sha256:8bdd6583e398f84d497dabec0287563b9eee471d711dea08a21482db5cd5a294 https://github.com/grafana/grafana-prometheus-datasource/releases/download/v13.2.3/prometheus-13.2.3.linux_amd64.zip /tmp/amd64.zip
ADD --checksum=sha256:a49bb6a6fe6e79578dc78841ce7f437a50d294a0fdb74bf25214cd6ce75d85db https://github.com/grafana/grafana-prometheus-datasource/releases/download/v13.2.3/prometheus-13.2.3.linux_arm64.zip /tmp/arm64.zip
ARG TARGETARCH
RUN unzip -q /tmp/${TARGETARCH}.zip -d /out && chmod +x /out/prometheus/gpx_grafana-prometheus-datasource_linux_${TARGETARCH}
FROM grafana/grafana:13.2.3@sha256:b28bae15e219c998fb0e0424ed724930cc61b1f61fb404d47c862f9a23f9e572
USER 0:0
RUN rm -rf /usr/share/grafana/data/plugins-bundled/grafana-postgresql-datasource /usr/share/grafana/data/plugins-bundled/postgres /usr/share/grafana/data/plugins-bundled/influxdb /usr/share/grafana/data/plugins-bundled/jaeger /usr/share/grafana/data/plugins-bundled/prometheus /usr/share/grafana/data/plugins-bundled/stackdriver /usr/share/grafana/data/plugins-bundled/tempo
COPY --from=plugins --chown=472:0 /out/prometheus /usr/share/grafana/data/plugins-bundled/prometheus
USER 472:0
