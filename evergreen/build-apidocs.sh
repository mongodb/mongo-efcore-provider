#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

if [ -z "$PACKAGE_VERSION" ]; then
  PACKAGE_VERSION=$(sh ./evergreen/generate-version.sh)
  echo Calculated PACKAGE_VERSION value: "$PACKAGE_VERSION"
fi

echo "Configure dotnet cli to use local manifest"
dotnet new tool-manifest --force

echo "Installing docfx tool"
dotnet tool install docfx --version "2.76.0" --local --verbosity q

echo "Building the api-docs"
dotnet tool run docfx metadata ./apidocs/docfx.json --property ProduceReferenceAssembly=true
dotnet tool run docfx build ./apidocs/docfx.json -o:./artifacts/apidocs/"$PACKAGE_VERSION"
