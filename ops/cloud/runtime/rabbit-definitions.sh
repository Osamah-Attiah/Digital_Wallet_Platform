#!/bin/sh
set -eu
umask 077
worker_password=$(cat /run/secrets/rabbit_password)
api_password=$(cat /run/secrets/rabbit_api_password)
printf '{"users":[{"name":"wallet_worker","password":"%s","tags":""},{"name":"wallet_observer","password":"%s","tags":""}],"vhosts":[{"name":"/"}],"permissions":[{"user":"wallet_worker","vhost":"/","configure":"^digitalwallet[.]v1[.].*","write":"^digitalwallet[.]v1[.].*|^$","read":"^digitalwallet[.]v1[.].*"},{"user":"wallet_observer","vhost":"/","configure":"","write":"","read":"^digitalwallet[.]v1[.].*"}]}' "$worker_password" "$api_password" > /tmp/definitions.json
printf 'listeners.tcp = none\nlisteners.ssl.default = 5671\nssl_options.cacertfile = /run/tls/ca.crt\nssl_options.certfile = /run/tls/rabbitmq.crt\nssl_options.keyfile = /run/tls/rabbitmq.key\nssl_options.verify = verify_none\nssl_options.fail_if_no_peer_cert = false\ndefinitions.import_backend = local_filesystem\ndefinitions.local.path = /tmp/definitions.json\n' > /tmp/rabbitmq.conf
unset worker_password api_password
export RABBITMQ_CONFIG_FILE=/tmp/rabbitmq
exec rabbitmq-server
