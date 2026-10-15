#!/bin/sh
# Encrypted PostgreSQL backups and restore (STORY-047, DEC-051). Runs in the `backup` service.
#   chog-backup run              every day at BACKUP_TIME (UTC): back up, retrying hourly after a failure
#   chog-backup now              one backup now
#   chog-backup list             the archives on the VM and off-VM
#   chog-backup restore <name>   restore an archive (or `latest`) into an empty database; the age
#                                identity (private key) is read from standard input, never from a file
# A backup is `pg_dump --format=custom` of PGDATABASE only, encrypted on the fly with `age` to
# BACKUP_AGE_RECIPIENT (a public key; the private key is never on the VM), so the plain dump never
# touches disk. It is written to /backups, copied with rclone to BACKUP_S3_BUCKET/BACKUP_S3_PREFIX on the
# `offsite` remote (configured by RCLONE_CONFIG_OFFSITE_*), and then old archives are pruned in both
# places (backup-retention.sh). The archive holds database rows only: no .env, no PostgreSQL role
# passwords, no certificates, no keys.
set -eu -o pipefail
umask 077

backups=/backups
retention=/usr/local/bin/backup-retention.sh
# The health check: present while the scheduler runs with settings that work.
ready=/tmp/chog-backup-ready
if [ -n "${BACKUP_S3_BUCKET:-}" ]; then remote="offsite:$BACKUP_S3_BUCKET/${BACKUP_S3_PREFIX:-chog-game-arena}"; else remote=; fi
prefix=${PGDATABASE:?PGDATABASE must be set}

log() { echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) chog-backup: $*"; }
fail() { log "ERROR: $*" >&2; exit 1; }

require_remote() {
  [ -n "$remote" ] || fail "BACKUP_S3_BUCKET is not set: backups need an off-VM copy (see docs/deployment.md)"
}

require_recipient() {
  [ -n "${BACKUP_AGE_RECIPIENT:-}" ] || fail "BACKUP_AGE_RECIPIENT is not set: backups are always encrypted (see docs/deployment.md)"
  age --encrypt --recipient "$BACKUP_AGE_RECIPIENT" </dev/null >/dev/null || fail "BACKUP_AGE_RECIPIENT is not a valid age public key"
}

archives_local() { ls -1 "$backups" 2>/dev/null | grep -E '\.dump\.age$' || true; }
archives_remote() { rclone lsf --files-only "$remote" 2>/dev/null | grep -E '\.dump\.age$' || true; }

prune() {
  for name in $(archives_local | "$retention" "${BACKUP_KEEP_DAILY:-7}" "${BACKUP_KEEP_WEEKLY:-4}"); do
    rm -f "$backups/$name" && log "pruned $name on the VM"
  done
  for name in $(archives_remote | "$retention" "${BACKUP_KEEP_DAILY:-7}" "${BACKUP_KEEP_WEEKLY:-4}"); do
    rclone deletefile "$remote/$name" && log "pruned $name off-VM"
  done
}

backup() {
  require_recipient
  require_remote
  name="$prefix-$(date -u +%Y%m%dT%H%M%SZ).dump.age"
  partial="$backups/.$name.partial"
  trap 'rm -f "$partial"' EXIT
  log "backing up database $PGDATABASE to $name"
  pg_dump --format=custom --compress=6 --no-password "$PGDATABASE" | age --encrypt --recipient "$BACKUP_AGE_RECIPIENT" --output "$partial" \
    || fail "pg_dump or age failed; nothing was kept"
  [ "$(head -c 21 "$partial")" = "age-encryption.org/v1" ] || fail "the archive is not age-encrypted; nothing was kept"
  mv "$partial" "$backups/$name"
  trap - EXIT
  log "wrote $name ($(wc -c <"$backups/$name") bytes)"
  rclone copyto "$backups/$name" "$remote/$name" || fail "copying $name off-VM failed; it is kept on the VM and nothing was pruned"
  log "copied $name off-VM"
  prune
}

# Seconds until the next BACKUP_TIME (HH:MM, UTC); a full day when it is exactly now.
seconds_until() {
  hours=${1%%:*}
  minutes=${1#*:}
  now=$(date -u +%s)
  wait=$(( (${hours#0} * 3600 + ${minutes#0} * 60 - now % 86400 + 86400) % 86400 ))
  [ "$wait" -gt 0 ] || wait=86400
  echo "$wait"
}

run() {
  at=${BACKUP_TIME:-03:00}
  case "$at" in
    [01][0-9]:[0-5][0-9] | 2[0-3]:[0-5][0-9]) ;;
    *) fail "BACKUP_TIME must be HH:MM (UTC), not '$at'" ;;
  esac
  rm -f "$ready"
  require_recipient
  require_remote
  # Catch a wrong endpoint or key when the stack starts, not at the first backup.
  quick="--retries 1 --low-level-retries 1 --contimeout 10s --timeout 20s"
  { rclone mkdir $quick "$remote" && rclone lsf $quick --max-depth 1 "$remote" >/dev/null; } || fail "cannot reach the off-VM bucket $remote"
  touch "$ready"
  trap exit TERM INT
  wait_seconds=$(seconds_until "$at")
  while :; do
    log "next backup in ${wait_seconds}s (daily at $at UTC)"
    sleep "$wait_seconds" & wait $!
    if (backup); then
      wait_seconds=$(seconds_until "$at")
    else
      wait_seconds=${BACKUP_RETRY_SECONDS:-3600}
      log "backup failed; retrying in ${wait_seconds}s"
    fi
  done
}

list() {
  echo "On the VM ($backups):"
  archives_local | sort | sed 's/^/  /'
  if [ -n "$remote" ]; then
    echo "Off-VM ($remote):"
    rclone lsf --files-only "$remote" | grep -E '\.dump\.age$' | sort | sed 's/^/  /' || fail "cannot list $remote"
  fi
}

# Tables, views and sequences outside the system schemas; an empty database has none.
user_relations() {
  psql --no-password --tuples-only --no-align --dbname="$PGDATABASE" --command \
    "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_%'"
}

restore() {
  wanted=${1:-}
  [ -n "$wanted" ] || fail "usage: chog-backup restore <archive name | latest>  (age identity on standard input)"
  case "$wanted" in latest | *.dump.age) ;; *) fail "'$wanted' is not a backup archive name" ;; esac
  case "$wanted" in */* | .*) fail "'$wanted' is not a backup archive name" ;; esac

  relations=$(user_relations) || fail "cannot reach database $PGDATABASE"
  [ "$relations" = 0 ] || fail "database $PGDATABASE is not empty ($relations relations); restore only into a clean database (see docs/deployment.md)"

  work=$(mktemp -d)
  trap 'rm -rf "$work"' EXIT
  cat >"$work/identity"
  grep -q '^AGE-SECRET-KEY-' "$work/identity" || fail "no age identity on standard input (pipe the private key file in)"

  if [ "$wanted" = latest ]; then
    wanted=$({ archives_local; [ -z "$remote" ] || archives_remote; } | awk '{ print substr($0, length($0) - 24) " " $0 }' | sort -r | head -n 1 | cut -d' ' -f2)
    [ -n "$wanted" ] || fail "no backup archives found on the VM or off-VM"
  fi
  if [ -s "$backups/$wanted" ]; then
    log "restoring $wanted from the VM"
    cp "$backups/$wanted" "$work/archive"
  else
    [ -n "$remote" ] || fail "$wanted is not on the VM and no off-VM remote is configured"
    log "restoring $wanted from off-VM"
    rclone copyto "$remote/$wanted" "$work/archive" || fail "cannot fetch $wanted from $remote"
  fi

  # age authenticates every chunk: a wrong key or a changed archive fails here, before the database.
  age --decrypt --identity "$work/identity" --output "$work/dump" "$work/archive" \
    || fail "cannot decrypt $wanted (wrong key or damaged archive); the database was not changed"
  rm -f "$work/identity"
  pg_restore --list "$work/dump" >/dev/null || fail "$wanted is not a valid dump; the database was not changed"
  pg_restore --no-password --dbname="$PGDATABASE" --no-owner --no-privileges --single-transaction --exit-on-error "$work/dump" \
    || fail "restoring $wanted failed and was rolled back; the database was not changed"
  log "restored $wanted into database $PGDATABASE ($(user_relations) relations)"
}

command=${1:-run}
[ $# -eq 0 ] || shift
case "$command" in
  run) run ;;
  now) backup ;;
  list) list ;;
  restore) restore "$@" ;;
  *) fail "unknown command '$command' (run, now, list or restore <name | latest>)" ;;
esac
