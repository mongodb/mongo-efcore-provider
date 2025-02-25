#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

if [ -n "$triggered_by_git_tag" ]; then
    PACKAGE_VERSION=$triggered_by_git_tag
else
    PACKAGE_VERSION=$(git describe --tags)
fi

PACKAGE_VERSION=$(echo $PACKAGE_VERSION | cut -c 2-)
echo "$PACKAGE_VERSION"
