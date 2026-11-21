#!/usr/bin/env bash
# Builds the container-target DLL, runs the test suite, and packages a release
# tarball in dist/ (Jellyfin.Plugin.Federation-{version}.tar.gz).
#
# The tarball contains only the plugin DLL: the host server provides the
# runtime and all MediaBrowser.* assemblies. The DLL was compiled against
# the exact assemblies of the image recorded in
# container/Jellyfin.Plugin.Federation.Container.csproj (currently
# linuxserver/jellyfin 12.2.0); deploying it to a differently versioned
# server fails at plugin load.
set -euo pipefail
cd "$(dirname "$0")"

VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props)"
if [[ -z "$VERSION" ]]; then
    echo "error: could not read <Version> from Directory.Build.props" >&2
    exit 1
fi

echo "==> dotnet test -c Release"
dotnet test -c Release

echo "==> dotnet build -c Release (container target)"
dotnet build -c Release container/Jellyfin.Plugin.Federation.Container.csproj

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/Jellyfin.Plugin.Federation"
cp container/bin/Release/net10.0/Jellyfin.Plugin.Federation.dll \
   "$STAGE/Jellyfin.Plugin.Federation/"

mkdir -p dist
TARBALL="dist/Jellyfin.Plugin.Federation-$VERSION.tar.gz"
tar -czf "$TARBALL" -C "$STAGE" .
rm -f "$TARBALL.sha256"
sha256sum "$TARBALL" > "$TARBALL.sha256"

echo "==> $TARBALL"
cat "$TARBALL.sha256"
