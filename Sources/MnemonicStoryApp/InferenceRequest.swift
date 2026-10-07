import Foundation
import MnemonicStoryCore

/// Owns the prompt before the detached run starts, through completion or cancellation.
/// Cancelling the worker first releases any writer holding the SecretBuffer lock.
final class InferenceRequest: @unchecked Sendable {
    private let worker: LocalInference
    private let prompt: SecretBuffer

    init(prompt: SecretBuffer, lifecycle: InferenceLifecycle = .shared) {
        self.prompt = prompt
        worker = LocalInference(lifecycle: lifecycle)
    }
    func run(executable: URL, model: URL) throws -> SecretBuffer {
        defer { prompt.clear() }
        return try worker.run(executable: executable, model: model, prompt: prompt)
    }
    func cancel() {
        worker.cancel()
        prompt.clear()
    }
    deinit { cancel() }
}
