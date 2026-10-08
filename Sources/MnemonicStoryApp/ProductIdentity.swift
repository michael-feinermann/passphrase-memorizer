import Foundation

enum ProductIdentity {
    static let name = "Passphrase Memorizer"
    static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0.1"
    }
    static var title: String { "\(name) \(version)" }
}
