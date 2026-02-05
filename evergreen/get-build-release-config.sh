#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

# Determine BUILD_CONFIGURATION based on PACKAGE_VERSION
BUILD_CONFIGURATION=""
if [[ "${PACKAGE_VERSION}" == "8."* ]]; then
    BUILD_CONFIGURATION="Release EF8"
fi
if [[ "${PACKAGE_VERSION}" == "9."* ]]; then
    BUILD_CONFIGURATION="Release EF9"
fi
if [[ "${PACKAGE_VERSION}" == "10."* ]]; then
    BUILD_CONFIGURATION="Release EF10"
fi

# Check if BUILD_CONFIGURATION is set
if [ -z "$BUILD_CONFIGURATION" ]; then
  echo "Error: Unknown PACKAGE_VERSION $PACKAGE_VERSION found. Update get-build-release-config.sh to handle this version."
  exit 1
fi

echo "$BUILD_CONFIGURATION"
