#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

DOTNET_SDK_PATH=./.dotnet
mkdir -p "$DOTNET_SDK_PATH"

if [[ $OS =~ [Ww]indows.* ]]; then
  # From .NET 11 the installer prefers .tar.gz on Windows whenever a `tar` is on PATH. On Evergreen that is
  # GNU tar, which treats `C:\...` as a remote host ("Cannot connect to C: resolve failed"), so force .zip.
  export DOTNET_INSTALL_SKIP_TAR=1
  echo "Downloading Windows .NET SDK installer into $DOTNET_SDK_PATH folder..."
  curl -Lfo "$DOTNET_SDK_PATH"/dotnet-install.ps1 https://dot.net/v1/dotnet-install.ps1
  echo "Installing .NET 10.0 SDK..."
  powershell.exe "$DOTNET_SDK_PATH"/dotnet-install.ps1 -Version 10.0.104 -InstallDir "$DOTNET_SDK_PATH" -NoPath
  echo "Installing .NET 11.0 SDK (test projects target net11.0)..."
  powershell.exe "$DOTNET_SDK_PATH"/dotnet-install.ps1 -Version 11.0.100 -InstallDir "$DOTNET_SDK_PATH" -NoPath
  echo "Installing .NET 9.0 runtime..."
  powershell.exe "$DOTNET_SDK_PATH"/dotnet-install.ps1 -Channel 9.0 -InstallDir "$DOTNET_SDK_PATH" -NoPath -Runtime dotnet
  echo "Installing .NET 8.0 runtime..."
  powershell.exe "$DOTNET_SDK_PATH"/dotnet-install.ps1 -Channel 8.0 -InstallDir "$DOTNET_SDK_PATH" -NoPath -Runtime dotnet
else
  echo "Downloading .NET SDK installer into $DOTNET_SDK_PATH folder..."
  curl -Lfo "$DOTNET_SDK_PATH"/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
  echo "Installing .NET 10.0 SDK..."
  bash "$DOTNET_SDK_PATH"/dotnet-install.sh --version 10.0.104 --install-dir "$DOTNET_SDK_PATH" --no-path
  echo "Installing .NET 11.0 SDK (test projects target net11.0)..."
  bash "$DOTNET_SDK_PATH"/dotnet-install.sh --version 11.0.100 --install-dir "$DOTNET_SDK_PATH" --no-path
  echo "Installing .NET 9.0 runtime..."
  bash "$DOTNET_SDK_PATH"/dotnet-install.sh --channel 9.0 --install-dir "$DOTNET_SDK_PATH" --no-path --runtime dotnet
  echo "Installing .NET 8.0 runtime..."
  bash "$DOTNET_SDK_PATH"/dotnet-install.sh --channel 8.0 --install-dir "$DOTNET_SDK_PATH" --no-path --runtime dotnet
fi
