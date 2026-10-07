import Foundation
import XCTest
@testable import MnemonicStoryApp

@MainActor final class PreferenceTests: XCTestCase {
    private func withDomain(_ body: (UserDefaults, String) throws -> Void) throws {
        let name = "local.passphrasereminder.preferences.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        try body(defaults, name)
    }

    func testFrameworkBookmarksAndAutosavedFramesAreRemovedWhileExplicitPreferencesSurvive() throws {
        try withDomain { defaults, name in
            defaults.setPersistentDomain([
                "preferredLanguage": "de", "windowWidth": 940.0, "windowHeight": 880.0,
                "NSOSPLastRootDirectory": Data("PUBLIC BOOKMARK FIXTURE".utf8),
                "NSNavLastRootDirectory": "PUBLIC DIRECTORY FIXTURE",
                "NSWindow Frame GoToSheet": "PUBLIC FRAME FIXTURE",
                "NSWindow Frame SwiftUI.WindowGroup-AppWindow-1": "PUBLIC FRAME FIXTURE",
                "unexpectedFrameworkSetting": "PUBLIC FIXTURE"
            ], forName: name)
            AppPreferences.purgeUnexpected(in: defaults, domainName: name)
            let result = try XCTUnwrap(defaults.persistentDomain(forName: name))
            XCTAssertEqual(Set(result.keys), ["preferredLanguage", "windowWidth", "windowHeight"])
            XCTAssertEqual(result["preferredLanguage"] as? String, "de")
            XCTAssertEqual(result["windowWidth"] as? Double, 940)
            XCTAssertEqual(result["windowHeight"] as? Double, 880)
        }
    }

    func testPurgingOneDomainDoesNotChangeAnotherDomain() throws {
        try withDomain { defaults, name in
            try withDomain { otherDefaults, otherName in
                defaults.set("PUBLIC CURRENT APP BOOKMARK", forKey: "NSOSPLastRootDirectory")
                otherDefaults.set("PUBLIC OTHER APP BOOKMARK", forKey: "NSOSPLastRootDirectory")
                AppPreferences.purgeUnexpected(in: defaults, domainName: name)
                XCTAssertNil(defaults.persistentDomain(forName: name)?["NSOSPLastRootDirectory"])
                XCTAssertEqual(otherDefaults.persistentDomain(forName: otherName)?["NSOSPLastRootDirectory"] as? String,
                               "PUBLIC OTHER APP BOOKMARK")
            }
        }
    }

    func testModelStartupAndTerminationRemoveFrameworkSessionMetadata() throws {
        try withDomain { defaults, name in
            defaults.set("PUBLIC STALE BOOKMARK", forKey: "NSOSPLastRootDirectory")
            defaults.set("en", forKey: "preferredLanguage")
            let model = MnemonicAssistantModel(defaults: defaults, preferenceDomain: name)
            XCTAssertEqual(model.language, .english)
            XCTAssertNil(defaults.persistentDomain(forName: name)?["NSOSPLastRootDirectory"])
            model.setPhrase("abandon ability about")
            defaults.set("PUBLIC LATE FRAME", forKey: "NSWindow Frame GoToSheet")
            model.terminate()
            XCTAssertEqual(Set(defaults.persistentDomain(forName: name)?.keys ?? Dictionary<String, Any>().keys),
                           ["preferredLanguage"])
            XCTAssertFalse(model.hasPhrase)
        }
    }
}
