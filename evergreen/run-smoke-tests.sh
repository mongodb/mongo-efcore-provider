#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

MONGODB_URI=${MONGODB_URI:=mongodb://localhost:27017/}

EFCORE_PROVIDER_PROJECT="./src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj"
TESTS_PROJECT="./tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj"

BUILD_CONFIGURATION=""
if [[ "${PACKAGE_VERSION}" == "8."* ]]; then
    BUILD_CONFIGURATION="Debug EF8"
fi
if [[ "${PACKAGE_VERSION}" == "9."* ]]; then
    BUILD_CONFIGURATION="Debug EF9"
fi
if [[ "${PACKAGE_VERSION}" == "10."* ]]; then
    BUILD_CONFIGURATION="Debug EF10"
fi

# Check if BUILD_CONFIGURATION is set
if [ -z "$BUILD_CONFIGURATION" ]; then
  echo "Error: Unknown PACKAGE_VERSION $PACKAGE_VERSION found. Update get-build-release-config.sh to handle this version."
  exit 1
fi

echo Retargeting API tests to use generated package instead of project dependency...
dotnet nuget add source "./artifacts/nuget" -n local --configfile "./nuget.config"
dotnet nuget locals temp -c
dotnet clean "./MongoDB.EFCoreProvider.sln"

echo "Configuring test project: $TESTS_PROJECT"
dotnet remove "$TESTS_PROJECT" reference "$EFCORE_PROVIDER_PROJECT"
dotnet add "$TESTS_PROJECT" package "MongoDB.EntityFrameworkCore" -v "$PACKAGE_VERSION"

echo "Run tests: $TESTS_PROJECT"
dotnet test "$TESTS_PROJECT" -c "$BUILD_CONFIGURATION" -e MONGODB__URI="${MONGODB_URI}" --results-directory "./artifacts/test-results" --logger "junit;LogFileName=TEST_{assembly}.xml;FailureBodyFormat=Verbose" --logger "console;verbosity=detailed"

echo "Revert changes for test project: $TESTS_PROJECT"
dotnet remove "$TESTS_PROJECT" package "MongoDB.EntityFrameworkCore"
dotnet add "$TESTS_PROJECT" reference "$EFCORE_PROVIDER_PROJECT"

dotnet nuget remove source local --configfile "./nuget.config"
