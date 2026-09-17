#!/bin/sh
set -eu

: "${MONGO_URL:?MONGO_URL is required}"
: "${BACKUP_DIR:=/backups}"
: "${BACKUP_RETENTION_DAYS:=14}"
: "${TZ:=Europe/Amsterdam}"

case "$BACKUP_RETENTION_DAYS" in
  ''|*[!0-9]*) echo 'BACKUP_RETENTION_DAYS must be a positive whole number' >&2; exit 2 ;;
esac
if [ "$BACKUP_RETENTION_DAYS" -lt 1 ]; then
  echo 'BACKUP_RETENTION_DAYS must be a positive whole number' >&2
  exit 2
fi

run_backup() {
  today="$(date +%F)"
  archive="huishoudplanner-$(date +%Y%m%d).archive.gz"
  mkdir -p "$BACKUP_DIR"
  mongodump --uri="$MONGO_URL" --archive="$BACKUP_DIR/$archive" --gzip

  for path in "$BACKUP_DIR"/huishoudplanner-????????.archive.gz; do
    [ -f "$path" ] || continue
    name="${path##*/}"
    day="${name#huishoudplanner-}"
    day="${day%.archive.gz}"
    archive_date="$(printf '%s\n' "$day" | sed 's/^\\(....\\)\\(..\\)\\(..\\)$/\\1-\\2-\\3/')"
    expires="$(date -d "$archive_date + $BACKUP_RETENTION_DAYS days" +%F 2>/dev/null || true)"
    if [ -n "$expires" ] && [ "$expires" \< "$today" ]; then
      rm -f "$path"
      echo "deleted $name"
    fi
  done
  echo "backup completed: $archive"
}

seconds_until_next_backup() {
  now="$(date +%s)"
  target="$(date -d "$(date +%F) 03:30" +%s)"
  if [ "$now" -ge "$target" ]; then
    target="$(date -d 'tomorrow 03:30' +%s)"
  fi
  echo $((target - now))
}

case "${1:-schedule}" in
  once) run_backup ;;
  schedule)
    while :; do
      sleep "$(seconds_until_next_backup)"
      run_backup || echo 'backup failed; will retry at the next scheduled run' >&2
    done
    ;;
  *) echo 'usage: huishoudplanner-backup [once|schedule]' >&2; exit 2 ;;
esac
