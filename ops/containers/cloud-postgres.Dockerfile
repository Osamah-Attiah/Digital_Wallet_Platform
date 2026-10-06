FROM postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24
# The deployment runs as UID 70; root-only gosu is never executed by its entrypoint.
RUN rm -f /usr/local/bin/gosu /usr/bin/gosu
USER 70:70
