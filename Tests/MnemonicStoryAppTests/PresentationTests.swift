import SwiftUI
import XCTest
@testable import MnemonicStoryApp

final class PresentationTests: XCTestCase {
    func testLiteralMarkersIncludingDuplicatesHaveBlueBoldAttributes() {
        let text = "A [Abandon] and [about] remember [Abandon]."
        let presentation = StoryPresentation.attributed(text, fontSize: 23)
        XCTAssertEqual(String(presentation.characters), text)
        var highlighted: [String] = []
        for run in presentation.runs {
            let fragment = String(presentation[run.range].characters)
            if run.foregroundColor == .blue {
                highlighted.append(fragment)
                XCTAssertEqual(run.font, .system(size: 23, weight: .bold, design: .rounded))
            } else {
                XCTAssertEqual(run.font, .system(size: 23, design: .rounded))
            }
            XCTAssertNil(run.link)
        }
        XCTAssertEqual(highlighted, ["[Abandon]", "[about]", "[Abandon]"])
    }

    func testGeneratedMarkdownIsDisplayedLiterallyWithoutLinksOrMarkupParsing() {
        let text = "**literal** _text_ <tag> ![](https://example.invalid) with [about]."
        let presentation = StoryPresentation.attributed(text, fontSize: 16)
        XCTAssertEqual(String(presentation.characters), text)
        for run in presentation.runs {
            XCTAssertNil(run.link)
            XCTAssertNil(run.inlinePresentationIntent)
        }
    }
}
