#!/bin/sh
# Backup retention (STORY-047, DEC-051). Reads archive names on stdin, one per line, and prints the
# ones to delete: everything except the newest KEEP_DAILY archives and the newest archive of each of
# the KEEP_WEEKLY most recent weeks (weeks start on Monday, UTC). Names that aren't backup archives
# (<name>-YYYYMMDDTHHMMSSZ.dump.age) are never printed, so nothing else in the folder is touched.
# Usage: backup-retention.sh [KEEP_DAILY] [KEEP_WEEKLY]   (defaults 7 and 4)
set -eu

daily=${1:-7}
weekly=${2:-4}
case "$daily$weekly" in *[!0-9]*) echo "backup-retention.sh: counts must be whole numbers" >&2; exit 2 ;; esac

# Newest first by the timestamp in the name, then keep or print each one.
awk '
  match($0, /-[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]T[0-9][0-9][0-9][0-9][0-9][0-9]Z\.dump\.age$/) { print substr($0, RSTART + 1, 16) " " $0 }
' | sort -r | awk -v daily="$daily" -v weekly="$weekly" '
  # Days since a fixed epoch (proleptic Gregorian); only differences matter.
  function days(y, m, d) {
    if (m <= 2) { y--; m += 12 }
    return 365 * y + int(y / 4) - int(y / 100) + int(y / 400) + int((153 * (m - 3) + 2) / 5) + d
  }
  BEGIN { monday = days(2001, 1, 1) }  # 2001-01-01 was a Monday
  {
    stamp = $1
    name = substr($0, length(stamp) + 2)
    week = int((days(substr(stamp, 1, 4) + 0, substr(stamp, 5, 2) + 0, substr(stamp, 7, 2) + 0) - monday) / 7)
    keep = 0
    if (++count <= daily) keep = 1
    if (!(week in seen) && weeks < weekly) { seen[week] = 1; weeks++; keep = 1 }
    if (!keep) print name
  }
'
