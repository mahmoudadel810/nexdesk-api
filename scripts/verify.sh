#!/usr/bin/env bash
# Runs every check a change must pass before it is merged into main. Needs Docker for the integration tests.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet restore
dotnet build --configuration Release --no-restore
dotnet test tests/NexDesk.UnitTests --configuration Release --no-build
dotnet test tests/NexDesk.IntegrationTests --configuration Release --no-build
dotnet format --verify-no-changes --no-restore

echo "All checks passed."
