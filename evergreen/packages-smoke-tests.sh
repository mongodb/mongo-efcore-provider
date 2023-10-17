#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

MONGODB_URI=${MONGODB_URI:=mongodb://localhost:27017/}

EFCORE_PROVIDER_PROJECT="./src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj"
TESTS_PROJECTS=(
    "./tests/MongoDB.EntityFrameworkCore.UnitTests/MongoDB.EntityFrameworkCore.UnitTests.csproj"
    "./tests/MongoDB.EntityFrameworkCore.FunctionalTests/MongoDB.EntityFrameworkCore.FunctionalTests.csproj"
    )

echo Retargeting API tests to use generated package instead of project dependency...
dotnet clean "./MongoDB.EFCoreProvider.sln"

for TEST_PROJECT in "${TESTS_PROJECTS[@]}"; do
  echo "Configuring test project: $TEST_PROJECT"
  dotnet remove "$TEST_PROJECT" reference "$EFCORE_PROVIDER_PROJECT"
  dotnet add "$TEST_PROJECT" package "MongoDB.EntityFrameworkCore" -v "$PACKAGE_VERSION" --source "./build/nuget"

  echo "Run tests: $TEST_PROJECT"
  dotnet test "$TEST_PROJECT" -e MONGODB__URI="${MONGODB_URI}" --runtime "${TARGET_RUNTIME}" --results-directory "./build/test-results" --logger "junit;verbosity=detailed;LogFileName=TEST_{assembly}.xml;FailureBodyFormat=Verbose"

  echo "Revert changes for test project: $TEST_PROJECT"
  dotnet remove "$TEST_PROJECT" package "MongoDB.EntityFrameworkCore"
  dotnet add "$TEST_PROJECT" reference "$EFCORE_PROVIDER_PROJECT"
done
