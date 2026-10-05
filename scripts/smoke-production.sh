#!/usr/bin/env bash
# Smoke test for a running Pulse API in PRODUCTION mode. Usage: scripts/smoke-production.sh <base-url>
#
# Proves the things that must be true of a production server and that unit and integration tests cannot see, because they run the app in
# Development: no Swagger, no database-wiping demo endpoints, no stack traces in errors, protected routes need a login, and the refresh
# token only ever travels in an httpOnly cookie. It only reads, with two exceptions that are safe by design: it POSTs to the demo wipe
# endpoints expecting a 404 (point this at a throwaway database, never at data you care about), and on an EMPTY database it
# bootstraps the first user to check the cookie (that step is skipped when users already exist).
#
# Exit status is non-zero if any check fails.

set -u
BASE="${1:?usage: smoke-production.sh <base-url, e.g. http://localhost:8080>}"
BASE="${BASE%/}"
fail=0

pass() { echo "PASS  $1"; }
bad()  { echo "FAIL  $1"; fail=1; }
http_code() { curl -s -o /dev/null -w '%{http_code}' --max-time 15 "$@"; }
expect_code() { # name expected curl-args...
  local name="$1" expected="$2"; shift 2
  local actual; actual="$(http_code "$@")"
  if [ "$actual" = "$expected" ]; then pass "$name (HTTP $actual)"; else bad "$name: expected HTTP $expected, got $actual"; fi
}

expect_code "liveness /healthz"                   200 "$BASE/healthz"
expect_code "readiness /readyz (Postgres reachable)" 200 "$BASE/readyz"
expect_code "Swagger is not served"               404 "$BASE/swagger/v1/swagger.json"
expect_code "demo reset endpoint is not found"    404 -X POST "$BASE/api/v1/demo/reset"
expect_code "demo clear endpoint is not found"    404 -X POST "$BASE/api/v1/demo/clear"
expect_code "a protected endpoint needs a login"  401 "$BASE/api/v1/tasks"
expect_code "a wrong password is 401, not an error" 401 -X POST -H 'Content-Type: application/json' \
  -d '{"email":"nobody@example.invalid","password":"not-a-real-password-1"}' "$BASE/api/v1/auth/login"

# Failures must not leak internals. A malformed body is a client error, and the response must not carry a stack trace.
malformed="$(curl -s --max-time 15 -X POST -H 'Content-Type: application/json' -d '{' "$BASE/api/v1/auth/login")"
if echo "$malformed" | grep -qE ' at [A-Za-z_.]+\(|\.cs:line [0-9]+|System\.[A-Za-z.]+Exception'; then
  bad "an error response leaks a stack trace"
else
  pass "error responses carry no stack trace"
fi

headers="$(curl -s -D - -o /dev/null --max-time 15 "$BASE/healthz")"
if echo "$headers" | grep -qi '^x-frame-options: *deny'; then pass "security headers are set"; else bad "X-Frame-Options: DENY is missing"; fi

# The refresh cookie. A fresh database has no users, so create the first one; if users exist the bootstrap is refused and this is skipped.
password="$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 20)Aa1!"
email="smoke-$RANDOM$RANDOM@example.invalid"
boot_code="$(http_code -X POST -H 'Content-Type: application/json' \
  -d "{\"name\":\"Smoke Test\",\"email\":\"$email\",\"password\":\"$password\"}" "$BASE/api/v1/auth/bootstrap")"
if [ "$boot_code" = "201" ]; then
  login="$(curl -s -i --max-time 15 -X POST -H 'Content-Type: application/json' \
    -d "{\"email\":\"$email\",\"password\":\"$password\",\"useCookie\":true}" "$BASE/api/v1/auth/login")"
  cookie="$(echo "$login" | tr -d '\r' | grep -i '^set-cookie: *pulse_refresh=' | head -1)"
  if [ -z "$cookie" ]; then
    bad "login did not set the pulse_refresh cookie"
  else
    for attr in 'httponly' 'secure' 'samesite=strict' 'path=/api/v1/auth'; do
      if echo "$cookie" | tr 'A-Z' 'a-z' | grep -q "$attr"; then pass "refresh cookie has $attr"; else bad "refresh cookie is missing $attr"; fi
    done
  fi
  body="$(echo "$login" | tr -d '\r' | sed -n '/^$/,$p' | tr -d '\n')"
  if echo "$body" | grep -qE '"refreshToken" *: *""'; then pass "the refresh token is not in the response body"; else bad "the refresh token appears in the response body"; fi
else
  echo "SKIP  refresh cookie checks (database already has users, bootstrap returned HTTP $boot_code)"
fi

if [ "$fail" -eq 0 ]; then echo "All smoke checks passed."; else echo "One or more smoke checks FAILED."; fi
exit "$fail"
