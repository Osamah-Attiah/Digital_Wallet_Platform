FROM redis:8.10-alpine@sha256:3811787313eba226a2ef38658c6ccb91cd5e110edc89c37767de373120a0e5a0
USER root
RUN apk add --no-cache --upgrade libssl3=3.3.7-r2 libcrypto3=3.3.7-r2
USER 999:999
