#!/bin/sh
set -eu
umask 077
worker_password=$(cat /run/secrets/rabbit_password)
api_password=$(cat /run/secrets/rabbit_api_password)
printf '{"users":[{"name":"wallet_worker","password":"%s","tags":""},{"name":"wallet_observer","password":"%s","tags":""}],"vhosts":[{"name":"/"}],"permissions":[{"user":"wallet_worker","vhost":"/","configure":"^digitalwallet[.]v1[.].*","write":"^digitalwallet[.]v1[.].*|^$","read":"^digitalwallet[.]v1[.].*"},{"user":"wallet_observer","vhost":"/","configure":"","write":"","read":"^digitalwallet[.]v1[.].*"}]}' "$worker_password" "$api_password" > /tmp/definitions.json
printf 'default_user = wallet_worker\ndefault_pass = %s\nlisteners.tcp.default = 5672\ndefinitions.import_backend = local_filesystem\ndefinitions.local.path = /tmp/definitions.json\n' "$worker_password" > /tmp/rabbitmq.conf
unset worker_password api_password
export RABBITMQ_CONFIG_FILE=/tmp/rabbitmq
exec rabbitmq-server
