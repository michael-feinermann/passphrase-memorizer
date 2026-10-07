import Foundation

/// Retains running and cancelling sessions until their child has been reaped.
/// Closing this lifecycle and launching a child share one lock; closure is permanent.
final class InferenceLifecycle: @unchecked Sendable {
    static let shared = InferenceLifecycle()
    private let lock = NSLock()
    private let drained = DispatchGroup()
    private var sessions: [ObjectIdentifier: LocalInference] = [:]
    private var shuttingDown = false

    func register(_ session: LocalInference) -> Bool {
        lock.withLock {
            guard !shuttingDown else { return false }
            sessions[ObjectIdentifier(session)] = session
            drained.enter()
            return true
        }
    }
    func unregister(_ session: LocalInference) {
        lock.withLock {
            if sessions.removeValue(forKey: ObjectIdentifier(session)) != nil { drained.leave() }
        }
    }
    func registerCompletion() -> CompletionToken? {
        lock.withLock {
            guard !shuttingDown else { return nil }
            drained.enter()
            return CompletionToken(lifecycle: self)
        }
    }
    fileprivate func finishCompletion() { drained.leave() }
    func launch(_ body: () throws -> Void) throws {
        try lock.withLock {
            guard !shuttingDown else { throw InferenceError.cancelled }
            try body()
        }
    }
    func shutdown(completion: @escaping @Sendable () -> Void) {
        let active = lock.withLock {
            shuttingDown = true
            return Array(sessions.values)
        }
        // Cancellation cooperates first, then escalates after two seconds.
        // Remain alive until every child is reaped and every response-handling
        // task has wiped a late result or handed the checked result to its model.
        for session in active { session.cancel() }
        drained.notify(queue: .global(qos: .utility), execute: completion)
    }
}

final class CompletionToken: @unchecked Sendable {
    private let lifecycle: InferenceLifecycle
    private let lock = NSLock()
    private var completed = false
    fileprivate init(lifecycle: InferenceLifecycle) { self.lifecycle = lifecycle }
    func complete() {
        let shouldLeave = lock.withLock {
            guard !completed else { return false }
            completed = true
            return true
        }
        if shouldLeave { lifecycle.finishCompletion() }
    }
    deinit { complete() }
}
