#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

MONGODB_URI=${MONGODB_URI:=mongodb://localhost:27017/}

if [ "$DRIVER_VERSION" = "latest" ]; then
  export DRIVER_VERSION="*"
fi

echo "Running EF Core tests for '${BUILD_CONFIGURATION}' configuration"
dotnet clean "./MongoDB.EFCoreProvider.sln"
dotnet test "./MongoDB.EFCoreProvider.sln" -e MONGODB_URI="${MONGODB_URI}" -c "${BUILD_CONFIGURATION}" --results-directory "./artifacts/test-results/${BUILD_CONFIGURATION// /}" --logger "junit;LogFileName=TEST_{assembly}.xml;FailureBodyFormat=Verbose" --logger "console;verbosity=detailed"
