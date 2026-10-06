FROM postgres:18-alpine@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873
# The deployment runs as UID 70; root-only gosu is never executed by its entrypoint.
RUN rm -f /usr/local/bin/gosu /usr/bin/gosu
USER 70:70
