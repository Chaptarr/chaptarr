#!/usr/bin/env bash
set -euo pipefail

if [[ ! "${EXPECTED_RELEASE_SHA:-}" =~ ^[0-9a-f]{40}$ ]]; then
  echo "The validated release source is not a full commit SHA."
  exit 1
fi

checked_out_sha="$(git rev-parse HEAD)"
if [[ "$checked_out_sha" != "$EXPECTED_RELEASE_SHA" ]]; then
  echo "main advanced from $EXPECTED_RELEASE_SHA to $checked_out_sha after release validation."
  exit 1
fi
