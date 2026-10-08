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
        restoreModelLocation()
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
        useModel(at: url)
    }

    @discardableResult
    func useModel(at url: URL) -> Bool {
        guard !terminated else { return false }
        guard let localURL = Self.validatedModelURL(url) else {
            error = language.text("Wähle eine vollständig lokale, reguläre GGUF-Datei ohne Verknüpfung oder Cloud-Speicher.", "Choose a fully local, regular GGUF file without links or cloud storage.")
            return false
        }
        cancel()
        modelURL = localURL; modelName = localURL.lastPathComponent; error = nil
        defaults.set(localURL.path, forKey: AppPreferences.modelPathKey)
        purgePanelNavigationPreferences()
        return true
    }

    private func restoreModelLocation() {
        guard let stored = defaults.object(forKey: AppPreferences.modelPathKey) else { return }
        guard let path = stored as? String, path.hasPrefix("/"), path.utf8.count <= 4_096,
              !path.utf8.contains(where: { $0 < 32 || $0 == 127 }) else {
            defaults.removeObject(forKey: AppPreferences.modelPathKey)
            reportUnavailableSavedModel()
            return
        }
        if let url = Self.validatedModelURL(URL(fileURLWithPath: path)) {
            modelURL = url; modelName = url.lastPathComponent
        } else {
            // Keep a valid location for a temporarily disconnected local disk.
            // Each startup validates the file again before enabling generation.
            reportUnavailableSavedModel()
        }
    }

    private func reportUnavailableSavedModel() {
        if error == nil {
            error = language.text("Das zuletzt gewählte Modell ist nicht verfügbar oder nicht mehr sicher lokal. Wähle die GGUF-Datei erneut aus.", "The last selected model is unavailable or is no longer safely local. Choose the GGUF file again.")
        }
    }

    private static func validatedModelURL(_ url: URL) -> URL? {
        guard url.isFileURL, url.host == nil || url.host == "" || url.host == "localhost",
              url.path.hasPrefix("/"), url.path.utf8.count <= 4_096,
              !url.path.utf8.contains(where: { $0 < 32 || $0 == 127 }), url.pathExtension == "gguf" else { return nil }
        let normalized = url.standardizedFileURL
        let canonical = normalized.resolvingSymlinksInPath()
        let path = canonical.path.lowercased()
        guard normalized.path == canonical.path, !path.contains("/library/cloudstorage/"),
              !path.contains("/mobile documents/") else { return nil }
        let keys: Set<URLResourceKey> = [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey, .volumeIsLocalKey, .isUbiquitousItemKey]
        var fileInfo = stat()
        guard let value = try? normalized.resourceValues(forKeys: keys), value.isRegularFile == true,
              value.isSymbolicLink != true, value.volumeIsLocal == true,
              value.isUbiquitousItem != true, lstat(normalized.path, &fileInfo) == 0,
              (fileInfo.st_flags & UInt32(SF_DATALESS)) == 0 else { return nil }
        let descriptor = open(normalized.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard descriptor >= 0 else { return nil }
        defer { close(descriptor) }
        var opened = stat(), current = stat(), filesystem = statfs()
        guard fstat(descriptor, &opened) == 0, (opened.st_mode & S_IFMT) == S_IFREG,
              opened.st_dev == fileInfo.st_dev, opened.st_ino == fileInfo.st_ino,
              opened.st_size >= 24, opened.st_size <= (Int64(32) << 30),
              (opened.st_flags & UInt32(SF_DATALESS)) == 0,
              fstatfs(descriptor, &filesystem) == 0, (filesystem.f_flags & UInt32(MNT_LOCAL)) != 0 else { return nil }
        var magic = [UInt8](repeating: 0, count: 4)
        let readCount = magic.withUnsafeMutableBytes { pread(descriptor, $0.baseAddress, $0.count, 0) }
        guard readCount == magic.count, magic == Array("GGUF".utf8),
              lstat(normalized.path, &current) == 0, current.st_dev == opened.st_dev,
              current.st_ino == opened.st_ino, current.st_size == opened.st_size,
              current.st_flags == opened.st_flags, normalized.resolvingSymlinksInPath().path == normalized.path else { return nil }
        return normalized
    }
    private func purgePanelNavigationPreferences() {
        // Includes NSNav*, NSOSP* bookmarks, GoToSheet and SwiftUI frame keys.
        // Finder/File Provider/global OS metadata remains outside our control.
        if let preferenceDomain { AppPreferences.purgeUnexpected(in: defaults, domainName: preferenceDomain) }
    }
    func generate() {
        guard canGenerate, let phrase, let modelURL else { return }
        guard Self.validatedModelURL(modelURL) != nil else {
            self.modelURL = nil; modelName = nil
            error = language.text("Das gewählte Modell ist nicht mehr sicher lokal verfügbar. Wähle die GGUF-Datei erneut aus.", "The selected model is no longer safely available locally. Choose the GGUF file again.")
            return
        }
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
