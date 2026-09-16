import Foundation

struct Browser {
    let id: String
    let name: String
}

struct ServerPaths {
    let root: String
    let sessions: String
}

/// Thin client for the server's localhost API. Synchronous on purpose: every caller runs on the main
/// thread from a menu action or menuWillOpen, and localhost answers in well under the timeout.
final class ServerApi {
    let baseUrl: URL
    private let session: URLSession

    init(baseUrl: URL) {
        self.baseUrl = baseUrl
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 1.5
        config.timeoutIntervalForResource = 3
        session = URLSession(configuration: config)
    }

    /// True when an EasyIntercept server (not some other program) answers on the port.
    func isEasyIntercept() -> Bool {
        guard let info = getJson("/api/info") else { return false }
        return info["proxyPort"] != nil
    }

    func systemProxyEnabled() -> Bool {
        (getJson("/api/system-proxy")?["enabled"] as? Bool) ?? false
    }

    func setSystemProxy(_ enabled: Bool) {
        _ = request("POST", "/api/system-proxy", body: ["enabled": enabled])
    }

    func browsers() -> [Browser] {
        guard let list = getJson("/api/browser-launch")?["browsers"] as? [[String: Any]] else { return [] }
        return list.compactMap { entry in
            guard let id = entry["id"] as? String, let name = entry["name"] as? String else { return nil }
            return Browser(id: id, name: name)
        }
    }

    func launchBrowser(id: String) -> Bool {
        guard let (status, _) = request("POST", "/api/browser-launch", body: ["browserId": id]) else { return false }
        return status == 200
    }

    func paths() -> ServerPaths? {
        guard let json = getJson("/api/paths"),
              let root = json["root"] as? String, let sessions = json["sessions"] as? String else { return nil }
        return ServerPaths(root: root, sessions: sessions)
    }

    private func getJson(_ path: String) -> [String: Any]? {
        guard let (status, data) = request("GET", path), status == 200 else { return nil }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    private func request(_ method: String, _ path: String, body: [String: Any]? = nil) -> (Int, Data)? {
        guard let url = URL(string: path, relativeTo: baseUrl) else { return nil }
        var req = URLRequest(url: url)
        req.httpMethod = method
        if let body {
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = try? JSONSerialization.data(withJSONObject: body)
        }

        let done = DispatchSemaphore(value: 0)
        var result: (Int, Data)?
        session.dataTask(with: req) { data, response, _ in
            if let http = response as? HTTPURLResponse, let data { result = (http.statusCode, data) }
            done.signal()
        }.resume()
        done.wait()
        return result
    }
}
