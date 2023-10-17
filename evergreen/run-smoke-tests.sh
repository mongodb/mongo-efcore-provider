#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

MONGODB_URI=${MONGODB_URI:=mongodb://localhost:27017/}

EFCORE_PROVIDER_PROJECT="./src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj"
TESTS_PROJECT="./tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj"

echo Retargeting API tests to use generated package instead of project dependency...
dotnet nuget add source "./artifacts/nuget" -n local --configfile "./nuget.config"
dotnet nuget locals temp -c
dotnet clean "./MongoDB.EFCoreProvider.sln"

echo "Configuring test project: $TESTS_PROJECT"
dotnet remove "$TESTS_PROJECT" reference "$EFCORE_PROVIDER_PROJECT"
dotnet add "$TESTS_PROJECT" package "MongoDB.EntityFrameworkCore" -v "$PACKAGE_VERSION"

echo "Run tests: $TESTS_PROJECT"
dotnet test "$TESTS_PROJECT" -e MONGODB__URI="${MONGODB_URI}" --runtime "${TARGET_RUNTIME}" --results-directory "./artifacts/test-results" --logger "junit;LogFileName=TEST_{assembly}.xml;FailureBodyFormat=Verbose" --logger "console;verbosity=detailed"

echo "Revert changes for test project: $TESTS_PROJECT"
dotnet remove "$TESTS_PROJECT" package "MongoDB.EntityFrameworkCore"
dotnet add "$TESTS_PROJECT" reference "$EFCORE_PROVIDER_PROJECT"

dotnet nuget remove source local --configfile "./nuget.config"
