import ServiceManagement

/// "Open at login" through SMAppService (macOS 13+): the app shows up under
/// System Settings → General → Login Items, and macOS starts it at login with the launched-as-login-item
/// marker on the open event (see AppDelegate.launchedAsLoginItem).
enum LoginItem {
    static var isEnabled: Bool { SMAppService.mainApp.status == .enabled }

    static var requiresApproval: Bool { SMAppService.mainApp.status == .requiresApproval }

    static func setEnabled(_ enabled: Bool) throws {
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }

    static func openSystemSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }
}
