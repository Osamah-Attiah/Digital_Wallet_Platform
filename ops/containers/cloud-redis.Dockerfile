FROM redis:7.4-alpine@sha256:858f009f9709ce576febc734aa78b8f6d624b82571f9ddb6bda4377c833b3499
USER root
RUN apk add --no-cache --upgrade libssl3=3.3.7-r2 libcrypto3=3.3.7-r2
USER 999:999
