#!/bin/sh
# Chooses the certificate nginx serves (STORY-046, DEC-050). Runs from the nginx image's
# /docker-entrypoint.d before nginx starts.
# - /etc/nginx/tls/current links to the Let's Encrypt certificate (written by the certbot service
#   into the shared, read-only mounted volume) once it exists, else to a self-signed one for
#   ${PUBLIC_HOSTNAME} generated here, so nginx can start before the first certificate is issued.
# - A watcher checks every minute and reloads nginx when the certificate appears or is renewed
#   (certbot points live/ at a new archive file on each renewal).
set -eu

live=/etc/letsencrypt/live/chog
fallback=/etc/nginx/tls/self-signed
current=/etc/nginx/tls/current

if [ ! -s "$fallback/privkey.pem" ]; then
  mkdir -p "$fallback"
  openssl req -x509 -newkey rsa:2048 -nodes -days 365 -subj "/CN=${PUBLIC_HOSTNAME}" \
    -addext "subjectAltName=DNS:${PUBLIC_HOSTNAME}" \
    -keyout "$fallback/privkey.pem" -out "$fallback/fullchain.pem" 2>/dev/null
  chmod 600 "$fallback/privkey.pem"
fi

# What nginx should serve: the issued certificate's current archive file, or the fallback.
chosen() {
  if [ -s "$live/fullchain.pem" ] && [ -s "$live/privkey.pem" ]; then
    readlink -f "$live/fullchain.pem"
  else
    echo self-signed
  fi
}

point() {
  if [ "$1" = self-signed ]; then ln -sfn "$fallback" "$current"; else ln -sfn "$live" "$current"; fi
}

state=$(chosen)
point "$state"
echo "40-chog-tls.sh: serving the ${state} certificate"

(
  while sleep "${TLS_CHECK_SECONDS:-60}"; do
    next=$(chosen)
    if [ "$next" != "$state" ]; then
      point "$next"
      if nginx -s reload; then
        echo "40-chog-tls.sh: now serving the ${next} certificate"
        state=$next
      fi
    fi
  done
) &
