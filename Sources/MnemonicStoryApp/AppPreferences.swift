import Foundation

/// Frameworks may add dialog bookmarks and complete window frames to the app
/// domain. Retain only our three explicit, non-secret preference keys.
enum AppPreferences {
    static let domain = "local.passphrasereminder.reminder"
    static let retainedKeys = Set(["preferredLanguage", "windowWidth", "windowHeight"])

    static func purgeUnexpected(in defaults: UserDefaults = .standard, domainName: String = domain) {
        guard let existing = defaults.persistentDomain(forName: domainName) else { return }
        let retained = existing.filter { retainedKeys.contains($0.key) }
        guard retained.count != existing.count else { return }
        // Touch this app's named domain only, never global or other-app defaults.
        defaults.setPersistentDomain(retained, forName: domainName)
    }
}
