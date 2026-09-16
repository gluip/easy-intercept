import AppKit

/// The ⚡ in the menu bar and its menu: the same items as the Windows tray (TrayIconService.BuildMenu),
/// refreshed from the server's API every time the menu opens.
final class StatusItemController: NSObject, NSMenuDelegate {
    private let settings: Settings
    private let api: ServerApi
    private let statusItem: NSStatusItem
    private let menu = NSMenu()

    private let proxyItem: NSMenuItem
    private let browserItem: NSMenuItem
    private let loginItem: NSMenuItem

    var onInstallCa: (() -> Void)?
    var onToggleLoginItem: ((Bool) -> Void)?
    var onQuit: (() -> Void)?

    init(settings: Settings, api: ServerApi) {
        self.settings = settings
        self.api = api
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)

        proxyItem = NSMenuItem(title: "System proxy (127.0.0.1:\(Settings.proxyPort))",
                               action: #selector(toggleSystemProxy), keyEquivalent: "")
        browserItem = NSMenuItem(title: "Launch proxied browser", action: nil, keyEquivalent: "")
        loginItem = NSMenuItem(title: "Open at login", action: #selector(toggleLoginItem), keyEquivalent: "")
        super.init()

        if let button = statusItem.button {
            let image = NSImage(systemSymbolName: "bolt.fill", accessibilityDescription: "EasyIntercept")
            image?.isTemplate = true
            button.image = image
            button.imagePosition = .imageLeading
            button.toolTip = "EasyIntercept — \(settings.uiUrl.absoluteString)"
        }

        let open = NSMenuItem(title: "Open EasyIntercept", action: #selector(openUi), keyEquivalent: "")
        open.attributedTitle = NSAttributedString(
            string: open.title,
            attributes: [.font: NSFont.boldSystemFont(ofSize: NSFont.systemFontSize)])

        let items: [NSMenuItem] = [
            open,
            .separator(),
            proxyItem,
            browserItem,
            NSMenuItem(title: "Open sessions folder", action: #selector(openSessionsFolder), keyEquivalent: ""),
            NSMenuItem(title: "Copy link for coding agents", action: #selector(copyAgentGuideLink), keyEquivalent: ""),
            .separator(),
            NSMenuItem(title: "Install CA certificate…", action: #selector(installCa), keyEquivalent: ""),
            loginItem,
            .separator(),
            NSMenuItem(title: "Quit EasyIntercept", action: #selector(quit), keyEquivalent: "q"),
        ]
        for item in items {
            item.target = self
            menu.addItem(item)
        }
        menu.delegate = self
        statusItem.menu = menu
    }

    func remove() {
        NSStatusBar.system.removeStatusItem(statusItem)
    }

    // MARK: NSMenuDelegate

    func menuWillOpen(_ menu: NSMenu) {
        proxyItem.state = api.systemProxyEnabled() ? .on : .off
        loginItem.state = LoginItem.isEnabled ? .on : .off
        refreshBrowserItem()
    }

    // One browser → direct item, several → submenu, none → disabled. Same as the Windows tray.
    private func refreshBrowserItem() {
        browserItem.submenu = nil
        browserItem.representedObject = nil
        browserItem.action = nil

        let browsers = api.browsers()
        if browsers.isEmpty {
            browserItem.title = "Launch proxied browser (none found)"
            browserItem.isEnabled = false
            return
        }

        browserItem.isEnabled = true
        if browsers.count == 1 {
            browserItem.title = "Launch proxied \(browsers[0].name)"
            browserItem.representedObject = browsers[0].id
            browserItem.action = #selector(launchBrowser(_:))
            browserItem.target = self
            return
        }

        browserItem.title = "Launch proxied browser"
        let submenu = NSMenu()
        for browser in browsers {
            let item = NSMenuItem(title: browser.name, action: #selector(launchBrowser(_:)), keyEquivalent: "")
            item.representedObject = browser.id
            item.target = self
            submenu.addItem(item)
        }
        browserItem.submenu = submenu
    }

    // MARK: Actions

    @objc private func openUi() {
        NSWorkspace.shared.open(settings.uiUrl)
    }

    @objc private func toggleSystemProxy() {
        api.setSystemProxy(!api.systemProxyEnabled())
    }

    @objc private func launchBrowser(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        if !api.launchBrowser(id: id) {
            Alerts.warn("Could not launch the proxied browser", "See \(ServerProcess.logFile.path) for details.")
        }
    }

    @objc private func openSessionsFolder() {
        let folder = api.paths()?.sessions ?? Settings.defaultDataRoot.appendingPathComponent("sessions").path
        try? FileManager.default.createDirectory(atPath: folder, withIntermediateDirectories: true)
        NSWorkspace.shared.open(URL(fileURLWithPath: folder, isDirectory: true))
    }

    // The guide at /llms.txt is only useful once a person points their agent at it; this puts the
    // link on the clipboard so it can go straight into a project's CLAUDE.md / AGENTS.md.
    @objc private func copyAgentGuideLink() {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(settings.agentGuideUrl.absoluteString, forType: .string)
        flash("Copied")
    }

    @objc private func installCa() {
        onInstallCa?()
    }

    @objc private func toggleLoginItem() {
        onToggleLoginItem?(!LoginItem.isEnabled)
    }

    @objc private func quit() {
        onQuit?()
    }

    /// Shows a word next to the icon for a moment, as feedback for actions without a visible result.
    private func flash(_ text: String, for seconds: TimeInterval = 2) {
        guard let button = statusItem.button else { return }
        button.title = " \(text)"
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { button.title = "" }
    }
}
