# Backup image (STORY-047, DEC-051). Build context: the repository root.
# The PostgreSQL image of the same version as the database, so pg_dump and pg_restore match the server,
# plus age (encryption) and rclone (the off-VM copy) from Alpine, pinned. Runs as the non-root
# `postgres` user; no secret is baked in.

ARG POSTGRES=postgres:18.6-alpine3.24

FROM ${POSTGRES}
RUN apk add --no-cache age=1.3.2-r1 rclone=1.74.1-r3 \
 && install -d -o postgres -g postgres -m 700 /backups
COPY --chmod=755 infrastructure/production/backup.sh /usr/local/bin/chog-backup
COPY --chmod=755 infrastructure/production/backup-retention.sh /usr/local/bin/backup-retention.sh
# rclone takes its remote from RCLONE_CONFIG_OFFSITE_* variables; there is no config file.
ENV RCLONE_CONFIG=/dev/null
USER postgres
WORKDIR /backups
ENTRYPOINT ["/usr/local/bin/chog-backup"]
CMD ["run"]
