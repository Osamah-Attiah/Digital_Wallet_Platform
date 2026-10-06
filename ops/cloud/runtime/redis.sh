#!/bin/sh
set -eu
umask 077
printf 'user default off\nuser wallet_cache on >%s ~digitalwallet:* +@read +@write +@connection +@scripting -flushall -flushdb -config -acl -shutdown -keys -scan -randomkey\n' "$(cat /run/secrets/redis_password)" > /tmp/users.acl
exec redis-server --port 0 --tls-port 6380 --tls-cert-file /run/tls/redis.crt --tls-key-file /run/tls/redis.key --tls-ca-cert-file /run/tls/ca.crt --tls-auth-clients no --aclfile /tmp/users.acl --save '' --appendonly no --maxmemory 128mb --maxmemory-policy allkeys-lru
