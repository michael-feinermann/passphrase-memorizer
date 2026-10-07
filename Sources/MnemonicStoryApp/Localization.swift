import Foundation

enum AppLanguage: String, CaseIterable, Identifiable, Sendable {
    case english = "en", german = "de"
    var id: String { rawValue }
    var displayName: String { self == .english ? "English" : "Deutsch" }
    func text(_ german: String, _ english: String) -> String { self == .english ? english : german }
}

enum MnemonicStyle: String, CaseIterable, Identifiable, Sendable {
    case rhyme, ballad, poem, shortStory, rap
    var id: String { rawValue }
    var promptName: String {
        switch self { case .rhyme: "rhyme"; case .ballad: "ballad"; case .poem: "poem"; case .shortStory: "short story"; case .rap: "rap" }
    }
}
