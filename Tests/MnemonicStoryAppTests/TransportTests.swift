import Darwin
import XCTest
import MnemonicStoryCore
@testable import MnemonicStoryApp

final class TransportTests: XCTestCase {
    private let input = "PUBLIC TRANSPORT FIXTURE"
    private let runnerIdentifier = "local.passphrasereminder.reminder.runner"

    private func command(_ executable: String, _ arguments: [String]) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        try process.run(); process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0, "Public native fixture command failed")
        guard process.terminationStatus == 0 else { throw CocoaError(.executableRuntimeMismatch) }
    }
    private func signingIdentity() throws -> String {
        guard let value = ProcessInfo.processInfo.environment["RUNTIME_TEST_SIGN_IDENTITY"], !value.isEmpty, value != "-" else {
            throw XCTSkip("Set RUNTIME_TEST_SIGN_IDENTITY to run signed native transport integration cases.")
        }
        return value
    }
    private func sign(_ executable: URL, identity: String, identifier: String) throws {
        try command("/usr/bin/codesign", ["--force", "--options", "runtime", "--timestamp=none",
                     "--identifier", identifier, "--sign", identity, executable.path])
    }
    private func withRunner(_ body: String, trusted: Bool = true, identifier: String? = nil,
                            test: (URL, URL) throws -> Void) throws {
        let identity = trusted ? try signingIdentity() : nil
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let source = directory.appendingPathComponent("public-protocol-fixture.c")
        let binary = directory.appendingPathComponent("PublicProtocolFixture")
        let text = """
        #include <stdint.h>
        #include <string.h>
        #include <unistd.h>
        #include <fcntl.h>
        #include <signal.h>
        #include <errno.h>
        #include <stdio.h>
        static int transfer(int fd, void *data, size_t n, int writing) {
            unsigned char *p=data;
            while(n) { ssize_t c=writing?write(fd,p,n):read(fd,p,n); if(c<0&&errno==EINTR)continue;
                if(c<=0)return 0; p+=c;n-=(size_t)c; } return 1;
        }
        static int size_frame(uint32_t n) { unsigned char h[4]={(unsigned char)n,(unsigned char)(n>>8),
            (unsigned char)(n>>16),(unsigned char)(n>>24)};return transfer(1,h,4,1); }
        int main(int argc,char **argv) { (void)argc;(void)argv;signal(SIGPIPE,SIG_IGN);
        \(body)
        return 0; }
        """
        // Positive cases execute signed native processes. There is no
        // interpreter fixture or production attestation bypass.
        try text.write(to: source, atomically: true, encoding: .utf8)
        try command("/usr/bin/clang", ["-O2", "-Wall", "-Wextra", "-Werror", "-Wno-unused-function", source.path, "-o", binary.path])
        if let identity { try sign(binary, identity: identity, identifier: identifier ?? runnerIdentifier) }
        try test(binary, directory)
    }
    private var receive: String {
        """
        if(!transfer(1,"MSAI",4,1))return 71;
        unsigned char h[4];if(!transfer(0,h,4,0))return 72;
        uint32_t n=(uint32_t)h[0]|((uint32_t)h[1]<<8)|((uint32_t)h[2]<<16)|((uint32_t)h[3]<<24);
        if(n>65536)return 73;
        unsigned char p[65536];if(!transfer(0,p,n,0))return 74;
        unsigned char trailing;if(read(0,&trailing,1)!=0)return 75;
        """
    }
    private var spy: String {
        """
        int fd=open(argv[1],O_WRONLY|O_CREAT|O_TRUNC,0600);if(fd<0)return 80;
        if(!transfer(1,"MSAI",4,1))return 81;
        unsigned char p[65536];ssize_t n;
        while((n=read(0,p,sizeof(p)))>0) { if(!transfer(fd,p,(size_t)n,1))return 82;fsync(fd); }
        close(fd);
        """
    }
    private func assertNoTransmission(_ executable: URL, directory: URL) throws {
        let capture = directory.appendingPathComponent("public-received-bytes")
        let prompt = SecretBuffer(input)
        XCTAssertThrowsError(try LocalInference().run(executable: executable, model: capture, prompt: prompt)) { error in
            guard case InferenceError.untrustedRunner = error else { return XCTFail("Unexpected rejection: \(error)") }
        }
        XCTAssertEqual(try Data(contentsOf: capture), Data(), "An untrusted child received prompt or framing bytes")
        XCTAssertTrue(prompt.isCleared)
        XCTAssertTrue(prompt.containsOnlyZeroBytes)
    }

    func testOneFramedResponseAndPromptWipe() throws {
        try withRunner(receive + """
        if(n!=strlen("PUBLIC TRANSPORT FIXTURE")||memcmp(p,"PUBLIC TRANSPORT FIXTURE",n)||argc!=2||strcmp(argv[1],"/public/model.gguf"))return 90;
        if(!size_frame(13)||!transfer(1,"PUBLIC RESULT",13,1))return 91;
        """) { binary, _ in
            let prompt = SecretBuffer(input)
            let result = try LocalInference().run(executable: binary, model: URL(fileURLWithPath: "/public/model.gguf"), prompt: prompt)
            defer { result.clear() }
            XCTAssertEqual(result.displayText(), "PUBLIC RESULT")
            XCTAssertTrue(prompt.isCleared)
            XCTAssertTrue(prompt.containsOnlyZeroBytes)
        }
    }
    func testProtocolFailuresWipePromptAndNeverReturnOutput() throws {
        let cases = ["return 71;", "transfer(1,\"NOPE\",4,1);", receive + "size_frame(65537);",
            receive + "size_frame(2);transfer(1,\"X\",1,1);",
            receive + "size_frame(1);unsigned char bad=255;transfer(1,&bad,1,1);",
            receive + "size_frame(1);transfer(1,\"XY\",2,1);",
            receive + "size_frame(1);transfer(1,\"X\",1,1);return 70;"]
        for source in cases {
            try withRunner(source) { binary, _ in
                let prompt = SecretBuffer(input)
                XCTAssertThrowsError(try LocalInference().run(executable: binary, model: URL(fileURLWithPath: "/public/model.gguf"), prompt: prompt))
                XCTAssertTrue(prompt.isCleared)
                XCTAssertTrue(prompt.containsOnlyZeroBytes)
            }
        }
    }
    func testPrecancelledSessionCannotLaunchOrReadPrompt() throws {
        let run = LocalInference(); run.cancel()
        let prompt = SecretBuffer(input)
        XCTAssertThrowsError(try run.run(executable: URL(fileURLWithPath: "/does/not/exist"), model: URL(fileURLWithPath: "/public/model.gguf"), prompt: prompt))
        XCTAssertTrue(prompt.isCleared)
    }
    func testCancellationStopsAWaitingWorkerAndClearsPrompt() throws {
        try withRunner(receive + "sleep(60);") { binary, _ in
            let session = LocalInference()
            let prompt = SecretBuffer(input)
            let started = Date()
            DispatchQueue.global().asyncAfter(deadline: .now() + 0.5) { session.cancel() }
            XCTAssertThrowsError(try session.run(executable: binary, model: URL(fileURLWithPath: "/public/model.gguf"), prompt: prompt))
            XCTAssertLessThan(Date().timeIntervalSince(started), 5)
            XCTAssertTrue(prompt.isCleared)
            XCTAssertTrue(prompt.containsOnlyZeroBytes)
        }
    }
    func testUnsignedReadySenderCannotReceivePrompt() throws {
        try withRunner(spy, trusted: false) { binary, directory in try assertNoTransmission(binary, directory: directory) }
    }
    func testWrongRunnerRoleCannotReceivePrompt() throws {
        try withRunner(spy, identifier: "local.passphrasereminder.reminder") { binary, directory in
            try assertNoTransmission(binary, directory: directory)
        }
    }
    func testReplacementAfterStaticValidationCannotReceivePrompt() throws {
        try withRunner(spy) { binary, directory in
            XCTAssertTrue(RuntimeGuard.validateRunner(binary))
            try sign(binary, identity: "-", identifier: runnerIdentifier)
            try assertNoTransmission(binary, directory: directory)
        }
    }
    func testInvalidPIDCannotBeAttested() {
        XCTAssertFalse(RuntimeGuard.validateRunningRunner(0))
        XCTAssertFalse(RuntimeGuard.validateRunningRunner(-1))
        XCTAssertFalse(RuntimeGuard.validateRunningRunner(getpid()))
    }
    func testShutdownReapsStubbornWorkerAfterCancelledSessionIsDropped() throws {
        try withRunner("signal(SIGTERM,SIG_IGN);" + receive + """
        int fd=open(argv[1],O_WRONLY|O_CREAT|O_TRUNC,0600);if(fd<0)return 92;
        dprintf(fd,"%ld",(long)getpid());close(fd);
        for(;;)pause();
        """) { binary, directory in
            let lifecycle = InferenceLifecycle()
            defer { lifecycle.shutdown {} }
            var activeSession: LocalInference? = LocalInference(lifecycle: lifecycle)
            let worker = try XCTUnwrap(activeSession)
            let prompt = SecretBuffer(input)
            let pidFile = directory.appendingPathComponent("public-child-pid")
            let finished = expectation(description: "Inference has returned after child reap")
            DispatchQueue.global().async {
                do {
                    let unexpected = try worker.run(executable: binary, model: pidFile, prompt: prompt)
                    unexpected.clear()
                    XCTFail("A cancelled stubborn worker unexpectedly returned output")
                } catch { }
                finished.fulfill()
            }
            let deadline = Date().addingTimeInterval(3)
            var pid: pid_t?
            while Date() < deadline {
                if let text = try? String(contentsOf: pidFile, encoding: .utf8), let value = pid_t(text), value > 0 {
                    pid = value; break
                }
                usleep(10_000)
            }
            let child = try XCTUnwrap(pid, "Public fixture did not receive the prompt")
            XCTAssertEqual(kill(child, 0), 0)
            activeSession?.cancel()
            activeSession = nil // Models clear this reference before an eventual application quit.
            let drained = expectation(description: "Shutdown confirms every cancelling child is reaped")
            let started = Date()
            lifecycle.shutdown {
                XCTAssertEqual(kill(child, 0), -1)
                XCTAssertEqual(errno, ESRCH)
                drained.fulfill()
            }
            wait(for: [finished, drained], timeout: 5)
            XCTAssertLessThan(Date().timeIntervalSince(started), 5)
            XCTAssertTrue(prompt.isCleared)
            XCTAssertTrue(prompt.containsOnlyZeroBytes)
        }
    }
    func testShutdownPermanentlyForbidsPendingAndLaterLaunches() throws {
        try withRunner("int fd=open(argv[1],O_WRONLY|O_CREAT|O_TRUNC,0600);if(fd>=0)close(fd);") { binary, directory in
            let lifecycle = InferenceLifecycle()
            defer { lifecycle.shutdown {} }
            let pending = LocalInference(lifecycle: lifecycle)
            let drained = expectation(description: "Unlaunched sessions cancel without waiting for a child")
            lifecycle.shutdown { drained.fulfill() }
            wait(for: [drained], timeout: 1)
            let capture = directory.appendingPathComponent("must-never-be-created")
            for session in [pending, LocalInference(lifecycle: lifecycle)] {
                let prompt = SecretBuffer(input)
                XCTAssertThrowsError(try session.run(executable: binary, model: capture, prompt: prompt)) { error in
                    guard case InferenceError.cancelled = error else { return XCTFail("Unexpected launch rejection: \(error)") }
                }
                XCTAssertTrue(prompt.isCleared)
                XCTAssertTrue(prompt.containsOnlyZeroBytes)
            }
            XCTAssertFalse(FileManager.default.fileExists(atPath: capture.path))
        }
    }

    func testOwnedPendingRequestIsWipedBeforeShutdownWithoutStartingDetachedRun() throws {
        let lifecycle = InferenceLifecycle()
        let prompt = SecretBuffer(input)
        let request = InferenceRequest(prompt: prompt, lifecycle: lifecycle)
        // The model owns the request before creating its asynchronous task.
        // A clear/quit can therefore wipe it even if that task never starts.
        request.cancel()
        XCTAssertTrue(prompt.isCleared)
        XCTAssertTrue(prompt.containsOnlyZeroBytes)
        let drained = expectation(description: "A cancelled pending request is fully drained")
        lifecycle.shutdown {
            XCTAssertTrue(prompt.isCleared)
            XCTAssertTrue(prompt.containsOnlyZeroBytes)
            drained.fulfill()
        }
        wait(for: [drained], timeout: 1)
        XCTAssertThrowsError(try request.run(executable: URL(fileURLWithPath: "/must/not/launch"),
                                           model: URL(fileURLWithPath: "/public/model.gguf"))) { error in
            guard case InferenceError.cancelled = error else { return XCTFail("Unexpected late start: \(error)") }
        }
        XCTAssertTrue(prompt.containsOnlyZeroBytes)
    }

    func testShutdownWaitsForLateResponseWipeBeforeCompletionTokenDrains() throws {
        let lifecycle = InferenceLifecycle()
        let token = try XCTUnwrap(lifecycle.registerCompletion())
        let result = SecretBuffer("PUBLIC LATE RESPONSE FIXTURE")
        let drained = DispatchSemaphore(value: 0)
        lifecycle.shutdown {
            XCTAssertTrue(result.isCleared)
            XCTAssertTrue(result.containsOnlyZeroBytes)
            drained.signal()
        }
        XCTAssertEqual(drained.wait(timeout: .now() + 0.05), .timedOut,
                       "Shutdown replied while the response handler still owned an unwiped result")
        XCTAssertNil(lifecycle.registerCompletion(), "Shutdown must forbid late response task registrations")
        result.clear()
        token.complete()
        token.complete() // Completion is idempotent; cancellation cannot underflow the group.
        XCTAssertEqual(drained.wait(timeout: .now() + 1), .success)
    }
}
