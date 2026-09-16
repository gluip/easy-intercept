import AppKit
import Darwin

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var settings = Settings.load()
    private var api: ServerApi!
    private let server = ServerProcess()
    private var statusItem: StatusItemController?
    private var sigterm: DispatchSourceSignal?

    /// Started by macOS at login (or with --autostart): stay quiet, don't open a browser.
    private var launchedAtLogin = false

    private static let firstRunDoneKey = "firstRunDone"

    func applicationDidFinishLaunching(_ notification: Notification) {
        launchedAtLogin = Self.launchedAsLoginItem() || CommandLine.arguments.contains("--autostart")
        installSigtermHandler()
        api = ServerApi(baseUrl: settings.uiUrl)

        // Single instance: a second launch just brings up the UI of the running one, whether that is
        // another copy of this app or a `dotnet run` from a terminal (same rule as Program.cs).
        if api.isEasyIntercept() {
            if !launchedAtLogin { NSWorkspace.shared.open(settings.uiUrl) }
            NSApp.terminate(nil)
            return
        }

        // The proxy port is fixed, so a UI port equal to it is "busy" even before anything listens.
        if settings.uiPort == Settings.proxyPort || !Ports.isFree(settings.uiPort) {
            guard let chosen = PortPrompt.show(busyPort: settings.uiPort,
                                               suggestedPort: Ports.findFree(from: settings.uiPort + 1)) else {
                NSApp.terminate(nil)
                return
            }
            do {
                try Settings.save(uiPort: chosen)
            } catch {
                Alerts.warn("Could not save the port", "\(error.localizedDescription)\n\nUsing port \(chosen) for this session only.")
            }
            settings.uiPort = chosen
            server.uiPortOverride = chosen
            api = ServerApi(baseUrl: settings.uiUrl)
        }

        server.onUnexpectedExit = { [weak self] status in self?.serverStopped(status) }
        startServer()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        // Double-clicking the app in Finder while it runs: same as "Open EasyIntercept" in the menu.
        if server.isRunning { NSWorkspace.shared.open(settings.uiUrl) }
        return false
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        statusItem?.remove()
        server.stop()
        return .terminateNow
    }

    // MARK: Server lifecycle

    private func startServer() {
        do {
            try server.start()
        } catch {
            Alerts.warn("Could not start the EasyIntercept server", error.localizedDescription)
            NSApp.terminate(nil)
            return
        }

        // Kestrel needs a moment; poll off the main thread so the app stays responsive.
        DispatchQueue.global().async { [self] in
            let deadline = Date().addingTimeInterval(20)
            var ready = false
            while Date() < deadline && server.isRunning {
                if api.isEasyIntercept() { ready = true; break }
                Thread.sleep(forTimeInterval: 0.25)
            }
            DispatchQueue.main.async { self.serverStarted(ready: ready) }
        }
    }

    private func serverStarted(ready: Bool) {
        guard ready else {
            if server.isRunning {
                Alerts.warn("The EasyIntercept server did not respond in time",
                            "See \(ServerProcess.logFile.path) for details.")
                NSApp.terminate(nil)
            }
            // else: the server already exited and serverStopped() is showing the alert
            return
        }

        if statusItem == nil {
            let item = StatusItemController(settings: settings, api: api)
            item.onInstallCa = { [weak self] in self?.installCa() }
            item.onToggleLoginItem = { [weak self] on in self?.setLoginItem(on) }
            item.onQuit = { NSApp.terminate(nil) }
            statusItem = item
        }

        if !launchedAtLogin { NSWorkspace.shared.open(settings.uiUrl) }
        showFirstRunDialogIfNeeded()
    }

    private func serverStopped(_ status: Int32) {
        let alert = NSAlert()
        alert.alertStyle = .critical
        alert.messageText = "The EasyIntercept server stopped unexpectedly"
        alert.informativeText = "Exit code \(status). The log is at \(ServerProcess.logFile.path)."
        alert.addButton(withTitle: "Restart")
        alert.addButton(withTitle: "Show Log")
        alert.addButton(withTitle: "Quit")
        NSApp.activate(ignoringOtherApps: true)
        switch alert.runModal() {
        case .alertFirstButtonReturn:
            startServer()
        case .alertSecondButtonReturn:
            NSWorkspace.shared.activateFileViewerSelecting([ServerProcess.logFile])
            NSApp.terminate(nil)
        default:
            NSApp.terminate(nil)
        }
    }

    // MARK: First run: the choices the Windows installer offers as tasks

    private func showFirstRunDialogIfNeeded() {
        let defaults = UserDefaults.standard
        guard !defaults.bool(forKey: Self.firstRunDoneKey) else { return }
        defaults.set(true, forKey: Self.firstRunDoneKey)

        let alert = NSAlert()
        alert.messageText = "EasyIntercept is running in the menu bar"
        alert.informativeText = "Click the ⚡ icon at the top right of the screen to open the UI, " +
            "toggle the system proxy, launch a proxied browser or quit.\n\n" +
            "Two things you probably want:"
        alert.addButton(withTitle: "Continue")
        alert.addButton(withTitle: "Skip")

        let caBox = NSButton(checkboxWithTitle: "Install the CA certificate (needed to inspect HTTPS)", target: nil, action: nil)
        caBox.state = .on
        let loginBox = NSButton(checkboxWithTitle: "Open EasyIntercept at login", target: nil, action: nil)
        loginBox.state = .on
        let stack = NSStackView(views: [caBox, loginBox])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.frame = NSRect(x: 0, y: 0, width: 340, height: 48)
        alert.accessoryView = stack

        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        if loginBox.state == .on { setLoginItem(true) }
        if caBox.state == .on { installCa() }
    }

    private func setLoginItem(_ enabled: Bool) {
        do {
            try LoginItem.setEnabled(enabled)
            if enabled && LoginItem.requiresApproval {
                Alerts.info("Approval needed",
                            "macOS wants you to allow EasyIntercept under System Settings → General → Login Items.")
                LoginItem.openSystemSettings()
            }
        } catch {
            Alerts.warn(enabled ? "Could not enable \"Open at login\"" : "Could not disable \"Open at login\"",
                        error.localizedDescription)
        }
    }

    private func installCa() {
        // Runs on a background queue: the password dialog macOS shows can stay open for a while.
        DispatchQueue.global().async {
            let result = ServerProcess.installCa()
            DispatchQueue.main.async {
                if result.status == 0 {
                    Alerts.info("CA certificate installed",
                                "HTTPS traffic through the proxy can now be inspected in Safari, Chrome and most tools. " +
                                "Firefox keeps its own trust store and needs the certificate imported separately.")
                } else {
                    Alerts.warn("The CA certificate was not installed",
                                "If you cancelled the password dialog, try again from the menu.\n\n" + result.output)
                }
            }
        }
    }

    // MARK: Launch context

    /// True when macOS started us as a login item: the open-application event then carries the
    /// launched-as-login-item marker (keyAELaunchedAsLogInItem). Must be read during launch.
    private static func launchedAsLoginItem() -> Bool {
        guard let event = NSAppleEventManager.shared().currentAppleEvent,
              event.eventClass == AEEventClass(kCoreEventClass),
              event.eventID == AEEventID(kAEOpenApplication) else { return false }
        // Documented form: the property-data parameter holds the launched-as-login-item enum …
        if let property = event.paramDescriptor(forKeyword: AEKeyword(keyAEPropData)),
           property.enumCodeValue == OSType(keyAELaunchedAsLogInItem) {
            return true
        }
        // … but be lenient and also accept the marker as a boolean parameter of its own.
        if let flag = event.paramDescriptor(forKeyword: AEKeyword(keyAELaunchedAsLogInItem)), flag.booleanValue {
            return true
        }
        return false
    }

    /// Cocoa doesn't route a plain SIGTERM (logout, `kill`) through applicationShouldTerminate; do it
    /// ourselves so the server always gets stopped along with the shell.
    private func installSigtermHandler() {
        signal(SIGTERM, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        source.setEventHandler { NSApp.terminate(nil) }
        source.resume()
        sigterm = source
    }
}
