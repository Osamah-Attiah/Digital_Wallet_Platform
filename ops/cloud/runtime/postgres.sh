#!/bin/sh
set -eu
export api_password="$(cat /run/secrets/api_db_password)" worker_password="$(cat /run/secrets/worker_db_password)" migrator_password="$(cat /run/secrets/migrator_db_password)"
exec docker-entrypoint.sh postgres -c ssl=on -c ssl_cert_file=/run/tls/postgres.crt -c ssl_key_file=/run/tls/postgres.key -c hba_file=/run-config/pg_hba.conf -c password_encryption=scram-sha-256 -c ssl_min_protocol_version=TLSv1.2
