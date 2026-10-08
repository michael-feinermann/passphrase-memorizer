import Foundation

/// Frameworks may add dialog bookmarks and complete window frames to the app
/// domain. Retain only our four explicitly allowed preference keys. The user
/// allows the model location to persist; credentials and generated text do not.
enum AppPreferences {
    static let domain = "local.passphrasereminder.reminder"
    static let modelPathKey = "modelPath"
    static let retainedKeys = Set(["preferredLanguage", "windowWidth", "windowHeight", modelPathKey])

    static func purgeUnexpected(in defaults: UserDefaults = .standard, domainName: String = domain) {
        guard let existing = defaults.persistentDomain(forName: domainName) else { return }
        let retained = existing.filter { retainedKeys.contains($0.key) }
        guard retained.count != existing.count else { return }
        // Touch this app's named domain only, never global or other-app defaults.
        defaults.setPersistentDomain(retained, forName: domainName)
    }
}
