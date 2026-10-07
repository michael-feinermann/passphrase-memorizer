import AppKit
import Darwin
import SwiftUI

@main struct MnemonicStoryApplication: App {
    @NSApplicationDelegateAdaptor(LifecycleDelegate.self) private var delegate
    @StateObject private var model = MnemonicAssistantModel()
    var body: some Scene {
        WindowGroup(ProductIdentity.title) {
            MnemonicAssistantView(model: model)
                .background(PrivacyWindow(model: model))
                .frame(minWidth: 820, minHeight: 740)
                .onAppear { delegate.model = model }
        }
        .defaultSize(width: 940, height: 880)
        .windowResizability(.contentMinSize)
        .commands {
            CommandGroup(replacing: .newItem) { }
            CommandGroup(replacing: .undoRedo) { }
            CommandMenu(ProductIdentity.name) {
                Button(model.language.text("Sitzung leeren", "Clear session")) { model.clear() }
                    .keyboardShortcut("k", modifiers: [.command, .shift])
                Button(model.language.text("Texte verdecken", "Conceal texts")) { model.conceal() }
                    .keyboardShortcut("h", modifiers: [.command, .shift])
            }
        }
    }
}

@MainActor final class LifecycleDelegate: NSObject, NSApplicationDelegate {
    weak var model: MnemonicAssistantModel?
    private var shutdownRequested = false
    private var shutdownCompleted = false
    func applicationDidFinishLaunching(_ notification: Notification) {
        AppPreferences.purgeUnexpected()
        _ = RuntimeGuard.disableCoreDumps()
        signal(SIGPIPE, SIG_IGN)
        NSApplication.shared.disableRelaunchOnLogin()
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.screensDidSleepNotification, NSWorkspace.sessionDidResignActiveNotification] {
            NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(conceal), name: name, object: nil)
        }
    }
    @objc private func conceal() { model?.conceal() }
    func applicationWillResignActive(_ notification: Notification) { model?.conceal() }
    func applicationWillHide(_ notification: Notification) { model?.conceal() }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if shutdownCompleted { return .terminateNow }
        if !shutdownRequested {
            shutdownRequested = true
            model?.terminate()
            InferenceLifecycle.shared.shutdown { [weak self] in
                Task { @MainActor [weak self] in
                    self?.shutdownCompleted = true
                    AppPreferences.purgeUnexpected()
                    NSApplication.shared.reply(toApplicationShouldTerminate: true)
                }
            }
        }
        return .terminateLater
    }
    func applicationWillTerminate(_ notification: Notification) { model?.terminate() }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool { false }
}

struct PrivacyWindow: NSViewRepresentable {
    let model: MnemonicAssistantModel
    func makeCoordinator() -> Coordinator { Coordinator(model: model) }
    func makeNSView(context: Context) -> NSView { let view = NSView(); attach(view, coordinator: context.coordinator); return view }
    func updateNSView(_ view: NSView, context: Context) { attach(view, coordinator: context.coordinator) }
    private func attach(_ view: NSView, coordinator: Coordinator) {
        DispatchQueue.main.async { if let window = view.window { coordinator.attach(window) } }
    }
    @MainActor final class Coordinator: NSObject {
        weak var window: NSWindow?
        let model: MnemonicAssistantModel
        init(model: MnemonicAssistantModel) { self.model = model }
        func attach(_ value: NSWindow) {
            guard window !== value else { return }
            window = value
            value.title = ProductIdentity.title
            _ = value.setFrameAutosaveName("")
            AppPreferences.purgeUnexpected()
            value.sharingType = .none; value.isRestorable = false; value.disableSnapshotRestoration()
            value.hidesOnDeactivate = true; value.tabbingMode = .disallowed
            let defaults = UserDefaults.standard
            let width = defaults.double(forKey: "windowWidth"), height = defaults.double(forKey: "windowHeight")
            if (820...4_096).contains(width), (740...4_096).contains(height) { value.setContentSize(NSSize(width: width, height: height)) }
            NotificationCenter.default.addObserver(self, selector: #selector(resized), name: NSWindow.didResizeNotification, object: value)
            NotificationCenter.default.addObserver(self, selector: #selector(closed), name: NSWindow.willCloseNotification, object: value)
        }
        @objc private func resized() {
            guard let size = window?.contentView?.bounds.size else { return }
            UserDefaults.standard.set(size.width, forKey: "windowWidth")
            UserDefaults.standard.set(size.height, forKey: "windowHeight")
            _ = window?.setFrameAutosaveName("")
            AppPreferences.purgeUnexpected()
        }
        @objc private func closed() { model.terminate() }
        deinit { NotificationCenter.default.removeObserver(self) }
    }
}
