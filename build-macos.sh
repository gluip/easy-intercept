#!/usr/bin/env bash
# Builds the macOS distribution, the counterpart of build-installer.ps1:
#   frontend (Vite) -> self-contained .NET publish -> Swift menu bar shell -> EasyIntercept.app -> .dmg
#
# Usage:   ./build-macos.sh --version 0.4.0 [--skip-frontend] [--skip-dmg] [--skip-notarize]
# Output:  dist/EasyIntercept-<version>-arm64.dmg
#
# Signing is optional and driven by the environment, so a laptop and CI behave the same:
#   MACOS_SIGN_IDENTITY   "Developer ID Application: …": sign with the hardened runtime
#   APPLE_ID, APPLE_TEAM_ID, APPLE_APP_PASSWORD   additionally notarize and staple (needs the identity)
# Without an identity the bundle is ad-hoc signed (Apple Silicon refuses unsigned code); Gatekeeper then
# asks the user to allow the app once, see README.
set -euo pipefail

VERSION="0.1.0"
ARCH="arm64"
SKIP_FRONTEND=0
SKIP_DMG=0
SKIP_NOTARIZE=0

usage() { sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --version=*) VERSION="${1#*=}"; shift ;;
        --arch) ARCH="$2"; shift 2 ;;
        --arch=*) ARCH="${1#*=}"; shift ;;
        --skip-frontend) SKIP_FRONTEND=1; shift ;;
        --skip-dmg) SKIP_DMG=1; shift ;;
        --skip-notarize) SKIP_NOTARIZE=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Version must look like x.y.z (got '$VERSION')" >&2; exit 2; }
case "$ARCH" in
    arm64) RID="osx-arm64"; SWIFT_TARGET="arm64-apple-macos13.0" ;;
    x64)   RID="osx-x64";   SWIFT_TARGET="x86_64-apple-macos13.0" ;;
    *) echo "--arch must be arm64 or x64 (got '$ARCH')" >&2; exit 2 ;;
esac

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MACOS_DIR="$ROOT/installer/macos"
DIST="$ROOT/dist"
WORK="$DIST/macos-$ARCH"
PUBLISH="$WORK/publish"
APP="$WORK/EasyIntercept.app"
DMG="$DIST/EasyIntercept-$VERSION-$ARCH.dmg"
IDENTITY="${MACOS_SIGN_IDENTITY:-}"

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }
ok() { printf '\033[32m%s\033[0m\n' "$*"; }

# ---------------------------------------------------------------- frontend
if [[ $SKIP_FRONTEND -eq 0 ]]; then
    step "Building frontend"
    (
        cd "$ROOT/frontend"
        # Never let a local proxy setting (possibly EasyIntercept itself) get in the way of npm
        unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy
        export NO_PROXY="*"
        [[ -d node_modules ]] || npm ci
        npx vite build --emptyOutDir
    )
fi

# ---------------------------------------------------------------- .NET publish
step "Publishing EasyIntercept $VERSION ($RID, self-contained)"
rm -rf "$WORK"
mkdir -p "$WORK"
# Folder publish, not single-file: single-file extracts the native libraries outside the bundle at run
# time, which is both slower and incompatible with a signed/notarized bundle.
dotnet publish "$ROOT/EasyIntercept/EasyIntercept.csproj" \
    -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    -p:Version="$VERSION" \
    -o "$PUBLISH" --nologo

# ---------------------------------------------------------------- Swift shell
step "Compiling menu bar app ($SWIFT_TARGET)"
swiftc -O -swift-version 5 -module-name EasyInterceptMenuBar \
    -sdk "$(xcrun --show-sdk-path)" -target "$SWIFT_TARGET" \
    "$MACOS_DIR"/MenuBar/*.swift -o "$WORK/EasyIntercept"

# ---------------------------------------------------------------- bundle
# The server lives under Contents/Resources: codesign treats everything in Contents/MacOS as code
# and refuses the (non-Mach-O) managed .dlls there, while under Resources they are sealed as data
# and only the actual Mach-O files (apphost, runtime dylibs) need their own signature.
SERVER="$APP/Contents/Resources/server"
step "Assembling $APP"
mkdir -p "$APP/Contents/MacOS" "$SERVER"
cp "$MACOS_DIR/Info.plist" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" "$APP/Contents/Info.plist"
cp "$WORK/EasyIntercept" "$APP/Contents/MacOS/EasyIntercept"
cp -R "$PUBLISH/." "$SERVER/"
rm -f "$SERVER/createdump"   # crash-dump helper, not needed and one less binary to sign
cp "$MACOS_DIR/EasyIntercept.icns" "$APP/Contents/Resources/EasyIntercept.icns"

# ---------------------------------------------------------------- sign
if [[ -n "$IDENTITY" ]]; then
    step "Signing with '$IDENTITY' (hardened runtime)"
else
    step "Signing ad hoc (set MACOS_SIGN_IDENTITY for a Developer ID signature)"
fi

sign_file() {
    if [[ -n "$IDENTITY" ]]; then
        codesign --force --sign "$IDENTITY" --options runtime --timestamp \
            --entitlements "$MACOS_DIR/server.entitlements" "$1"
    else
        codesign --force --sign - "$1"
    fi
}

# codesign only signs the bundle's main executable; every Mach-O inside server/ (apphost, runtime
# dylibs) must be signed first. Detect them instead of keeping a list of runtime file names.
while IFS= read -r file; do
    if file -b "$file" | grep -q '^Mach-O'; then sign_file "$file"; fi
done < <(find "$SERVER" -type f)

if [[ -n "$IDENTITY" ]]; then
    codesign --force --sign "$IDENTITY" --options runtime --timestamp "$APP"
else
    codesign --force --sign - "$APP"
fi
codesign --verify --strict --verbose=2 "$APP"

# ---------------------------------------------------------------- notarize
can_notarize=0
if [[ -n "$IDENTITY" && -n "${APPLE_ID:-}" && -n "${APPLE_TEAM_ID:-}" && -n "${APPLE_APP_PASSWORD:-}" && $SKIP_NOTARIZE -eq 0 ]]; then
    can_notarize=1
fi

notarize() {
    xcrun notarytool submit "$1" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" \
        --password "$APPLE_APP_PASSWORD" --wait
    xcrun stapler staple "$2"
}

if [[ $can_notarize -eq 1 ]]; then
    step "Notarizing app"
    ditto -c -k --keepParent "$APP" "$WORK/EasyIntercept.zip"
    notarize "$WORK/EasyIntercept.zip" "$APP"
elif [[ -n "$IDENTITY" ]]; then
    echo "Skipping notarization (set APPLE_ID, APPLE_TEAM_ID and APPLE_APP_PASSWORD to enable)"
fi

if [[ $SKIP_DMG -eq 1 ]]; then
    ok "App bundle ready: $APP (disk image skipped)"
    exit 0
fi

# ---------------------------------------------------------------- dmg
step "Creating disk image"
STAGE="$WORK/dmg"
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
# hdiutil occasionally reports "Resource busy" on CI runners; a second attempt normally succeeds
for attempt in 1 2 3; do
    if hdiutil create -volname "EasyIntercept $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null; then
        break
    fi
    [[ $attempt -lt 3 ]] || { echo "hdiutil failed three times" >&2; exit 1; }
    sleep 3
done

if [[ -n "$IDENTITY" ]]; then
    codesign --force --sign "$IDENTITY" --timestamp "$DMG"
fi
if [[ $can_notarize -eq 1 ]]; then
    step "Notarizing disk image"
    notarize "$DMG" "$DMG"
fi

ok "Disk image ready: $DMG"
