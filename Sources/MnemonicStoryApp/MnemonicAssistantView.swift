import AppKit
import MnemonicStoryCore
import SwiftUI

/// No plaintext input or generated output is retained in SwiftUI state.
/// The AppKit input remains mounted while concealment covers its rendering.
struct MnemonicAssistantView: View {
    @ObservedObject var model: MnemonicAssistantModel
    @State private var showRevealConfirmation = false
    @State private var showSecurityDetails = false

    var body: some View {
        GeometryReader { geometry in
            let metrics = StoryDisplayMetrics(viewportSize: geometry.size)
            ZStack {
                StoryPalette.background.ignoresSafeArea()
                ScrollView {
                    VStack(spacing: 20 * metrics.scale) {
                        header
                        isolationStatus
                        phraseSection
                        modelSection
                        if model.hasStory { storySection }
                        securityDetails
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.horizontal, 30 * metrics.scale)
                    .padding(.vertical, 28 * metrics.scale)
                }
            }
            .environment(\.storyDisplayMetrics, metrics)
            .font(.system(size: 14 * metrics.scale, design: .rounded))
        }
        .controlSize(.large)
        .preferredColorScheme(.dark)
        .confirmationDialog(
            tr("Merkhilfe vorübergehend anzeigen?", "Reveal the memory aid temporarily?"),
            isPresented: $showRevealConfirmation,
            titleVisibility: .visible
        ) {
            Button(tr("Für 10 Minuten anzeigen", "Reveal for 10 minutes")) { model.reveal() }
            Button(tr("Abbrechen", "Cancel"), role: .cancel) { }
        } message: {
            Text(tr(
                "Die Merkhilfe enthält deine geheimen Wörter. Beende vorher Bildschirmfreigaben, Aufnahmen und Fernwartung. macOS garantiert keinen Schutz gegen Bildschirmaufnahmen.",
                "The memory aid contains your secret words. Stop screen sharing, recording, and remote support first. macOS does not guarantee protection against screen capture."
            ))
        }
    }

    private var header: some View {
        HStack(spacing: 16) {
            Image(nsImage: NSApplication.shared.applicationIconImage)
                .resizable()
                .scaledToFit()
                .frame(width: 58, height: 58)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                Text(ProductIdentity.title)
                    .storyFont(size: 29, weight: .bold)
                    .foregroundStyle(.white)
                Text(tr(
                    "Private Merkhilfen für BIP39 und EFF. Lokal auf deinem Mac.",
                    "Private memory aids for BIP39 and EFF. Locally on your Mac."
                ))
                .storyFont()
                .foregroundStyle(StoryPalette.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 8)
            languageSwitcher
        }
    }

    private var languageSwitcher: some View {
        HStack(spacing: 3) {
            ForEach(AppLanguage.allCases, id: \.rawValue) { language in
                Button { model.language = language } label: {
                    Text(language == .english ? "EN" : "DE")
                        .storyFont(weight: .bold)
                        .foregroundStyle(model.language == language ? .white : StoryPalette.mutedText)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 6)
                        .background(
                            model.language == language ? StoryPalette.teal.opacity(0.18) : .clear,
                            in: RoundedRectangle(cornerRadius: 7)
                        )
                }
                .buttonStyle(.plain)
                .disabled(model.isRunning)
                .accessibilityLabel(language == .english ? "English" : "Deutsch")
                .accessibilityAddTraits(model.language == language ? .isSelected : [])
            }
        }
        .padding(3)
        .background(StoryPalette.panelStrong, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(StoryPalette.border, lineWidth: 1))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(tr("Sprache", "Language"))
    }

    private var isolationStatus: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: "lock.shield.fill")
                .storyFont(size: 25, weight: .semibold)
                .foregroundStyle(StoryPalette.teal)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 6) {
                Text(tr("Abgesicherte Offline-Ausführung", "Protected offline execution"))
                    .storyFont(weight: .semibold)
                    .foregroundStyle(StoryPalette.teal)
                Text(tr(
                    "Isolierter nativer Prozess mit Metal-GPU-Beschleunigung und CPU-Ausweichpfad. Netzwerkzugriff und Dateischreiben sind gesperrt. Unabhängig von anderen KI-Apps, ohne Cloudkonto.",
                    "Isolated native process with Metal GPU acceleration and CPU fallback. Network access and file writes are blocked. Independent of other AI apps, with no cloud account."
                ))
                .storyFont()
                .foregroundStyle(StoryPalette.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(StoryPalette.teal.opacity(0.055), in: RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(StoryPalette.teal.opacity(0.18), lineWidth: 1))
    }

    private var phraseSection: some View {
        StorySectionCard(
            step: "01",
            title: tr("Geheime Wörter eingeben", "Enter your secret words"),
            subtitle: tr("Die Wörter bleiben ausschließlich in dieser Sitzung.", "The words stay in this session only.")
        ) {
            VStack(alignment: .leading, spacing: 12) {
                Picker(tr("Wortliste", "Word list"), selection: $model.format) {
                    Text("BIP39").tag(PhraseFormat.bip39)
                    Text("EFF").tag(PhraseFormat.eff)
                }
                .pickerStyle(.segmented)
                .disabled(model.isRunning)
                Text(model.format == .bip39
                     ? tr("1 bis 128 Wörter aus der englischen BIP39-Wortliste. Keine Prüfung von Wallet-Gültigkeit oder Prüfsumme.", "1 to 128 words from the English BIP39 word list. Wallet validity and checksum are not checked.")
                     : tr("1 bis 128 Wörter aus der EFF Large Wordlist. Leerzeichen und Bindestriche sind als Trennzeichen erlaubt.", "1 to 128 words from the EFF Large Wordlist. Spaces and hyphens are accepted as separators."))
                    .storyFont()
                    .foregroundStyle(StoryPalette.secondaryText)
                    .fixedSize(horizontal: false, vertical: true)
                ZStack {
                    SecurePhraseInput(model: model)
                        .disabled(model.isRunning || !model.isInputVisible)
                        .opacity(model.isInputVisible ? 1 : 0)
                        .allowsHitTesting(model.isInputVisible && !model.isRunning)
                        .accessibilityHidden(!model.isInputVisible)
                    if !model.isInputVisible {
                        VStack(spacing: 10) {
                            Label(tr("Eingabe verdeckt", "Input hidden"), systemImage: "eye.slash")
                                .storyFont(weight: .semibold)
                                .foregroundStyle(StoryPalette.secondaryText)
                            Button(action: model.toggleInputVisibility) {
                                Text(tr("Eingabe anzeigen", "Reveal input"))
                            }
                            .buttonStyle(StoryQuietButtonStyle())
                            .disabled(model.isRunning)
                        }
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .background(StoryPalette.wordChip)
                    }
                }
                .frame(height: 150)
                .clipShape(RoundedRectangle(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).stroke(StoryPalette.border, lineWidth: 1))
                .privacySensitive()
                HStack(alignment: .top, spacing: 12) {
                    VStack(alignment: .leading, spacing: 5) {
                        Text(tr("\(model.wordCount) Wörter", "\(model.wordCount) words"))
                            .storyFont(weight: .semibold)
                            .monospacedDigit()
                        if let validation = model.validationMessage {
                            Text(validation)
                                .storyFont()
                                .foregroundStyle(StoryPalette.amber)
                                .fixedSize(horizontal: false, vertical: true)
                        } else if !model.hasPhrase {
                            Text(tr("Wörter durch Leerzeichen oder Zeilenumbrüche trennen.", "Separate words with spaces or line breaks."))
                                .storyFont()
                                .foregroundStyle(StoryPalette.secondaryText)
                        }
                    }
                    Spacer(minLength: 4)
                    Button(action: model.toggleInputVisibility) {
                        Label(
                            model.isInputVisible ? tr("Verdecken", "Hide") : tr("Anzeigen", "Reveal"),
                            systemImage: model.isInputVisible ? "eye.slash" : "eye"
                        )
                    }
                    .buttonStyle(StoryQuietButtonStyle())
                    .disabled(model.isRunning)
                    clearSessionButton
                }
            }
        }
    }

    private var modelSection: some View {
        StorySectionCard(
            step: "02",
            title: tr("Merkhilfe lokal erzeugen", "Create a memory aid locally"),
            subtitle: tr("Gemma 4 E4B oder ein kompatibles GGUF-Modell.", "Gemma 4 E4B or a compatible GGUF model.")
        ) {
            VStack(alignment: .leading, spacing: 16) {
                modelControls
                styleControls
                generationControls
                if let error = model.error {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .storyFont()
                        .foregroundStyle(StoryPalette.amber)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    private var modelControls: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(tr("Lokales Modell", "Local model"))
                    .storyFont(weight: .semibold)
                Text(model.modelName ?? tr("Noch kein Modell ausgewählt", "No model selected yet"))
                    .storyFont()
                    .foregroundStyle(StoryPalette.secondaryText)
                    .lineLimit(2)
                    .truncationMode(.middle)
                Text(tr("Die Auswahl gilt nur für diese Sitzung.", "Selection lasts for this session only."))
                    .storyFont()
                    .foregroundStyle(StoryPalette.mutedText)
            }
            Spacer(minLength: 4)
            Button(action: model.selectModel) {
                Label(model.hasModel ? tr("Modell wechseln", "Change model") : tr("GGUF auswählen", "Choose GGUF"), systemImage: "folder")
            }
            .buttonStyle(StoryQuietButtonStyle())
            .disabled(model.isRunning)
        }
    }

    private var styleControls: some View {
        VStack(alignment: .leading, spacing: 8) {
            Picker(tr("Form der Merkhilfe", "Memory aid format"), selection: $model.style) {
                ForEach(MnemonicStyle.allCases, id: \.rawValue) { style in
                    Text(styleTitle(style)).tag(style)
                }
            }
            .pickerStyle(.segmented)
            .disabled(model.isRunning)
            Text(tr(
                "Ausgabe auf \(languageName). Die ursprünglichen englischen Wörter bleiben unverändert.",
                "Output in \(languageName). The original English words remain unchanged."
            ))
            .storyFont()
            .foregroundStyle(StoryPalette.secondaryText)
            .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var generationControls: some View {
        HStack(alignment: .center, spacing: 12) {
            Group {
                if model.isRunning {
                    HStack(spacing: 10) {
                        ProgressView().controlSize(.small)
                        Text(tr("Merkhilfe wird lokal erzeugt…", "Creating the memory aid locally…"))
                    }
                } else {
                    Text(tr("Jede Generierung beginnt mit einem leeren KI-Kontext.", "Each generation starts with an empty AI context."))
                }
            }
            .storyFont()
            .foregroundStyle(StoryPalette.secondaryText)
            .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 4)
            if model.isRunning {
                Button(action: model.cancel) {
                    Label(tr("Abbrechen", "Cancel"), systemImage: "stop.fill")
                }
                .buttonStyle(StoryDangerButtonStyle())
            } else {
                Button(action: model.generate) {
                    Label(model.hasStory ? tr("Neu erzeugen", "Generate again") : tr("Merkhilfe erzeugen", "Create memory aid"), systemImage: "text.book.closed")
                        .storyFont(weight: .bold)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 11)
                }
                .buttonStyle(StoryPrimaryButtonStyle())
                .disabled(!model.canGenerate)
            }
        }
    }

    private var storySection: some View {
        StorySectionCard(
            step: "03",
            title: tr("Deine Merkhilfe", "Your memory aid"),
            subtitle: tr("Behandle die Merkhilfe wie das ursprüngliche Geheimnis.", "Treat the memory aid like the original secret.")
        ) {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 12) {
                    Label(
                        model.isStoryVisible ? tr("Merkhilfe angezeigt", "Memory aid visible") : tr("Merkhilfe verdeckt", "Memory aid hidden"),
                        systemImage: model.isStoryVisible ? "eye" : "eye.slash"
                    )
                    .storyFont(weight: .semibold)
                    .foregroundStyle(model.isStoryVisible ? StoryPalette.amber : StoryPalette.secondaryText)
                    Spacer(minLength: 4)
                    Button {
                        if model.isStoryVisible { model.conceal() }
                        else { showRevealConfirmation = true }
                    } label: {
                        Label(model.isStoryVisible ? tr("Verdecken", "Hide") : tr("Anzeigen", "Reveal"), systemImage: model.isStoryVisible ? "eye.slash" : "eye")
                    }
                    .buttonStyle(StoryQuietButtonStyle())
                    clearSessionButton
                }
                if model.isStoryVisible {
                    ScrollView {
                        VisibleStoryText(text: model.storyText)
                            .lineSpacing(5)
                            .textSelection(.disabled)
                            .fixedSize(horizontal: false, vertical: true)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(16)
                    }
                    .frame(height: 260)
                    .background(StoryPalette.wordChip, in: RoundedRectangle(cornerRadius: 12))
                    .privacySensitive()
                } else {
                    Text(tr(
                        "Die Merkhilfe enthält geheime Wörter und wird erst nach deiner Bestätigung angezeigt.",
                        "The memory aid contains secret words and appears only after you confirm reveal."
                    ))
                    .storyFont()
                    .foregroundStyle(StoryPalette.secondaryText)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(16)
                    .background(StoryPalette.wordChip, in: RoundedRectangle(cornerRadius: 12))
                }
                Text(tr(
                    "Die Merkhilfe ersetzt kein sicheres Backup. Prüfe Wörter und Reihenfolge an deiner ursprünglichen Eingabe.",
                    "The memory aid does not replace a secure backup. Check the words and their order against your original input."
                ))
                .storyFont()
                .foregroundStyle(StoryPalette.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var clearSessionButton: some View {
        Button(action: model.clear) {
            Label(tr("Sitzung leeren", "Clear session"), systemImage: "trash")
        }
        .buttonStyle(StoryDangerButtonStyle())
    }

    private var securityDetails: some View {
        VStack(alignment: .leading, spacing: 9) {
            Button { showSecurityDetails.toggle() } label: {
                Label(tr("Sitzung und Löschung", "Session and clearing"), systemImage: showSecurityDetails ? "chevron.up" : "chevron.down")
                    .storyFont(weight: .semibold)
                    .foregroundStyle(StoryPalette.secondaryText)
            }
            .buttonStyle(.plain)
            Text(tr(
                "Kein gespeicherter Chatverlauf. Leeren und reguläres Beenden löschen die Eingabe und die KI-Sitzung. Die Anzeige wird bei Deaktivierung verdeckt.",
                "No saved chat history. Clearing and quitting normally erase the input and AI session. The display is concealed when the app deactivates."
            ))
            .storyFont()
            .foregroundStyle(StoryPalette.secondaryText)
            .fixedSize(horizontal: false, vertical: true)
            if showSecurityDetails {
                Text(tr(
                    "Die App überschreibt ihre eigenen geheimen Puffer und beendet den isolierten KI-Prozess. Bereits von Swift, macOS oder der Anzeige erzeugte Kopien lassen sich nicht garantiert unwiderruflich löschen. Erzwungenes Beenden kann die Bereinigung verhindern. Gespeichert werden ausschließlich die gewählte App-Sprache und die Fenstergröße.",
                    "The app overwrites its owned secret buffers and terminates the isolated AI process. Copies already created by Swift, macOS, or rendering cannot be guaranteed to be erased irrevocably. Forced termination may prevent cleanup. Only the selected app language and window size are saved."
                ))
                .storyFont()
                .foregroundStyle(StoryPalette.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var languageName: String { model.language == .english ? "English" : "Deutsch" }

    private func styleTitle(_ style: MnemonicStyle) -> String {
        switch style {
        case .rhyme: tr("Reim", "Rhyme")
        case .ballad: tr("Ballade", "Ballad")
        case .poem: tr("Gedicht", "Poem")
        case .shortStory: tr("Kurzgeschichte", "Short story")
        case .rap: tr("Rap", "Rap")
        }
    }

    private func tr(_ german: String, _ english: String) -> String { model.language.text(german, english) }
}

private struct VisibleStoryText: View {
    let text: String
    @Environment(\.storyDisplayMetrics) private var metrics
    var body: some View {
        Text(StoryPresentation.attributed(text, fontSize: 16 * metrics.scale))
    }
}

private struct StoryDisplayMetrics {
    let viewportSize: CGSize
    var scale: CGFloat {
        guard viewportSize.width.isFinite, viewportSize.height.isFinite else { return 1 }
        return sqrt(max(1, viewportSize.width / 1060)) * sqrt(max(1, viewportSize.height / 840))
    }
}

private struct StoryDisplayMetricsKey: EnvironmentKey {
    static let defaultValue = StoryDisplayMetrics(viewportSize: CGSize(width: 1060, height: 840))
}

private extension EnvironmentValues {
    var storyDisplayMetrics: StoryDisplayMetrics {
        get { self[StoryDisplayMetricsKey.self] }
        set { self[StoryDisplayMetricsKey.self] = newValue }
    }
}

private struct StoryFontModifier: ViewModifier {
    @Environment(\.storyDisplayMetrics) private var metrics
    let size: CGFloat
    let weight: Font.Weight
    func body(content: Content) -> some View {
        content.font(.system(size: max(14, size) * metrics.scale, weight: weight, design: .rounded))
    }
}

private extension View {
    func storyFont(size: CGFloat = 14, weight: Font.Weight = .regular) -> some View {
        modifier(StoryFontModifier(size: size, weight: weight))
    }
}

private struct StorySectionCard<Content: View>: View {
    let step: String
    let title: String
    let subtitle: String
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(alignment: .top, spacing: 13) {
                Text(step)
                    .storyFont(weight: .heavy)
                    .foregroundStyle(StoryPalette.teal)
                    .padding(.horizontal, 9)
                    .padding(.vertical, 6)
                    .background(StoryPalette.teal.opacity(0.10), in: RoundedRectangle(cornerRadius: 8))
                VStack(alignment: .leading, spacing: 3) {
                    Text(title).storyFont(size: 18, weight: .bold).foregroundStyle(.white)
                    Text(subtitle).storyFont().foregroundStyle(StoryPalette.secondaryText)
                }
                Spacer(minLength: 0)
            }
            content
        }
        .padding(22)
        .background(StoryPalette.panel, in: RoundedRectangle(cornerRadius: 23, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 23, style: .continuous).stroke(StoryPalette.border, lineWidth: 1))
        .shadow(color: .black.opacity(0.16), radius: 24, y: 10)
    }
}

private struct StoryPrimaryButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .foregroundStyle(.white)
            .background(
                LinearGradient(colors: [StoryPalette.teal.opacity(0.92), StoryPalette.blue], startPoint: .leading, endPoint: .trailing),
                in: RoundedRectangle(cornerRadius: 13)
            )
            .shadow(color: StoryPalette.teal.opacity(0.18), radius: 12, y: 5)
            .saturation(isEnabled ? 1 : 0.28)
            .opacity(isEnabled ? (configuration.isPressed ? 0.86 : 1) : 0.38)
    }
}

private struct StoryQuietButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .storyFont(weight: .bold)
            .foregroundStyle(.white)
            .padding(.horizontal, 13)
            .padding(.vertical, 9)
            .background(StoryPalette.panelStrong, in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(StoryPalette.border, lineWidth: 1))
            .opacity(isEnabled ? (configuration.isPressed ? 0.72 : 1) : 0.38)
    }
}

private struct StoryDangerButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .storyFont(weight: .bold)
            .foregroundStyle(Color(red: 1.0, green: 0.62, blue: 0.62))
            .padding(.horizontal, 13)
            .padding(.vertical, 9)
            .background(Color.red.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.red.opacity(0.20), lineWidth: 1))
            .opacity(isEnabled ? (configuration.isPressed ? 0.70 : 1) : 0.38)
    }
}

private enum StoryPalette {
    static let background = Color(red: 0.025, green: 0.042, blue: 0.070)
    static let panel = Color(red: 0.055, green: 0.078, blue: 0.112).opacity(0.96)
    static let panelStrong = Color(red: 0.075, green: 0.100, blue: 0.138)
    static let wordChip = Color(red: 0.067, green: 0.096, blue: 0.130)
    static let teal = Color(red: 0.23, green: 0.88, blue: 0.77)
    static let blue = Color(red: 0.25, green: 0.48, blue: 0.95)
    static let amber = Color(red: 1.0, green: 0.72, blue: 0.30)
    static let secondaryText = Color.white.opacity(0.66)
    static let mutedText = Color.white.opacity(0.56)
    static let border = Color.white.opacity(0.085)
}
