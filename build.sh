#!/usr/bin/env bash
# Gera um executável único e autossuficiente do MediaOrganizer para cada sistema.
#
# Saída em dist/:
#   dist/windows/MediaOrganizer.exe
#   dist/linux/MediaOrganizer
#   dist/macos/MediaOrganizer-arm64.app  (Apple Silicon)
#   dist/macos/MediaOrganizer-x64.app    (Intel)
#
# Uso:
#   ./build.sh                          # todos os sistemas
#   ./build.sh osx-arm64                # só macOS Apple Silicon
#   VERSION=1.2.0 ./build.sh linux-x64
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT/src/MediaOrganizer.App/MediaOrganizer.App.csproj"
DIST="$ROOT/dist"
PUBLISH_ROOT="$ROOT/artifacts/publish"
VERSION="${VERSION:-1.1.0}"
# O Info.plist do macOS aceita só números ("1.1.0-rc.1" vira "1.1.0").
PLIST_VERSION="${VERSION%%-*}"
CONFIGURATION="${CONFIGURATION:-Release}"

if [ "$#" -gt 0 ]; then TARGETS=("$@"); else TARGETS=(win-x64 linux-x64 osx-arm64 osx-x64); fi

echo "==> Testes"
dotnet test --project "$ROOT/tests/MediaOrganizer.Core.Tests" -c "$CONFIGURATION"

for RID in "${TARGETS[@]}"; do
  echo "==> Publicando $RID"
  OUT="$PUBLISH_ROOT/$RID"
  rm -rf "$OUT"
  dotnet publish "$PROJECT" -c "$CONFIGURATION" -r "$RID" -o "$OUT" -p:Version="$VERSION"

  case "$RID" in
    win-*)
      mkdir -p "$DIST/windows"
      cp "$OUT/MediaOrganizer.exe" "$DIST/windows/"
      ;;
    linux-*)
      mkdir -p "$DIST/linux"
      cp "$OUT/MediaOrganizer" "$DIST/linux/"
      chmod +x "$DIST/linux/MediaOrganizer"
      ;;
    osx-*)
      ARCH="${RID#osx-}"
      APP="$DIST/macos/MediaOrganizer-$ARCH.app"
      rm -rf "$APP"
      mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
      cp "$OUT/MediaOrganizer" "$APP/Contents/MacOS/"
      chmod +x "$APP/Contents/MacOS/MediaOrganizer"
      cp "$ROOT/build/macos/app.icns" "$APP/Contents/Resources/"
      sed "s/\$(VERSION)/$PLIST_VERSION/g" "$ROOT/build/macos/Info.plist" > "$APP/Contents/Info.plist"
      # Assinatura ad-hoc (só no macOS): evita o erro "app danificado" em Apple Silicon.
      if command -v codesign >/dev/null 2>&1; then codesign --force --deep --sign - "$APP" || true; fi
      ;;
  esac
done

echo
echo "==> Pronto. Arquivos em $DIST"
find "$DIST" -type f \( -name MediaOrganizer -o -name MediaOrganizer.exe \) -exec ls -lh {} \;
