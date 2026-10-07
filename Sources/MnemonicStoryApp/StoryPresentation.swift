import SwiftUI

/// Literal text only: generated Markdown, links and other markup are never parsed.
/// This transient presentation is constructed only while the story is visible.
enum StoryPresentation {
    static func attributed(_ text: String, fontSize: CGFloat) -> AttributedString {
        var result = AttributedString(text)
        result.font = .system(size: fontSize, design: .rounded)
        var cursor = result.startIndex
        while cursor < result.endIndex,
              let opening = result[cursor...].characters.firstIndex(of: "["),
              let closing = result[opening...].characters.firstIndex(of: "]") {
            let end = result.characters.index(after: closing)
            result[opening..<end].foregroundColor = .blue
            result[opening..<end].font = .system(size: fontSize, weight: .bold, design: .rounded)
            cursor = end
        }
        return result
    }
}
