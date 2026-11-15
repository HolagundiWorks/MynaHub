#!/usr/bin/env bash
# End-to-end check of the supported first start on a REAL running server and a REAL Postgres (used by .github/workflows/api.yml).
# Expects the API listening on $BASE (default http://127.0.0.1:3000) and the same environment as the server (DATABASE_URL, BETTER_AUTH_SECRET ...).
# Everything here is disposable CI data; never point it at a company's deployment.
set -euo pipefail
BASE="${BASE:-http://127.0.0.1:3000}"
PASSWORD="ci-only-passphrase-12345"

echo "health"
curl -fsS "$BASE/health" >/dev/null
curl -fsS "$BASE/health/ready" >/dev/null

echo "setup token"
TOKEN="$(npm run --silent bootstrap:token | grep -E '^  [A-Za-z0-9_-]{40,}$' | tr -d ' ')"
test -n "$TOKEN"

echo "bootstrap"
curl -fsS -X POST "$BASE/v1/bootstrap" -H 'content-type: application/json' \
  -d "{\"setupToken\":\"$TOKEN\",\"organizationName\":\"CI Company\",\"slug\":\"ci-company\",\"adminName\":\"CI Owner\",\"adminEmail\":\"owner@ci.example\",\"adminPassword\":\"$PASSWORD\"}" >/dev/null

echo "sign in"
BEARER="$(curl -sS -D - -o /dev/null -X POST "$BASE/api/auth/sign-in/email" -H 'content-type: application/json' \
  -d "{\"email\":\"owner@ci.example\",\"password\":\"$PASSWORD\"}" | grep -i '^set-auth-token:' | cut -d' ' -f2 | tr -d '\r')"
test -n "$BEARER"

echo "who am I"
curl -fsS "$BASE/v1/me" -H "authorization: Bearer $BEARER" | grep -q '"Owner"'

echo "public sign-up is off"
STATUS="$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$BASE/api/auth/sign-up/email" -H 'content-type: application/json' \
  -d '{"email":"stranger@ci.example","password":"a long enough passphrase","name":"Stranger"}')"
test "$STATUS" -ge 400

echo "bootstrap is closed for good"
if npm run --silent bootstrap:token >/dev/null 2>&1; then
  echo "FAIL: a second setup token was issued" >&2
  exit 1
fi
echo "OK"
