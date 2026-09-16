import AppKit

/// Shown when the configured UI port is taken: the macOS counterpart of Desktop/PortPrompt.cs.
enum PortPrompt {
    /// Returns the chosen port, or nil when the user chose Quit.
    static func show(busyPort: Int, suggestedPort: Int) -> Int? {
        var suggestion = String(suggestedPort)
        while true {
            let alert = NSAlert()
            alert.messageText = "Port \(busyPort) is already in use"
            alert.informativeText = "Another application is using port \(busyPort). " +
                "Choose a different port for the EasyIntercept web UI. " +
                "It will be saved so you don't have to do this again. " +
                "(The proxy itself always uses port \(Settings.proxyPort).)"
            alert.addButton(withTitle: "Use this port")
            alert.addButton(withTitle: "Quit")

            let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 120, height: 24))
            field.stringValue = suggestion
            field.alignment = .center
            alert.accessoryView = field
            alert.window.initialFirstResponder = field

            NSApp.activate(ignoringOtherApps: true)
            guard alert.runModal() == .alertFirstButtonReturn else { return nil }

            let text = field.stringValue.trimmingCharacters(in: .whitespaces)
            if let port = Int(text), (1...65535).contains(port), port != Settings.proxyPort {
                return port
            }
            suggestion = text
        }
    }
}
