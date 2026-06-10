#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SERVER_DIR="$PROJECT_ROOT/Builds/LinuxServer"
SERVER_EXE="$SERVER_DIR/WoopRiderServer.x86_64"
LOG_DIR="$PROJECT_ROOT/Logs/ServerLogs"
LOG_FILE="$LOG_DIR/wooprider-server-$(date +%Y%m%d-%H%M%S).log"

if [[ ! -f "$SERVER_EXE" ]]; then
  echo "Server executable not found: $SERVER_EXE" >&2
  exit 1
fi

mkdir -p "$LOG_DIR"

# Linux Dedicated Server 실행 스크립트.
# -batchmode/-nographics는 화면 없이 서버 프로세스로 실행하기 위한 Unity 표준 옵션.
chmod +x "$SERVER_EXE"
cd "$SERVER_DIR"
echo "Server log: $LOG_FILE"
./WoopRiderServer.x86_64 \
  -batchmode \
  -nographics \
  -server \
  -port 7777 \
  -roomId test-room-1 \
  -maxPlayers 6 \
  -logFile "$LOG_FILE"
