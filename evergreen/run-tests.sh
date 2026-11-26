#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

MONGODB_URI=${MONGODB_URI:=mongodb://localhost:27017/}

if [ "$DRIVER_VERSION" = "latest" ]; then
  export DRIVER_VERSION="*"
fi

echo "Running EF Core tests for '${BUILD_CONFIGURATION}' configuration"
dotnet clean "./MongoDB.EFCoreProvider.sln"
test_args=(-e MONGODB_URI="${MONGODB_URI}" -c "${BUILD_CONFIGURATION}" --results-directory "./artifacts/test-results/${BUILD_CONFIGURATION// /}" --logger "junit;LogFileName=TEST_{assembly}.xml;FailureBodyFormat=Verbose" --logger "console;verbosity=detailed")

# The EF11 SpecificationTests use xUnit v3 4.x, which only supports Microsoft.Testing.Platform, so "dotnet test" cannot run
# them (see AGENTS.md). Run them through their own test host instead and keep "dotnet test" for the other projects.
run_ef11_spec_tests() {
  local spec_dir="tests/MongoDB.EntityFrameworkCore.SpecificationTests"
  local results_dir="./artifacts/test-results/${BUILD_CONFIGURATION// /}"
  mkdir -p "${results_dir}"
  MONGODB_URI="${MONGODB_URI}" dotnet "./${spec_dir}/bin/${BUILD_CONFIGURATION}/net11.0/MongoDB.EntityFrameworkCore.SpecificationTests.dll" \
    -junit "${results_dir}/TEST_MongoDB.EntityFrameworkCore.SpecificationTests.xml" -noColor || return $?

  # A misconfigured host can exit 0 having run nothing, so require the results to contain test cases.
  local test_count
  test_count=$(grep -c "<testcase " "${results_dir}/TEST_MongoDB.EntityFrameworkCore.SpecificationTests.xml" || true)
  echo "EF11 SpecificationTests reported ${test_count} test cases"
  if [ "${test_count}" -eq 0 ]; then
    echo "ERROR: the EF11 SpecificationTests ran no tests" >&2
    return 1
  fi
}

exit_code=0
case "${MONGODB_VERSION}" in
  5.0|6.0)
    # MongoDB 5.0 and 6.0 intermittently fail with "Unable to read from a snapshot due to pending collection
    # catalog changes" when writes overlap, so run each test project in turn (dotnet test on the solution runs
    # them concurrently) with the test collections of each project running one at a time.
    echo "Running test projects and test collections sequentially for MongoDB ${MONGODB_VERSION}"
    for project in tests/*/*.csproj; do
      if [ "${BUILD_CONFIGURATION}" = "Debug EF11" ] && [[ "${project}" == *SpecificationTests* ]]; then
        dotnet build "./${project}" -c "${BUILD_CONFIGURATION}" || exit_code=$?
        run_ef11_spec_tests || exit_code=$?
      else
        dotnet test "./${project}" "${test_args[@]}" -- xUnit.ParallelizeTestCollections=false || exit_code=$?
      fi
    done
    exit $exit_code
    ;;
esac

if [ "${BUILD_CONFIGURATION}" = "Debug EF11" ]; then
  dotnet build "./MongoDB.EFCoreProvider.sln" -c "${BUILD_CONFIGURATION}"
  for project in tests/*/*.csproj; do
    if [[ "${project}" != *SpecificationTests* ]]; then
      dotnet test "./${project}" --no-build "${test_args[@]}" || exit_code=$?
    fi
  done
  run_ef11_spec_tests || exit_code=$?
  exit $exit_code
fi

dotnet test "./MongoDB.EFCoreProvider.sln" "${test_args[@]}"
