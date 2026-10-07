import XCTest
import AppKit
@testable import MnemonicStoryApp
import MnemonicStoryCore

@MainActor final class SessionTests: XCTestCase {
    private func withModel(_ body: (MnemonicAssistantModel, UserDefaults, String) throws -> Void) throws {
        let name = "local.mnemonicstory.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let model = MnemonicAssistantModel(defaults: defaults)
        defer { model.terminate() }
        try body(model, defaults, name)
    }
    func testEnglishDefaultAndOnlyExplicitLanguagePersists() throws {
        try withModel { model, defaults, name in
            XCTAssertEqual(model.language, .english)
            XCTAssertTrue((defaults.persistentDomain(forName: name) ?? [:]).isEmpty)
            model.language = .german
            model.setPhrase("abandon about ability")
            model.style = .rap
            model.clear()
            let second = MnemonicAssistantModel(defaults: defaults)
            defer { second.terminate() }
            XCTAssertEqual(second.language, .german)
            XCTAssertFalse(second.hasPhrase)
            XCTAssertFalse(second.hasModel)
            XCTAssertFalse(second.hasStory)
            XCTAssertEqual(second.style, .shortStory)
            XCTAssertEqual(defaults.persistentDomain(forName: name) as? [String: String], ["preferredLanguage": "de"])
        }
    }
    func testClearAndTerminationInvalidateSessionAndPreventReuse() throws {
        try withModel { model, _, _ in
            model.setPhrase("abandon about ability")
            XCTAssertTrue(model.hasPhrase)
            XCTAssertEqual(model.wordCount, 3)
            model.conceal()
            XCTAssertFalse(model.isInputVisible)
            model.clear()
            XCTAssertFalse(model.hasPhrase)
            XCTAssertEqual(model.wordCount, 0)
            XCTAssertEqual(model.inputRevision, 1)
            model.setPhrase("abandon about")
            model.terminate()
            model.setPhrase("abandon")
            XCTAssertFalse(model.hasPhrase)
            XCTAssertFalse(model.canGenerate)
        }
    }
    func testInvalidReplacementAndOversizeCannotReusePreviousValidPhrase() throws {
        try withModel { model, _, _ in
            model.setPhrase("abandon about")
            XCTAssertTrue(model.hasPhrase)
            model.setPhrase("INVALID_UNKNOWN_WORD")
            XCTAssertFalse(model.hasPhrase)
            XCTAssertNotNil(model.validationMessage)
            model.setPhrase("abandon about")
            model.setPhrase(String(repeating: "a", count: 8193))
            XCTAssertFalse(model.hasPhrase)
            XCTAssertEqual(model.wordCount, 0)
            XCTAssertNotNil(model.validationMessage)
        }
    }
    func testChangingListRevalidatesAndValidationLanguageUpdates() throws {
        try withModel { model, _, _ in
            model.setPhrase("abacus")
            XCTAssertFalse(model.hasPhrase)
            let englishError = model.validationMessage
            model.language = .german
            XCTAssertNotEqual(model.validationMessage, englishError)
            model.format = .eff
            XCTAssertTrue(model.hasPhrase)
            XCTAssertNil(model.validationMessage)
        }
    }
    func testInvalidPreferenceFallsBackToEnglish() throws {
        try withModel { _, defaults, _ in
            defaults.set("fr", forKey: "preferredLanguage")
            let model = MnemonicAssistantModel(defaults: defaults)
            defer { model.terminate() }
            XCTAssertEqual(model.language, .english)
        }
    }
    func testClearAndQuitSynchronouslyEraseMountedTextStorageWithoutSwiftUIUpdate() throws {
        try withModel { model, _, _ in
            let view = NSTextView(frame: .zero)
            view.allowsUndo = false
            model.attachInputView(view)
            let storage = try XCTUnwrap(view.textStorage)
            view.string = "abandon about ability"
            model.setPhrase(view.string)
            model.clear()
            XCTAssertEqual(view.string, "")
            XCTAssertEqual(storage.length, 0)
            XCTAssertFalse(view.undoManager?.canUndo ?? false)
            view.string = "abandon about"
            model.setPhrase(view.string)
            model.terminate()
            XCTAssertEqual(view.string, "")
            XCTAssertEqual(storage.length, 0)
            XCTAssertFalse(model.hasPhrase)
            model.detachInputView(view)
        }
    }
}
