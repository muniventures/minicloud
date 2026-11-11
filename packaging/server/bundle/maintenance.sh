#!/bin/sh
# Run only from an independently verified exact release; the installer repeats trust checks.
set -eu
exec sh "$(dirname "$0")/install-server.sh" maintenance "$@"
