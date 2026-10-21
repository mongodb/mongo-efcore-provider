#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

MONGODB_URI=${MONGODB_URI:=mongodb://localhost:27017/}

if [ "$DRIVER_VERSION" = "latest" ]; then
  export DRIVER_VERSION="*"
fi

echo "Running EF Core tests for '${BUILD_CONFIGURATION}' configuration"
dotnet clean "./MongoDB.EFCoreProvider.sln"
test_args=(-e MONGODB_URI="${MONGODB_URI}" -c "${BUILD_CONFIGURATION}" --results-directory "./artifacts/test-results/${BUILD_CONFIGURATION// /}" --logger "junit;LogFileName=TEST_{assembly}.xml;FailureBodyFormat=Verbose" --logger "console;verbosity=detailed")

case "${MONGODB_VERSION}" in
  5.0|6.0)
    # MongoDB 5.0 and 6.0 intermittently fail with "Unable to read from a snapshot due to pending collection
    # catalog changes" when writes overlap, so run each test project in turn (dotnet test on the solution runs
    # them concurrently) with the test collections of each project running one at a time.
    echo "Running test projects and test collections sequentially for MongoDB ${MONGODB_VERSION}"
    exit_code=0
    for project in tests/*/*.csproj; do
      dotnet test "./${project}" "${test_args[@]}" -- xUnit.ParallelizeTestCollections=false || exit_code=$?
    done
    exit $exit_code
    ;;
esac

dotnet test "./MongoDB.EFCoreProvider.sln" "${test_args[@]}"
