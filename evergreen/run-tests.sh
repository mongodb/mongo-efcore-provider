#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

MONGODB_URI=${MONGODB_URI:=mongodb://localhost:27017/}

if [ "$DRIVER_VERSION" = "latest" ]; then
  export DRIVER_VERSION="*"
fi

echo "Running EF Core tests for '${BUILD_CONFIGURATION}' configuration"
dotnet clean "./MongoDB.EFCoreProvider.sln"
# MongoDB 5.0 and 6.0 intermittently fail with "Unable to read from a snapshot due to pending collection
# catalog changes" when tests run in parallel, so run the test collections sequentially on those servers.
runner_args=()
case "${MONGODB_VERSION}" in
  5.0|6.0)
    echo "Running test collections sequentially for MongoDB ${MONGODB_VERSION}"
    runner_args=(-- xUnit.ParallelizeTestCollections=false)
    ;;
esac

dotnet test "./MongoDB.EFCoreProvider.sln" -e MONGODB_URI="${MONGODB_URI}" -c "${BUILD_CONFIGURATION}" --results-directory "./artifacts/test-results/${BUILD_CONFIGURATION// /}" --logger "junit;LogFileName=TEST_{assembly}.xml;FailureBodyFormat=Verbose" --logger "console;verbosity=detailed" "${runner_args[@]}"
