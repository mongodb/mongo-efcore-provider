#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

if [ -z "$PACKAGE_VERSION" ]; then
  echo Calculated PACKAGE_VERSION value: "$PACKAGE_VERSION"
fi

echo "Configure dotnet cli to use local manifest"
dotnet new tool-manifest --force

echo "Installing docfx tool"
dotnet tool install docfx --version "2.78.4" --local --verbosity q

BUILD_CONFIGURATION=""
if [[ "${PACKAGE_VERSION}" == "8."* ]]; then
    BUILD_CONFIGURATION="Release EF8"
fi
if [[ "${PACKAGE_VERSION}" == "9."* ]]; then
    BUILD_CONFIGURATION="Release EF9"
fi
if [[ "${PACKAGE_VERSION}" == "10."* ]]; then
    BUILD_CONFIGURATION="Release EF10"
fi

# Check if BUILD_CONFIGURATION is set
if [ -z "$BUILD_CONFIGURATION" ]; then
  echo "Error: Unknown PACKAGE_VERSION $PACKAGE_VERSION found. Update get-build-release-config.sh to handle this version."
  exit 1
fi

echo "Building the api-docs for '${PACKAGE_VERSION} using '${BUILD_CONFIGURATION}'"
dotnet restore src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj -p:Configuration="${BUILD_CONFIGURATION}"
dotnet tool run docfx metadata ./apidocs/docfx.json --property "Configuration=${BUILD_CONFIGURATION};ProduceReferenceAssembly=true"
dotnet tool run docfx build ./apidocs/docfx.json -o:./artifacts/apidocs/"$PACKAGE_VERSION"
