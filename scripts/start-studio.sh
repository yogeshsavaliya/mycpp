#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "The .NET SDK is not installed. Install .NET SDK 9 from https://dot.net and run this script again."
  exit 1
fi
if ! command -v node >/dev/null 2>&1 || ! command -v npm >/dev/null 2>&1; then
  echo "Node.js and npm are required to build the web interface. Install Node.js 22 or later, then run this script again."
  exit 1
fi
if ! command -v curl >/dev/null 2>&1; then
  echo "curl is required so this script can wait until the server answers. Install curl and run this script again."
  exit 1
fi

echo "Building the web interface..."
(cd "$root/web/studio" && npm install && npm run build)

echo "Starting IL2CPP Recovery Studio..."
dotnet run --project "$root/src/Il2CppRecover.Web/Il2CppRecover.Web.csproj" &
server=$!
cleanup() {
  kill "$server" >/dev/null 2>&1 || true
}
trap cleanup INT TERM

url="http://127.0.0.1:5088"
ready=0
for _ in $(seq 1 90); do
  if curl -fsS "$url/api/health" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done

if [[ "$ready" -ne 1 ]]; then
  echo "The server did not become ready. Read the messages above, then press Ctrl+C."
  wait "$server"
  exit 1
fi

echo "IL2CPP Recovery Studio is ready at $url"
echo "Press Ctrl+C in this window to stop. Projects stay on this computer."
if command -v xdg-open >/dev/null 2>&1; then
  xdg-open "$url" >/dev/null 2>&1 || true
elif command -v open >/dev/null 2>&1; then
  open "$url" >/dev/null 2>&1 || true
fi
wait "$server"
