import AppKit

/// Shown when the configured UI port is taken: the macOS counterpart of Desktop/PortPrompt.cs.
enum PortPrompt {
    /// Returns the chosen port, or nil when the user chose Quit.
    static func show(busyPort: Int, suggestedPort: Int) -> Int? {
        var suggestion = String(suggestedPort)
        var note = ""
        while true {
            let alert = NSAlert()
            alert.messageText = "Port \(busyPort) is already in use"
            alert.informativeText = "Another application is using port \(busyPort). " +
                "Choose a different port for the EasyIntercept web UI. " +
                "It will be saved so you don't have to do this again. " +
                "(The proxy itself always uses port \(Settings.proxyPort).)" + note
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
            if let port = Int(text), Settings.isValidPort(port), port != Settings.proxyPort {
                if Ports.isFree(port) { return port }
                note = "\n\nPort \(port) is in use as well."
            } else {
                note = "\n\nEnter a number between 1 and 65535 (\(Settings.proxyPort) is the proxy port)."
            }
            suggestion = text
        }
    }
}
