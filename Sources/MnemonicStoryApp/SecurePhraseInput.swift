import AppKit
import SwiftUI

/// No undo, spell checking, substitutions, service menu, drag export or clipboard export.
/// AppKit still owns transient NSString/rendering copies, explicitly covered by the audit limit.
struct SecurePhraseInput: NSViewRepresentable {
    @ObservedObject var model: MnemonicAssistantModel
    func makeCoordinator() -> Coordinator { Coordinator(model: model) }
    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSScrollView()
        let text = PhraseTextView(frame: .zero)
        text.delegate = context.coordinator
        text.isRichText = false; text.importsGraphics = false; text.allowsUndo = false
        text.isContinuousSpellCheckingEnabled = false; text.isGrammarCheckingEnabled = false
        text.isAutomaticSpellingCorrectionEnabled = false; text.isAutomaticTextCompletionEnabled = false
        text.isAutomaticQuoteSubstitutionEnabled = false; text.isAutomaticDashSubstitutionEnabled = false
        text.isAutomaticLinkDetectionEnabled = false; text.isAutomaticDataDetectionEnabled = false
        text.isAutomaticTextReplacementEnabled = false
        if #available(macOS 15.0, *) {
            text.writingToolsBehavior = .none
            text.allowedWritingToolsResultOptions = []
        }
        text.font = .monospacedSystemFont(ofSize: 15, weight: .regular)
        text.textColor = .white; text.backgroundColor = NSColor(calibratedWhite: 0.07, alpha: 1)
        text.insertionPointColor = .white; text.textContainerInset = NSSize(width: 12, height: 12)
        text.isVerticallyResizable = true; text.isHorizontallyResizable = false
        text.autoresizingMask = [.width]
        text.textContainer?.widthTracksTextView = true
        text.textContainer?.containerSize = NSSize(width: scroll.contentSize.width, height: .greatestFiniteMagnitude)
        scroll.documentView = text; scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        context.coordinator.revision = model.inputRevision
        model.attachInputView(text)
        return scroll
    }
    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let text = scroll.documentView as? NSTextView else { return }
        model.attachInputView(text)
        if context.coordinator.revision != model.inputRevision {
            context.coordinator.revision = model.inputRevision
            PhraseInputStorage.clear(text)
        }
        text.isHidden = !model.isInputVisible
        text.isEditable = model.isInputVisible && !model.isRunning
        text.isSelectable = model.isInputVisible && !model.isRunning
        text.setAccessibilityElement(model.isInputVisible)
        if !model.isInputVisible { text.window?.makeFirstResponder(nil) }
    }
    static func dismantleNSView(_ scroll: NSScrollView, coordinator: Coordinator) {
        guard let text = scroll.documentView as? NSTextView else { return }
        PhraseInputStorage.clear(text)
        coordinator.model.detachInputView(text)
        text.delegate = nil
    }
    @MainActor final class Coordinator: NSObject, NSTextViewDelegate {
        let model: MnemonicAssistantModel
        var revision = 0
        init(model: MnemonicAssistantModel) { self.model = model }
        func textDidChange(_ notification: Notification) {
            guard let text = notification.object as? NSTextView else { return }
            model.setPhrase(text.string)
        }
        func textView(_ textView: NSTextView, shouldChangeTextIn affectedCharRange: NSRange, replacementString: String?) -> Bool {
            guard let replacementString else { return false }
            return textView.string.utf8.count + replacementString.utf8.count <= 8_192 + affectedCharRange.length
        }
    }
}

@MainActor enum PhraseInputStorage {
    static func clear(_ text: NSTextView) {
        // Clear synchronously on session clear/quit, even if SwiftUI never updates again.
        // NSString, rendering and allocator copies outside this live storage are not erasable here.
        let count = text.textStorage?.length ?? 0
        text.textStorage?.replaceCharacters(in: NSRange(location: 0, length: count), with: String(repeating: " ", count: count))
        text.string = ""
        text.undoManager?.removeAllActions()
        text.setSelectedRange(NSRange(location: 0, length: 0))
    }
}

@MainActor private final class PhraseTextView: NSTextView {
    override func copy(_ sender: Any?) { }
    override func cut(_ sender: Any?) { }
    override func writeSelection(to pboard: NSPasteboard, type: NSPasteboard.PasteboardType) -> Bool { false }
    override func validRequestor(forSendType sendType: NSPasteboard.PasteboardType?, returnType: NSPasteboard.PasteboardType?) -> Any? { nil }
    override func menu(for event: NSEvent) -> NSMenu? { nil }
    override func paste(_ sender: Any?) {
        guard let text = NSPasteboard.general.string(forType: .string), text.utf8.count <= 8_192 else { return }
        insertText(text, replacementRange: selectedRange())
    }
}
