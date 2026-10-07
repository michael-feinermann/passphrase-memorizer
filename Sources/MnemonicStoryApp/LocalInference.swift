import Darwin
import Foundation
import MnemonicStoryCore

enum InferenceError: Error { case unavailable, launch, sandbox, untrustedRunner, protocolFailure, inference, cancelled, deadline }

/// One session owns one process. No shell, service, server, prompt arguments or environment values.
final class LocalInference: @unchecked Sendable {
    private let lock = NSLock()
    private let lifecycle: InferenceLifecycle
    private var process: Process?
    private var cancelled = false
    private var runStarted = false
    static let maximumBytes = 65_536
    static let deadlineSeconds = 600

    init(lifecycle: InferenceLifecycle = .shared) {
        self.lifecycle = lifecycle
        if !lifecycle.register(self) { cancelled = true }
    }

    func cancel() {
        let state: (pending: Bool, running: Process?) = lock.withLock {
            guard !cancelled else { return (false, nil) }
            cancelled = true
            guard runStarted else { return (true, nil) }
            guard let process, process.isRunning else { return (false, nil) }
            _ = kill(process.processIdentifier, SIGTERM)
            return (false, process)
        }
        if state.pending { lifecycle.unregister(self) }
        if let running = state.running {
            // Native SIGTERM cooperates with llama's abort callback and wipes owned buffers.
            // A stuck/untrusted model parser cannot hold a session open indefinitely.
            DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 2) {
                if running.isRunning { _ = kill(running.processIdentifier, SIGKILL) }
            }
        }
    }

    func run(executable: URL, model: URL, prompt: SecretBuffer) throws -> SecretBuffer {
        defer { prompt.clear() }
        try lock.withLock {
            guard !cancelled, !runStarted else { throw InferenceError.cancelled }
            runStarted = true
        }
        defer { prompt.clear(); lifecycle.unregister(self) }
        let task = Process()
        let input = Pipe(), output = Pipe()
        task.executableURL = executable
        task.arguments = [model.path]
        task.environment = ["PATH": "/usr/bin:/bin", "LANG": "C", "LC_ALL": "C"]
        task.currentDirectoryURL = URL(fileURLWithPath: "/")
        task.standardInput = input
        task.standardOutput = output
        task.standardError = FileHandle.nullDevice
        try lifecycle.launch {
            try lock.withLock {
                guard !cancelled else { throw InferenceError.cancelled }
                process = task
                do { try task.run() } catch { process = nil; throw InferenceError.launch }
            }
        }
        let timer = DispatchSource.makeTimerSource(queue: .global(qos: .utility))
        timer.schedule(deadline: .now() + .seconds(Self.deadlineSeconds))
        timer.setEventHandler { [weak self] in self?.cancel() }
        timer.resume()
        defer {
            timer.cancel()
            try? input.fileHandleForWriting.close()
            try? input.fileHandleForReading.close()
            try? output.fileHandleForReading.close()
            try? output.fileHandleForWriting.close()
            lock.withLock { if task.isRunning { _ = kill(task.processIdentifier, SIGKILL) } }
            task.waitUntilExit()
            lock.withLock { process = nil }
        }
        // Parent closes unused ends; otherwise EOF can be held open by our own descriptor.
        try? input.fileHandleForReading.close()
        try? output.fileHandleForWriting.close()
        let readFD = output.fileHandleForReading.fileDescriptor
        let writeFD = input.fileHandleForWriting.fileDescriptor
        guard fcntl(writeFD, F_SETNOSIGPIPE, 1) == 0 else { throw InferenceError.launch }
        var ready = [UInt8](repeating: 0, count: 4)
        try ready.withUnsafeMutableBytes { try readExactly(readFD, into: $0) }
        guard ready == [77, 83, 65, 73] else { throw InferenceError.sandbox }
        // Attest the process that actually started after its sandbox handshake,
        // before sending even the prompt length. A prelaunch path check cannot
        // attest a substituted executable at this point.
        guard task.isRunning, RuntimeGuard.validateRunningRunner(task.processIdentifier),
              task.isRunning, !lock.withLock({ cancelled }) else { throw InferenceError.untrustedRunner }
        guard prompt.count > 0, prompt.count <= Self.maximumBytes else { throw InferenceError.protocolFailure }
        var size = UInt32(prompt.count).littleEndian
        try withUnsafeBytes(of: &size) { try writeExactly(writeFD, bytes: $0) }
        guard try prompt.read({ try writeExactly(writeFD, bytes: $0); return true }) == true else { throw InferenceError.cancelled }
        prompt.clear()
        try? input.fileHandleForWriting.close()
        var outputSize: UInt32 = 0
        try withUnsafeMutableBytes(of: &outputSize) { try readExactly(readFD, into: $0) }
        let count = Int(UInt32(littleEndian: outputSize))
        guard (1...Self.maximumBytes).contains(count) else { throw InferenceError.protocolFailure }
        let story = SecretBuffer(count: count)
        do {
            try story.write { try readExactly(readFD, into: $0) }
            var trailing: UInt8 = 0
            let trailingCount = Darwin.read(readFD, &trailing, 1)
            guard trailingCount == 0 else { throw InferenceError.protocolFailure }
            task.waitUntilExit()
            guard !lock.withLock({ cancelled }), task.terminationReason == .exit, task.terminationStatus == 0 else { throw InferenceError.inference }
            // Strict UTF-8 validation happens only after the bounded protocol succeeds.
            guard story.read({ String(bytes: $0, encoding: .utf8) != nil }) == true else { throw InferenceError.protocolFailure }
            return story
        } catch { story.clear(); throw error }
    }

    private func readExactly(_ fd: Int32, into bytes: UnsafeMutableRawBufferPointer) throws {
        guard let base = bytes.baseAddress else { return }
        var offset = 0
        while offset < bytes.count {
            let n = Darwin.read(fd, base.advanced(by: offset), bytes.count - offset)
            if n < 0 && errno == EINTR { continue }
            guard n > 0 else { throw InferenceError.protocolFailure }
            offset += n
        }
    }
    private func writeExactly(_ fd: Int32, bytes: UnsafeRawBufferPointer) throws {
        guard let base = bytes.baseAddress else { return }
        var offset = 0
        while offset < bytes.count {
            let n = Darwin.write(fd, base.advanced(by: offset), bytes.count - offset)
            if n < 0 && errno == EINTR { continue }
            guard n > 0 else { throw InferenceError.protocolFailure }
            offset += n
        }
    }
    deinit { cancel() }
}
