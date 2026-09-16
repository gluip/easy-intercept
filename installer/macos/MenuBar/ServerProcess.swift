import Foundation

/// The .NET server as a child process: started from the bundle, logged to ~/Library/Logs, stopped on quit.
final class ServerProcess {
    /// The published .NET app inside the bundle (Resources, not MacOS: see build-macos.sh).
    static var executable: URL {
        Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/server/EasyIntercept")
    }

    static var logDir: URL {
        FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Logs/EasyIntercept")
    }

    static var logFile: URL { logDir.appendingPathComponent("server.log") }

    private var process: Process?
    private var stopping = false

    /// Called on the main thread when the server exits without us asking for it.
    var onUnexpectedExit: ((Int32) -> Void)?

    var isRunning: Bool { process?.isRunning ?? false }

    func start() throws {
        try Self.prepareLog()
        let log = try FileHandle(forWritingTo: Self.logFile)
        log.seekToEndOfFile()

        let p = Process()
        p.executableURL = Self.executable
        p.currentDirectoryURL = Self.executable.deletingLastPathComponent()
        // The shell opens the browser itself (not at login); the server follows us down if we crash.
        p.arguments = ["--no-browser", "--parent-pid=\(getpid())"]
        p.standardOutput = log
        p.standardError = log
        p.terminationHandler = { [weak self] proc in
            try? log.close()
            DispatchQueue.main.async {
                guard let self, !self.stopping else { return }
                self.onUnexpectedExit?(proc.terminationStatus)
            }
        }

        stopping = false
        try p.run()
        process = p
    }

    /// SIGTERM first (the .NET host shuts down gracefully within its 3 s timeout), SIGKILL as a last
    /// resort: the same idea as the exit watchdog in the Windows tray icon.
    func stop(timeout: TimeInterval = 8) {
        guard let p = process, p.isRunning else { return }
        stopping = true
        p.terminate()
        let deadline = Date().addingTimeInterval(timeout)
        while p.isRunning && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.1)
        }
        if p.isRunning { kill(p.processIdentifier, SIGKILL) }
        p.waitUntilExit()
    }

    /// Runs `EasyIntercept --install-ca` (see CaInstaller.cs) and returns its exit code and output.
    /// macOS shows its own password dialog while this runs; the CA is trusted for the current user only.
    static func installCa() -> (status: Int32, output: String) {
        let p = Process()
        p.executableURL = executable
        p.currentDirectoryURL = executable.deletingLastPathComponent()
        p.arguments = ["--install-ca"]
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        do { try p.run() } catch { return (-1, error.localizedDescription) }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, String(decoding: data, as: UTF8.self))
    }

    /// Keeps the log from growing forever: above 5 MB the previous log becomes server.log.1.
    private static func prepareLog() throws {
        let fm = FileManager.default
        try fm.createDirectory(at: logDir, withIntermediateDirectories: true)
        if let size = (try? fm.attributesOfItem(atPath: logFile.path))?[.size] as? UInt64, size > 5_000_000 {
            let previous = logDir.appendingPathComponent("server.log.1")
            try? fm.removeItem(at: previous)
            try? fm.moveItem(at: logFile, to: previous)
        }
        if !fm.fileExists(atPath: logFile.path) {
            fm.createFile(atPath: logFile.path, contents: nil)
        }
    }
}
