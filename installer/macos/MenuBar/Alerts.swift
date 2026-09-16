import AppKit

/// NSAlert helpers. Accessory apps have no window, so alerts explicitly bring the app forward.
enum Alerts {
    static func warn(_ message: String, _ details: String) {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = message
        alert.informativeText = details
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    static func info(_ message: String, _ details: String) {
        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = message
        alert.informativeText = details
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }
}
