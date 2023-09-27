#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail
set +o xtrace # Disable tracing to prevent secrets being printed out

dotnet nuget update source 'mongo-dev' --store-password-in-clear-text --username "$GITHUB_USER" --password "$GITHUB_APIKEY"
