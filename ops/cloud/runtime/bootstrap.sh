#!/bin/sh
# Run only on the approved dedicated VM. No local invocation during Cloud Readiness.
set -eu
[ "${CLOUD_EXECUTION_APPROVED:-}" = "true" ] || { echo 'Cloud execution requires explicit user approval.' >&2; exit 1; }
[ "$(id -u)" = 0 ] || exit 1
apt-get update -qq
apt-get install -y docker.io docker-compose-v2 ca-certificates python3 curl
systemctl enable --now docker
# Host obtains metadata credentials; containers must never reach instance identity or secret APIs.
iptables -C DOCKER-USER -d 169.254.169.254/32 -j REJECT 2>/dev/null || iptables -I DOCKER-USER -d 169.254.169.254/32 -j REJECT
# Persist this rule through host and Docker restarts with a unit, then verify from a container.
cat >/etc/systemd/system/wallet-metadata-block.service <<'UNIT'
[Unit]
After=docker.service
Requires=docker.service
PartOf=docker.service
[Service]
Type=oneshot
ExecStart=/bin/sh -c '/usr/sbin/iptables -C DOCKER-USER -d 169.254.169.254/32 -j REJECT || /usr/sbin/iptables -I DOCKER-USER -d 169.254.169.254/32 -j REJECT'
RemainAfterExit=yes
[Install]
WantedBy=docker.service
UNIT
systemctl daemon-reload
systemctl enable --now wallet-metadata-block.service
# Dedicated data disk: never format an existing filesystem or guess a block device.
disk=/dev/disk/by-id/google-wallet-data
[ -b "$disk" ] || exit 1
if ! blkid "$disk" >/dev/null 2>&1; then mkfs.ext4 "$disk"; fi
mkdir -p /srv/wallet/data /etc/wallet
uuid=$(blkid -s UUID -o value "$disk")
grep -q "$uuid" /etc/fstab || printf 'UUID=%s /srv/wallet/data ext4 defaults,nosuid,nodev 0 2\n' "$uuid" >>/etc/fstab
mountpoint -q /srv/wallet/data || mount /srv/wallet/data
for entry in postgres-data:70 rabbit-data:999 report-files:1654 prometheus-data:65534 grafana-data:472; do
 name=${entry%:*};uid=${entry#*:};install -d -m 0700 -o "$uid" -g "$uid" "/srv/wallet/data/$name"
done
# /run is tmpfs. Populate /etc/wallet/secret-versions.json separately through the approved operator.
# Re-run fetch-secrets on every host boot BEFORE starting the approved release, via wallet-demo.service.
echo 'Host prepared. Secret versions, TLS trust, release signatures and restart unit still require the reviewed deployment steps.'
