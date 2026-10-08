import Darwin
import Foundation
import MnemonicStoryCore
import XCTest
@testable import MnemonicStoryApp

@MainActor final class ModelLocationTests: XCTestCase {
    private func withLocation(_ body: (URL, UserDefaults, String) throws -> Void) throws {
        let name = "local.memorizer.model.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        let directory = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent(name, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer {
            defaults.removePersistentDomain(forName: name)
            try? FileManager.default.removeItem(at: directory)
        }
        try body(directory, defaults, name)
    }

    private func writePublicModel(to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        // Only a public header fixture: restoring a location never loads an LLM.
        try (Data("GGUF".utf8) + Data(repeating: 0, count: 20)).write(to: url)
    }

    func testSelectedLocalModelIsRestoredWithoutSessionContentsOrAutomaticInference() throws {
        try withLocation { directory, defaults, name in
            let url = directory.appendingPathComponent("public-model.gguf")
            try writePublicModel(to: url)
            defaults.set(940.0, forKey: "windowWidth")
            defaults.set(880.0, forKey: "windowHeight")
            let first = MnemonicAssistantModel(defaults: defaults, preferenceDomain: name)
            XCTAssertTrue(first.useModel(at: url))
            first.language = .german
            first.setPhrase("abandon about ability")
            first.clear()
            XCTAssertTrue(first.hasModel)
            first.terminate()
            XCTAssertFalse(first.hasModel)
            XCTAssertEqual(defaults.string(forKey: AppPreferences.modelPathKey), url.path)
            XCTAssertEqual(Set(defaults.persistentDomain(forName: name)?.keys ?? Dictionary<String, Any>().keys),
                           ["preferredLanguage", "windowWidth", "windowHeight", "modelPath"])
            let restarted = MnemonicAssistantModel(defaults: defaults, preferenceDomain: name)
            defer { restarted.terminate() }
            XCTAssertTrue(restarted.hasModel)
            XCTAssertEqual(restarted.modelName, url.lastPathComponent)
            XCTAssertEqual(restarted.language, .german)
            XCTAssertFalse(restarted.hasPhrase)
            XCTAssertFalse(restarted.hasStory)
            XCTAssertFalse(restarted.isRunning)
            XCTAssertFalse(restarted.canGenerate)
            XCTAssertNil(restarted.error)
            XCTAssertEqual(defaults.double(forKey: "windowWidth"), 940)
            XCTAssertEqual(defaults.double(forKey: "windowHeight"), 880)
        }
    }

    func testUnavailableSavedLocationIsExplainedAndRediscoveredWhenItReturns() throws {
        try withLocation { directory, defaults, name in
            let url = directory.appendingPathComponent("temporarily-unavailable.gguf")
            defaults.set(url.path, forKey: AppPreferences.modelPathKey)
            for language in [AppLanguage.english, .german] {
                defaults.set(language.rawValue, forKey: "preferredLanguage")
                let model = MnemonicAssistantModel(defaults: defaults, preferenceDomain: name)
                XCTAssertFalse(model.hasModel)
                XCTAssertNil(model.modelName)
                XCTAssertFalse(model.canGenerate)
                let message = try XCTUnwrap(model.error)
                XCTAssertTrue(message.contains(language == .english ? "last selected model" : "zuletzt gewählte Modell"))
                model.terminate()
                XCTAssertEqual(defaults.string(forKey: AppPreferences.modelPathKey), url.path)
            }
            try writePublicModel(to: url)
            let restored = MnemonicAssistantModel(defaults: defaults, preferenceDomain: name)
            defer { restored.terminate() }
            XCTAssertTrue(restored.hasModel)
            XCTAssertNil(restored.error)
        }
    }

    func testSelectionAndStartupRejectLinksCloudLocationsAndInvalidHeaders() throws {
        try withLocation { directory, defaults, name in
            let valid = directory.appendingPathComponent("valid.gguf")
            try writePublicModel(to: valid)
            let linked = directory.appendingPathComponent("link.gguf")
            try FileManager.default.createSymbolicLink(at: linked, withDestinationURL: valid)
            let linkedDirectory = directory.appendingPathComponent("linked-directory")
            try FileManager.default.createSymbolicLink(at: linkedDirectory, withDestinationURL: directory)
            let cloud = directory.appendingPathComponent("Library/CloudStorage/Provider/model.gguf")
            let mobile = directory.appendingPathComponent("Library/Mobile Documents/Provider/model.gguf")
            let wrongExtension = directory.appendingPathComponent("model.txt")
            let uppercase = directory.appendingPathComponent("model.GGUF")
            let control = directory.appendingPathComponent("control\nmodel.gguf")
            for url in [cloud, mobile, wrongExtension, uppercase, control] { try writePublicModel(to: url) }
            let wrongMagic = directory.appendingPathComponent("wrong-magic.gguf")
            try Data(repeating: 0, count: 24).write(to: wrongMagic)
            let truncated = directory.appendingPathComponent("truncated.gguf")
            try Data("GGUF".utf8).write(to: truncated)
            let fifo = directory.appendingPathComponent("pipe.gguf")
            XCTAssertEqual(mkfifo(fifo.path, 0o600), 0)
            for unsafe in [linked, linkedDirectory.appendingPathComponent("valid.gguf"), cloud, mobile,
                           wrongExtension, uppercase, control, wrongMagic, truncated, fifo, directory] {
                defaults.removeObject(forKey: AppPreferences.modelPathKey)
                let selected = MnemonicAssistantModel(defaults: defaults, preferenceDomain: name)
                XCTAssertFalse(selected.useModel(at: unsafe), unsafe.path)
                XCTAssertFalse(selected.hasModel)
                XCTAssertNil(defaults.object(forKey: AppPreferences.modelPathKey))
                selected.terminate()
                defaults.set(unsafe.path, forKey: AppPreferences.modelPathKey)
                let restored = MnemonicAssistantModel(defaults: defaults, preferenceDomain: name)
                XCTAssertFalse(restored.hasModel, unsafe.path)
                XCTAssertNotNil(restored.error)
                restored.terminate()
            }
            let remote = try XCTUnwrap(URL(string: "file://remote.invalid" + valid.path))
            let model = MnemonicAssistantModel(defaults: defaults, preferenceDomain: name)
            defer { model.terminate() }
            XCTAssertFalse(model.useModel(at: remote))
            XCTAssertFalse(model.hasModel)
        }
    }

    func testMalformedSavedPathsAreRemovedInsteadOfBecomingCapabilities() throws {
        try withLocation { _, defaults, name in
            for value: Any in ["relative/model.gguf", "/invalid\0/model.gguf", "/invalid\n/model.gguf",
                              "/" + String(repeating: "a", count: 4_096), 42, Data("PUBLIC BOOKMARK".utf8)] {
                defaults.set(value, forKey: AppPreferences.modelPathKey)
                let model = MnemonicAssistantModel(defaults: defaults, preferenceDomain: name)
                XCTAssertFalse(model.hasModel)
                XCTAssertNotNil(model.error)
                XCTAssertNil(defaults.object(forKey: AppPreferences.modelPathKey))
                model.terminate()
            }
        }
    }

    func testFailedSelectionCannotReplacePreviouslyValidatedModelLocation() throws {
        try withLocation { directory, defaults, name in
            let valid = directory.appendingPathComponent("valid.gguf")
            try writePublicModel(to: valid)
            let model = MnemonicAssistantModel(defaults: defaults, preferenceDomain: name)
            defer { model.terminate() }
            XCTAssertTrue(model.useModel(at: valid))
            XCTAssertFalse(model.useModel(at: directory.appendingPathComponent("missing.gguf")))
            XCTAssertTrue(model.hasModel)
            XCTAssertEqual(model.modelName, valid.lastPathComponent)
            XCTAssertEqual(defaults.string(forKey: AppPreferences.modelPathKey), valid.path)
        }
    }

    func testGenerationRechecksModelAfterFileWasReplacedBySymlink() throws {
        try withLocation { directory, defaults, name in
            let selected = directory.appendingPathComponent("selected.gguf")
            let replacement = directory.appendingPathComponent("replacement.gguf")
            try writePublicModel(to: selected)
            try writePublicModel(to: replacement)
            let model = MnemonicAssistantModel(defaults: defaults, preferenceDomain: name)
            defer { model.terminate() }
            XCTAssertTrue(model.useModel(at: selected))
            model.setPhrase("abandon ability about")
            XCTAssertTrue(model.canGenerate)
            try FileManager.default.removeItem(at: selected)
            try FileManager.default.createSymbolicLink(at: selected, withDestinationURL: replacement)
            model.generate()
            XCTAssertFalse(model.hasModel)
            XCTAssertFalse(model.isRunning)
            XCTAssertFalse(model.hasStory)
            XCTAssertTrue(model.error?.contains("no longer safely available locally") ?? false)
            XCTAssertEqual(defaults.string(forKey: AppPreferences.modelPathKey), selected.path)
        }
    }
}
