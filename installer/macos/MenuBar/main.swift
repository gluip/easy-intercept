import AppKit

// Menu bar shell for EasyIntercept on macOS: no window, no Dock icon, a ⚡ in the status area of the
// menu bar. The actual server is the .NET app in Contents/Resources/server, run as a child process and
// driven over its localhost API (see AppDelegate).
let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
