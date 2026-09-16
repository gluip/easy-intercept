import Darwin
import Foundation

/// Port checks with the same semantics as Launcher.IsPortFree / FindFreePort on the .NET side.
enum Ports {
    static func isFree(_ port: Int) -> Bool {
        guard Settings.isValidPort(port) else { return false }
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return false }
        defer { close(fd) }

        // Kestrel binds with SO_REUSEADDR, so a port that only has TIME_WAIT leftovers counts as free
        var one: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, socklen_t(MemoryLayout<Int32>.size))

        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = in_port_t(port).bigEndian
        addr.sin_addr.s_addr = INADDR_ANY
        let result = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        return result == 0
    }

    static func findFree(from start: Int) -> Int {
        var port = start
        while port < 65535 {
            if port != Settings.proxyPort && isFree(port) { return port }
            port += 1
        }
        return start
    }
}
