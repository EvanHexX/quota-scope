#!/bin/zsh
# Builds QuotaScope.app from source on this Mac (docs/MACOS_BUILD.md, 7.2).
# Needs only the Xcode Command Line Tools. No Apple Developer account: the app
# is signed ad-hoc, or with a local self-signed identity named in
# QUOTASCOPE_SIGN_IDENTITY so a Keychain "Always Allow" survives rebuilds.
#
#   mac/build.sh [--release] [--install]
set -euo pipefail

cd "$(dirname "$0")"
configuration=debug
install=0
for arg in "$@"; do
  case "$arg" in
    --release) configuration=release ;;
    --install) install=1 ;;
    *) echo "usage: mac/build.sh [--release] [--install]" >&2; exit 2 ;;
  esac
done

echo "==> swift build -c $configuration"
swift build -c "$configuration"
bin="$(swift build -c "$configuration" --show-bin-path)"

echo "==> self-tests"
"$bin/quotascope-selftest"
"$bin/quotascope-providertest"

version="$(git describe --tags --abbrev=0 2>/dev/null || echo v0.0.0)"
version="${version#v}"
build="$(git rev-list --count HEAD 2>/dev/null || echo 0)"

app="$PWD/.build/QuotaScope.app"
echo "==> assembling $app ($version, build $build)"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$bin/QuotaScope" "$app/Contents/MacOS/QuotaScope"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>io.github.evanhexx.quotascope</string>
  <key>CFBundleName</key><string>QuotaScope</string>
  <key>CFBundleDisplayName</key><string>QuotaScope</string>
  <key>CFBundleExecutable</key><string>QuotaScope</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$build</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleLocalizations</key><array><string>en</string><string>ko</string></array>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

identity="${QUOTASCOPE_SIGN_IDENTITY:--}"
echo "==> signing with ${identity/#-/ad-hoc (-)}"
codesign --force --sign "$identity" "$app"
codesign --verify --strict "$app"
codesign -dv "$app" 2>&1 | grep -E '^(Identifier|Signature|Authority)=' || true

if (( install )); then
  target="$HOME/Applications/QuotaScope.app"
  echo "==> installing to $target"
  pkill -x QuotaScope 2>/dev/null && sleep 1 || true
  mkdir -p "$HOME/Applications"
  rm -rf "$target"
  cp -R "$app" "$target"
  echo "Open it with: open \"$target\""
else
  echo "Built: $app"
  echo "Run it with: open \"$app\""
fi
