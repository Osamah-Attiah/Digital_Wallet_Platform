#!/bin/sh
set -eu
api_password=$(cat /run/secrets/api_db_password)
worker_password=$(cat /run/secrets/worker_db_password)
migrator_password=$(cat /run/secrets/migrator_db_password)
export api_password worker_password migrator_password
exec docker-entrypoint.sh postgres
