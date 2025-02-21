#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

MONGODB_URI=${MONGODB_URI:=mongodb://localhost:27017/}
BUILD_CONFIGURATION=${BUILD_CONFIGURATION:=Debug}

EFCORE_PROVIDER_PROJECT_PATH="./src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj"
if [ -n "$DRIVER_VERSION" ]
then
  ## Update Driver's package reference if specified
  if [ "$DRIVER_VERSION" = "latest" ]
  then
    echo "Installing the latest version of MongoDB.Driver..."
    dotnet remove "$EFCORE_PROVIDER_PROJECT_PATH" package MongoDB.Driver
    dotnet add "$EFCORE_PROVIDER_PROJECT_PATH" package MongoDB.Driver
  else
    echo "Installing the $DRIVER_VERSION version of MongoDB.Driver..."
    dotnet remove "$EFCORE_PROVIDER_PROJECT_PATH" package MongoDB.Driver
    dotnet add "$EFCORE_PROVIDER_PROJECT_PATH" package MongoDB.Driver -v "$DRIVER_VERSION"
  fi
fi

echo "Running EF Core tests for '${BUILD_CONFIGURATION}' configuration"
dotnet clean "./MongoDB.EFCoreProvider.sln"
dotnet test "./MongoDB.EFCoreProvider.sln" -e MONGODB_URI="${MONGODB_URI}" -c "${BUILD_CONFIGURATION}" --results-directory ./artifacts/test-results/${BUILD_CONFIGURATION// /} --logger "junit;LogFileName=TEST_{assembly}.xml;FailureBodyFormat=Verbose" --logger "console;verbosity=detailed"
