import Foundation

/// The user-level overrides the server reads from <DataRoot>/appsettings.json. Same file and keys
/// as StartupOptions.SaveUserSetting on the .NET side, so a port chosen here is what the server binds.
struct Settings {
    static let defaultUiPort = 1337
    static let proxyPort = 9999

    /// ~/Library/Application Support/EasyIntercept: AppPaths.DefaultRoot on macOS.
    static var defaultDataRoot: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("EasyIntercept")
    }

    static var settingsFile: URL { defaultDataRoot.appendingPathComponent("appsettings.json") }

    var uiPort: Int

    var uiUrl: URL { URL(string: "http://localhost:\(uiPort)")! }
    var agentGuideUrl: URL { uiUrl.appendingPathComponent("llms.txt") }

    static func load() -> Settings {
        var settings = Settings(uiPort: defaultUiPort)
        if let value = readJson()?["UiPort"] {
            if let port = value as? Int { settings.uiPort = port }
            else if let text = value as? String, let port = Int(text) { settings.uiPort = port }
        }
        return settings
    }

    /// Writes UiPort and keeps every other key in the file (DataRoot, …) as it was.
    static func save(uiPort: Int) throws {
        var json = readJson() ?? [:]
        json["UiPort"] = uiPort
        try FileManager.default.createDirectory(at: defaultDataRoot, withIntermediateDirectories: true)
        let data = try JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: settingsFile, options: .atomic)
    }

    private static func readJson() -> [String: Any]? {
        guard let data = try? Data(contentsOf: settingsFile) else { return nil }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }
}
