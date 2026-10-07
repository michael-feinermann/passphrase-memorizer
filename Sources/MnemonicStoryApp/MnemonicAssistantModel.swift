import AppKit
import Darwin
import Foundation
import MnemonicStoryCore
import Security
import UniformTypeIdentifiers

@MainActor
final class MnemonicAssistantModel: ObservableObject {
    @Published var language: AppLanguage { didSet { defaults.set(language.rawValue, forKey: "preferredLanguage"); purgePanelNavigationPreferences(); clearStory(); revalidate() } }
    @Published var format: PhraseFormat = .bip39 { didSet { revalidate(); clearStory() } }
    @Published var style: MnemonicStyle = .shortStory
    @Published private(set) var isRunning = false
    @Published private(set) var isStoryVisible = false
    @Published private(set) var isInputVisible = true
    @Published private(set) var error: String?
    @Published private(set) var inputRevision = 0
    @Published private(set) var wordCount = 0
    @Published private(set) var modelName: String?
    @Published private(set) var hasStory = false
    @Published private(set) var validationMessage: String?
    private let defaults: UserDefaults
    private let preferenceDomain: String?
    private let lexicon: PhraseLexicon?
    private var phrase: ValidatedPhrase?
    private var rawInput: SecretBuffer?
    private weak var mountedInput: NSTextView?
    private var story: SecretBuffer?
    private var modelURL: URL?
    private var activeRequest: InferenceRequest?
    private var runTask: Task<Void, Never>?
    private var revealTimer: Task<Void, Never>?
    private var generationID = UUID()
    private var terminated = false
    static let storyRevealDuration: Duration = .seconds(600)

    init(defaults: UserDefaults = .standard, preferenceDomain: String? = nil) {
        self.defaults = defaults
        self.preferenceDomain = preferenceDomain ?? (defaults === UserDefaults.standard ? AppPreferences.domain : nil)
        if let domain = self.preferenceDomain { AppPreferences.purgeUnexpected(in: defaults, domainName: domain) }
        language = defaults.string(forKey: "preferredLanguage").flatMap(AppLanguage.init(rawValue:)) ?? .english
        lexicon = try? PhraseLexicon()
        if lexicon == nil { error = language.text("Wortlistenprüfung fehlgeschlagen.", "Word-list verification failed.") }
    }
    var hasPhrase: Bool { phrase != nil && phrase?.storage.isCleared == false }
    var hasModel: Bool { modelURL != nil }
    var canGenerate: Bool { hasPhrase && hasModel && !isRunning && !terminated }
    var storyText: String { isStoryVisible ? story?.displayText() ?? "" : "" }

    func attachInputView(_ view: NSTextView) { mountedInput = view }
    func detachInputView(_ view: NSTextView) {
        if mountedInput === view { mountedInput = nil }
    }

    func setPhrase(_ text: String) {
        guard !terminated else { return }
        clearStory()
        guard text.utf8.count <= 8_192 else {
            rawInput?.clear(); rawInput = nil; phrase?.clear(); phrase = nil; wordCount = 0
            validationMessage = language.text("Die Eingabe ist zu lang.", "The input is too long."); return
        }
        rawInput?.clear(); rawInput = text.isEmpty ? nil : SecretBuffer(text)
        revalidate()
    }
    private func revalidate() {
        phrase?.clear(); phrase = nil; wordCount = 0; validationMessage = nil
        guard let rawInput, !rawInput.isCleared, let lexicon else { return }
        do {
            phrase = try lexicon.parse(rawInput.displayText(), format: format)
            wordCount = phrase?.wordCount ?? 0
        } catch {
            validationMessage = language.text("Gib 1 bis 128 Wörter aus der gewählten englischen Wortliste ein. EFF erlaubt Leerzeichen und Bindestriche.", "Enter 1 to 128 words from the selected English word list. EFF accepts spaces and hyphens.")
        }
    }
    func selectModel() {
        guard !terminated else { return }
        cancel()
        let panel = NSOpenPanel()
        _ = panel.setFrameAutosaveName("")
        panel.allowedContentTypes = [UTType(filenameExtension: "gguf") ?? .data]
        panel.canChooseDirectories = false; panel.allowsMultipleSelection = false
        panel.title = language.text("Lokales GGUF-Modell auswählen", "Choose local GGUF model")
        defer { purgePanelNavigationPreferences() }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let keys: Set<URLResourceKey> = [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey, .volumeIsLocalKey, .isUbiquitousItemKey]
        var fileInfo = stat()
        let canonical = url.resolvingSymlinksInPath()
        let blockedProvider = canonical.path.contains("/Library/CloudStorage/") || canonical.path.contains("/Library/Mobile Documents/")
        guard let value = try? url.resourceValues(forKeys: keys), value.isRegularFile == true,
              value.isSymbolicLink != true, value.volumeIsLocal == true,
              value.isUbiquitousItem != true, !blockedProvider,
              lstat(url.path, &fileInfo) == 0, (fileInfo.st_flags & UInt32(SF_DATALESS)) == 0,
              (value.fileSize ?? 0) > 4, url.pathExtension.lowercased() == "gguf" else {
            error = language.text("Wähle eine reguläre GGUF-Datei auf einem lokalen Datenträger.", "Choose a regular GGUF file on a local disk."); return
        }
        modelURL = url; modelName = url.lastPathComponent; error = nil
    }
    private func purgePanelNavigationPreferences() {
        // Includes NSNav*, NSOSP* bookmarks, GoToSheet and SwiftUI frame keys.
        // Finder/File Provider/global OS metadata remains outside our control.
        if let preferenceDomain { AppPreferences.purgeUnexpected(in: defaults, domainName: preferenceDomain) }
    }
    func generate() {
        guard canGenerate, let phrase, let modelURL else { return }
        guard RuntimeGuard.current(), let executable = runnerURL(), RuntimeGuard.validateRunner(executable) else {
            error = language.text("Signatur oder gesicherte Laufzeitprüfung fehlgeschlagen. Starte das signierte App-Bundle ohne Debugger.", "Signature or secure runtime verification failed. Start the signed app bundle without a debugger."); return
        }
        clearStory()
        guard let prompt = phrase.prompt(style: style.promptName, language: language == .english ? "English" : "German") else { return }
        guard let completion = InferenceLifecycle.shared.registerCompletion() else { prompt.clear(); return }
        let id = UUID(); generationID = id
        let request = InferenceRequest(prompt: prompt); activeRequest = request
        isRunning = true; error = nil; isInputVisible = false
        let selectedLanguage = language
        runTask = Task { [weak self] in
            defer { completion.complete() }
            let result = await Task.detached(priority: .userInitiated) { () -> Result<SecretBuffer, InferenceError> in
                do { return .success(try request.run(executable: executable, model: modelURL)) }
                catch { return .failure((error as? InferenceError) ?? .inference) }
            }.value
            guard let self, self.generationID == id, !self.terminated else {
                if case let .success(buffer) = result { buffer.clear() }; return
            }
            request.cancel()
            self.isRunning = false; self.activeRequest = nil; self.runTask = nil
            switch result {
            case let .success(buffer):
                guard phrase.verifyStory(buffer) else {
                    buffer.clear()
                    self.error = selectedLanguage.text("Die Ausgabe erfüllt die Merkhilfeprüfung nicht. Merkhilfetext und alle markierten Wörter in der richtigen Reihenfolge sind erforderlich. Versuche es erneut oder wähle eine andere Textform.", "The output does not pass the memory aid check. It must include memory aid text and every marked word in the correct order. Try again or choose another style."); return
                }
                self.story = buffer; self.hasStory = true; self.isStoryVisible = false
            case .failure:
                self.error = selectedLanguage.text("Die lokale Generierung wurde sicher abgebrochen. Prüfe Modell, Arbeitsspeicher und Laufzeit. Es wurde kein Verlauf gespeichert.", "Local generation stopped safely. Check the model, available memory and runtime. No history was saved.")
            }
        }
    }
    func cancel() {
        generationID = UUID(); activeRequest?.cancel(); activeRequest = nil
        runTask?.cancel(); runTask = nil; isRunning = false
    }
    private func clearStory() {
        cancel(); revealTimer?.cancel(); revealTimer = nil
        story?.clear(); story = nil; hasStory = false; isStoryVisible = false; error = nil
    }
    func clear() {
        if let mountedInput { PhraseInputStorage.clear(mountedInput) }
        clearStory(); rawInput?.clear(); rawInput = nil; phrase?.clear(); phrase = nil
        wordCount = 0; validationMessage = nil; inputRevision += 1; isInputVisible = true
    }
    func conceal() { isInputVisible = false; isStoryVisible = false; revealTimer?.cancel(); revealTimer = nil }
    func reveal() {
        guard hasStory, !terminated, RuntimeGuard.current() else { return }
        isStoryVisible = true
        revealTimer?.cancel()
        revealTimer = Task { [weak self] in
            try? await Task.sleep(for: Self.storyRevealDuration)
            guard !Task.isCancelled else { return }; self?.conceal()
        }
    }
    func toggleInputVisibility() { isInputVisible.toggle() }
    func terminate() { terminated = true; clear(); modelURL = nil; modelName = nil; purgePanelNavigationPreferences() }
    private func runnerURL() -> URL? {
        let url = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/LocalMnemonicRunner")
        return FileManager.default.isExecutableFile(atPath: url.path) ? url : nil
    }
    deinit { activeRequest?.cancel(); rawInput?.clear(); phrase?.clear(); story?.clear(); revealTimer?.cancel() }
}
