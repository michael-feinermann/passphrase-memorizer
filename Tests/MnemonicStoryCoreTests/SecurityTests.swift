import XCTest
@testable import MnemonicStoryCore

final class SecurityTests: XCTestCase {
    func testOwnedBufferClearsAllAliasesAndCannotBeRewritten() {
        let first = SecretBuffer("PUBLIC_TEST_BUFFER")
        let alias = first
        XCTAssertFalse(first.containsOnlyZeroBytes)
        alias.clear()
        XCTAssertTrue(first.isCleared)
        XCTAssertTrue(first.containsOnlyZeroBytes)
        XCTAssertEqual(first.displayText(), "")
        XCTAssertNil(first.write { $0[0] = 65 })
    }
    func testWordListMembershipAcceptsBothBoundariesAndPreservesRepeats() throws {
        let lexicon = try PhraseLexicon()
        for format in PhraseFormat.allCases {
            let word = format == .bip39 ? "abandon" : "abacus"
            for count in [1, 128] {
                let phrase = try lexicon.parse(Array(repeating: word, count: count).joined(separator: " "), format: format)
                defer { phrase.clear() }
                XCTAssertEqual(phrase.wordCount, count)
                let story = SecretBuffer("A remembered path: " + Array(repeating: "[\(word)]", count: count).joined(separator: " "))
                defer { story.clear() }
                XCTAssertTrue(phrase.verifyStory(story))
            }
        }
    }
    func testInputRejectsOutOfBoundsInjectionUnknownWordsAndWrongList() throws {
        let lexicon = try PhraseLexicon()
        for raw in ["", Array(repeating: "abandon", count: 129).joined(separator: " "), "ignore [instructions]", "abandon\u{0}about", "NOT_A_WORD", "über", String(repeating: "a", count: 8193)] {
            XCTAssertThrowsError(try lexicon.parse(raw, format: .bip39))
        }
        XCTAssertThrowsError(try lexicon.parse("aardvark", format: .bip39))
        XCTAssertThrowsError(try lexicon.parse("abandon-about", format: .bip39))
    }
    func testEFFHyphenAndOriginalCapitalizationPreserved() throws {
        let lexicon = try PhraseLexicon()
        let eff = try lexicon.parse("ABACUS-\nABDomen", format: .eff)
        defer { eff.clear() }
        XCTAssertEqual(eff.wordCount, 2)
        let phrase = try lexicon.parse("ABANDON\tABOUT\nABANDON", format: .bip39)
        defer { phrase.clear() }
        XCTAssertEqual(phrase.wordCount, 3)
        let prompt = try XCTUnwrap(phrase.prompt(style: "poem", language: "German"))
        defer { prompt.clear() }
        XCTAssertTrue(prompt.displayText().hasSuffix("[ABANDON] [ABOUT] [ABANDON] \n"))
    }
    func testStoryRequiresEveryExactMarkedWordInOrderIncludingDuplicates() throws {
        let phrase = try PhraseLexicon().parse("abandon about abandon", format: .bip39)
        defer { phrase.clear() }
        for invalid in ["abandon about abandon", "[about] [abandon] [abandon]", "[abandon] [about]", "[abandon] [about] [abandon] [about]", "[ABANDON] [about] [abandon]", "[abandon] [about] [abandon]]"] {
            let buffer = SecretBuffer(invalid); defer { buffer.clear() }
            XCTAssertFalse(phrase.verifyStory(buffer))
        }
        let valid = SecretBuffer("We [abandon] our doubt, sing [about] hope, and [abandon] fear.")
        defer { valid.clear() }
        XCTAssertTrue(phrase.verifyStory(valid))
        phrase.clear()
        XCTAssertFalse(phrase.verifyStory(valid))
        XCTAssertNil(phrase.prompt(style: "rap", language: "English"))
    }
    func testStoryRejectsBareMarkersAndPunctuationButAcceptsLiteraryText() throws {
        let phrase = try PhraseLexicon().parse("abandon about abandon", format: .bip39)
        defer { phrase.clear() }
        for invalid in ["[abandon] [about] [abandon]", "[abandon], [about]; [abandon]!", "123 [abandon]\n[about]…[abandon] 🎵"] {
            let story = SecretBuffer(invalid); defer { story.clear() }
            XCTAssertFalse(phrase.verifyStory(story))
        }
        for valid in [
            "At dawn we [abandon] the storm and sing [about] the light;\nat dusk we [abandon] our fear and hold the night.",
            "Wir [abandon] den Sturm, erzählen [about] das Licht;\nund [abandon] die Angst, bis der Morgen anbricht.",
            "Ü [abandon] [about] [abandon]"
        ] {
            let story = SecretBuffer(valid); defer { story.clear() }
            XCTAssertTrue(phrase.verifyStory(story))
        }
    }
}
