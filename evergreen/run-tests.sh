#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

MONGODB_URI=${MONGODB_URI:=mongodb://localhost:27017/}

EFCORE_PROVIDER_PROJECT_PATH="./src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj"
if [ -n "$DRIVER_VERSION" ]
then
  ## Update Driver's package reference if specified
  if [ "$DRIVER_VERSION" = "latest" ]
  then
    echo "Installing the latest version of MongoDB.Driver..."
    dotnet remove "$EFCORE_PROVIDER_PROJECT_PATH" package MongoDB.Driver
    dotnet add "$EFCORE_PROVIDER_PROJECT_PATH" package MongoDB.Driver
  elif [ -n "$DRIVER_VERSION" ]
  then
    echo "Installing the $DRIVER_VERSION version of MongoDB.Driver..."
    dotnet remove "$EFCORE_PROVIDER_PROJECT_PATH" package MongoDB.Driver
    dotnet add "$EFCORE_PROVIDER_PROJECT_PATH" package MongoDB.Driver -v "$DRIVER_VERSION"
  fi
fi

dotnet clean "./MongoDB.EFCoreProvider.sln"


dotnet test "./MongoDB.EFCoreProvider.sln" -e MONGODB_URI="${MONGODB_URI}" --runtime "${TARGET_RUNTIME}" --results-directory ./artifacts/test-results --logger "junit;LogFileName=TEST_{assembly}.xml;FailureBodyFormat=Verbose" --logger "console;verbosity=detailed"
