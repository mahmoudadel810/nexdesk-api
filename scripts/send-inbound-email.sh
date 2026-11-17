#!/usr/bin/env bash
# Simulates a mail gateway delivering an email to a workspace: it becomes a ticket, or a reply when the
# subject contains a ticket reference such as [#42] and the sender is that ticket's requester.
#
# Usage:
#   scripts/send-inbound-email.sh <workspace-slug> <from-address> <subject> <body> [from-name]
#
# Example:
#   scripts/send-inbound-email.sh acme casey@customer.example "Cannot sign in" "I get an error." "Casey"
#   scripts/send-inbound-email.sh acme casey@customer.example "Re: Cannot sign in [#12]" "It works now."
#
# Reads INBOUND_SECRET and API_PORT from .env next to docker-compose.yml.
set -euo pipefail
cd "$(dirname "$0")/.."

if [ "$#" -lt 4 ]; then
  sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
  exit 64
fi

set -a
# shellcheck disable=SC1091
. ./.env
set +a

python3 - "$2" "$3" "$4" "${5:-}" <<'PY' |
import json, sys
sender, subject, text, name = sys.argv[1:5]
print(json.dumps({"from": sender, "fromName": name or None, "subject": subject, "text": text}))
PY
curl --fail-with-body --silent --show-error \
  --request POST "http://localhost:${API_PORT:-8080}/api/v1/inbound/email/$1" \
  --header "Content-Type: application/json" \
  --header "X-Inbound-Secret: ${INBOUND_SECRET}" \
  --data @-
echo
