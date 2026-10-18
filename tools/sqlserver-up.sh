#!/usr/bin/env bash
# Start a DISPOSABLE SQL Server (Developer edition, for development and test only) in a container
# and write its connection string to an env file. Used by developers and by CI.
#
# Usage: tools/sqlserver-up.sh [--name ecp-sql] [--port 14330] [--image mcr.microsoft.com/mssql/server:2022-latest] [--env-file .ecp-dev/test-sql.env] [--backup-dir DIR] [--run-as-root]
#
# --backup-dir DIR  shares DIR with the server (as /ecp-backups) so backup and restore tests can read the .bak files the server writes. The env file then also carries
#                   ECP_TEST_SQL_BACKUP_DIR (the host path) and ECP_TEST_SQL_BACKUP_DIR_SERVER (the path the server sees). Without it those tests are NOT VERIFIED.
# --run-as-root     runs the server process as root inside the container (CI uses it together with a default ACL so the runner user can read what the server writes).
#
# The sa password is generated at run time and written only to the env file (git-ignored).
# Running the container accepts the SQL Server licence terms for the Developer edition; the
# operator is responsible for that acceptance. Never use this for production data.
set -euo pipefail
NAME=ecp-sql; PORT=14330; IMAGE=mcr.microsoft.com/mssql/server:2022-latest; ENV_FILE=.ecp-dev/test-sql.env; BACKUP_DIR=''; USER_ARGS=()
while [[ $# -gt 0 ]]; do case "$1" in
  --name) NAME=$2; shift 2;; --port) PORT=$2; shift 2;; --image) IMAGE=$2; shift 2;; --env-file) ENV_FILE=$2; shift 2;; --backup-dir) BACKUP_DIR=$2; shift 2;; --run-as-root) USER_ARGS=(--user root); shift;;
  *) echo "unknown argument $1" >&2; exit 2;; esac; done

command -v docker >/dev/null || { echo "docker CLI not found" >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo "docker daemon not reachable" >&2; exit 1; }

if docker ps --format '{{.Names}}' | grep -qx "$NAME" && [[ -f "$ENV_FILE" ]]; then
  echo "SQL Server container '$NAME' already running; reusing $ENV_FILE"; exit 0
fi
docker rm -f "$NAME" >/dev/null 2>&1 || true
PW="Aa1!$(openssl rand -hex 16)"
# --network host keeps this working where the daemon has no bridge network (some sandboxes).
MOUNT_ARGS=()
if [[ -n "$BACKUP_DIR" ]]; then mkdir -p "$BACKUP_DIR"; BACKUP_DIR=$(cd "$BACKUP_DIR" && pwd); chmod 0777 "$BACKUP_DIR"; MOUNT_ARGS=(-v "$BACKUP_DIR:/ecp-backups"); fi
docker run -d --name "$NAME" --network host "${USER_ARGS[@]}" "${MOUNT_ARGS[@]}" \
  -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e MSSQL_SA_PASSWORD="$PW" -e MSSQL_TCP_PORT="$PORT" "$IMAGE" >/dev/null

echo "waiting for SQL Server on port $PORT ..."
for i in $(seq 1 60); do
  if docker exec "$NAME" /opt/mssql-tools18/bin/sqlcmd -C -S "localhost,$PORT" -U sa -P "$PW" -Q "SELECT 1" >/dev/null 2>&1; then
    mkdir -p "$(dirname "$ENV_FILE")"; umask 077
    printf "ECP_TEST_SQL='Server=127.0.0.1,%s;User Id=sa;Password=%s;TrustServerCertificate=True;Encrypt=True'\n" "$PORT" "$PW" > "$ENV_FILE"
    if [[ -n "$BACKUP_DIR" ]]; then printf "ECP_TEST_SQL_BACKUP_DIR='%s'\nECP_TEST_SQL_BACKUP_DIR_SERVER='/ecp-backups'\n" "$BACKUP_DIR" >> "$ENV_FILE"; fi
    echo "ready. Connection string written to $ENV_FILE (git-ignored)."; exit 0
  fi
  sleep 2
done
echo "SQL Server did not become ready in time" >&2; docker logs --tail 20 "$NAME" >&2 || true; exit 1
