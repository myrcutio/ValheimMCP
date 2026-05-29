#!/usr/bin/env bash
# Build ValheimMCP in Release and assemble a Thunderstore-ready zip in dist/.
#
# Run this locally — compiling requires Valheim's managed assemblies (referenced
# from your Steam install via the .csproj), which are NOT redistributable and so
# are not available on a stock CI runner.
#
# The version is read from the .csproj <Version> (the single source of truth) and
# injected into the packaged manifest.json, so you only ever bump it in one place.
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$(pwd)"

CSPROJ="src/ValheimMCP/ValheimMCP.csproj"
VERSION="$(grep -oP '<Version>\K[^<]+(?=</Version>)' "${CSPROJ}" | head -1)"
if [[ -z "${VERSION}" ]]; then
  echo "error: could not read <Version> from ${CSPROJ}" >&2
  exit 1
fi
echo "Packaging ValheimMCP v${VERSION}"

# 1. Build Release and capture the DLL path MSBuild reports (the .csproj uses a
#    custom OutputPath pointing into your BepInEx plugins dir, so we don't guess it).
BUILD_LOG="$(dotnet build src/ValheimMCP/ValheimMCP.csproj -c Release)"
echo "${BUILD_LOG}"
DLL="$(printf '%s\n' "${BUILD_LOG}" | grep -oP 'ValheimMCP -> \K.*ValheimMCP\.dll' | head -1)"
if [[ -z "${DLL}" || ! -f "${DLL}" ]]; then
  echo "error: could not locate built ValheimMCP.dll from the build output" >&2
  exit 1
fi
echo "Using DLL: ${DLL}"

# 2. Assemble the Thunderstore package layout in a staging dir.
STAGE="$(mktemp -d)"
trap 'rm -rf "${STAGE}"' EXIT
cp Thunderstore/icon.png README.md CHANGELOG.md LICENSE "${STAGE}/"
# Sync the manifest's version_number to the .csproj <Version> as we stage it.
sed -E "s/(\"version_number\"[[:space:]]*:[[:space:]]*\")[^\"]*\"/\1${VERSION}\"/" \
  Thunderstore/manifest.json > "${STAGE}/manifest.json"
mkdir -p "${STAGE}/plugins/ValheimMCP"
cp "${DLL}" "${STAGE}/plugins/ValheimMCP/"

# 3. Zip it.
mkdir -p dist
OUT="${ROOT}/dist/ValheimMCP-${VERSION}.zip"
rm -f "${OUT}"
( cd "${STAGE}" && zip -r -q "${OUT}" . )
echo "Wrote ${OUT}"
