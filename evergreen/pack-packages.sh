#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

if [ -z "$PACKAGE_VERSION" ]; then
  PACKAGE_VERSION=$(sh ./evergreen/generate-version.sh)
  echo Calculated PACKAGE_VERSION value: "$PACKAGE_VERSION"
fi

BUILD_CONFIGURATION="Release"
if [[ "${PACKAGE_VERSION}" == "9."* ]]; then
    BUILD_CONFIGURATION="Release EF9"
fi

echo Creating nuget package $PACKAGE_VERSION using $BUILD_CONFIGURATION build configuration...

dotnet clean ./MongoDB.EFCoreProvider.sln
dotnet pack ./MongoDB.EFCoreProvider.sln -o ./artifacts/nuget -c "$BUILD_CONFIGURATION" -p:Version="$PACKAGE_VERSION" --include-symbols -p:SymbolPackageFormat=snupkg -p:ContinuousIntegrationBuild=true
