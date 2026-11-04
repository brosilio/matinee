#!/usr/bin/env bash
set -euo pipefail

repo_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
work_dir=$(mktemp -d /tmp/matinee-ci.XXXXXX)
trap 'rm -rf -- "$work_dir"' EXIT

cd "$repo_dir"

# Keep every referenced project's bin/obj output out of the sibling httpgrl checkout.
dotnet build matinee.slnx -c Release -m:1 --artifacts-path "$work_dir/build"
dotnet "$work_dir/build/bin/Matinee.Tests/release/Matinee.Tests.dll"

dotnet publish tests/Matinee.Tests/Matinee.Tests.csproj -c Release -m:1 \
  -p:PublishAot=true --artifacts-path "$work_dir/tests-build" -o "$work_dir/tests"
"$work_dir/tests/Matinee.Tests"

dotnet publish src/Matinee/Matinee.csproj -c Release -m:1 \
  --artifacts-path "$work_dir/server-build" -o "$work_dir/server"
"$work_dir/server/matinee" -t -c conf/matinee.conf

if command -v node >/dev/null 2>&1; then
  node --check web/js/player.js
  node --check web/js/conn.js
  node --check web/js/chat.js
  node --check web/js/main.js
  node --experimental-default-type=module tests/web/player-smoke.mjs
fi

echo "matinee CI: PASS"
