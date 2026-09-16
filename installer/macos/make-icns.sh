#!/usr/bin/env bash
# Renders the app icon: the same ⚡ glyph as frontend/public/favicon.svg and installer/make-icons.ps1,
# drawn with the system emoji font, into installer/macos/EasyIntercept.icns (checked in, like icon.ico).
# Usage: installer/macos/make-icns.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

cat > "$WORK/render.swift" <<'SWIFT'
import AppKit

let output = CommandLine.arguments[1]
let size: CGFloat = 1024
let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { rect in
    let text = NSAttributedString(string: "⚡", attributes: [.font: NSFont.systemFont(ofSize: 780)])
    let bounds = text.boundingRect(with: rect.size, options: [.usesLineFragmentOrigin])
    let origin = NSPoint(x: (size - bounds.width) / 2, y: (size - bounds.height) / 2)
    text.draw(with: NSRect(origin: origin, size: bounds.size), options: [.usesLineFragmentOrigin])
    return true
}
guard let cg = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { exit(1) }
let rep = NSBitmapImageRep(cgImage: cg)
guard let png = rep.representation(using: .png, properties: [:]) else { exit(1) }
try png.write(to: URL(fileURLWithPath: output))
SWIFT

echo "==> Rendering ⚡ at 1024 px"
swiftc -O -sdk "$(xcrun --show-sdk-path)" "$WORK/render.swift" -o "$WORK/render"
"$WORK/render" "$WORK/icon-1024.png"

echo "==> Building iconset"
ICONSET="$WORK/EasyIntercept.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$WORK/icon-1024.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$WORK/icon-1024.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done

iconutil -c icns "$ICONSET" -o "$HERE/EasyIntercept.icns"
echo "Icon written to $HERE/EasyIntercept.icns"
