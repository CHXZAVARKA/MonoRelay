import AppKit
import SwiftUI

struct StreamPreset: Decodable, Identifiable {
    let id: String
    let title: String
    let detail: String
    let maxSize: Int
    let fps: Int
    let bitRateMbps: Int
    let videoBufferMs: Int
    let audioBufferMs: Int

    static let catalog: [StreamPreset] = {
        guard let url = Bundle.main.url(forResource: "stream-presets", withExtension: "json"),
              let data = try? Data(contentsOf: url),
              let presets = try? JSONDecoder().decode([StreamPreset].self, from: data)
        else { return [] }
        return presets
    }()
}

@MainActor
final class Session: ObservableObject {
    enum Phase: Equatable {
        case idle, searching, starting, streaming, setup(SetupStep), failed(String)
    }

    @Published private(set) var phase = Phase.idle
    @Published private(set) var selectedPreset: String
    private let preferences: UserDefaults
    private var launched = false
    private var job: Task<Void, Never>?
    private var player: Process?
    private var runID = UUID()
    private var restartForPreset = false
    private var adaptive = AdaptiveQuality()

    private var activePreset: StreamPreset? {
        let presets = StreamPreset.catalog
        if selectedPreset == AdaptiveQuality.id {
            return presets.indices.contains(adaptive.index) ? presets[adaptive.index] : nil
        }
        return presets.first { $0.id == selectedPreset }
    }

    init(preferences: UserDefaults = .standard) {
        self.preferences = preferences
        let saved = preferences.string(forKey: "streamPreset")
        selectedPreset = StreamPreset.catalog.first { $0.id == saved }?.id ?? AdaptiveQuality.id
    }

    func selectPreset(_ id: String) {
        guard id != selectedPreset,
              id == AdaptiveQuality.id || StreamPreset.catalog.contains(where: { $0.id == id }) else { return }
        adaptive = AdaptiveQuality()
        selectedPreset = id
        preferences.set(id, forKey: "streamPreset")
        restartPlayer()
    }

    private func restartPlayer() {
        guard !restartForPreset, let player else { return }
        restartForPreset = true
        phase = .starting
        if player.isRunning { player.terminate() }
    }

    func startAtLaunch() {
        guard !launched else { return }
        launched = true
        start()
    }

    func start(manual: Endpoint? = nil, canReconnect: Bool = true) {
        adaptive = AdaptiveQuality()
        connect(manual: manual, canReconnect: canReconnect)
    }

    private func connect(manual: Endpoint? = nil, canReconnect: Bool = true) {
        stop()
        guard let tools = Toolchain.find() else {
            phase = .failed("Install scrcpy and Android Platform Tools, then retry.")
            return
        }
        let id = runID
        let known = preferences.string(forKey: "headsetSerial")
        let history = Array((preferences.stringArray(forKey: "addresses") ?? []).prefix(8))
        phase = .searching
        job = Task.detached(priority: .userInitiated) { [weak self] in
            let result = QuestSearch(tools: tools).find(
                remembered: known, history: history, manual: manual, passes: canReconnect ? 3 : 7
            ) {
                Task.isCancelled
            }
            guard !Task.isCancelled else { return }
            await self?.finish(result, tools: tools, id: id, canReconnect: canReconnect)
        }
    }

    func stop() {
        restartForPreset = false
        runID = UUID()
        job?.cancel()
        job = nil
        if let player, player.isRunning { player.terminate() }
        player = nil
        phase = .idle
    }

    private func finish(_ result: SearchResult, tools: Toolchain, id: UUID, canReconnect: Bool) {
        guard id == runID else { return }
        job = nil
        switch result {
        case .setup(let step):
            phase = .setup(step)
        case .unavailable(let message):
            phase = .failed(message)
        case .found(let headset):
            remember(headset)
            launchPlayer(headset, tools: tools, id: id, canReconnect: canReconnect)
        }
    }

    private func launchPlayer(_ headset: Headset, tools: Toolchain, id: UUID, canReconnect: Bool) {
        guard let preset = activePreset else {
            phase = .failed("Stream profiles are missing. Reinstall MonoRelay.")
            return
        }
        var arguments = [
            "--serial=\(headset.transport)", "--max-size=\(preset.maxSize)", "--max-fps=\(preset.fps)",
            "--video-bit-rate=\(preset.bitRateMbps)M", "--video-buffer=\(preset.videoBufferMs)",
            "--no-control", "--hwdec=disabled", "--pause-on-exit=false",
            "--window-title=MonoRelay · Stream", "--window-width=880", "--window-height=495",
        ]
        if selectedPreset == AdaptiveQuality.id {
            adaptive.beginStream()
            arguments.append("--print-fps")
        }
        if let crop = headset.crop {
            arguments += ["--crop=\(crop)", "--render-fit=stretched"]
        }
        arguments += ["--audio-source=playback", "--audio-dup", "--audio-buffer=\(preset.audioBufferMs)"]

        let process = Process()
        let output = Pipe()
        let reader = StreamOutput()
        process.executableURL = tools.scrcpy
        process.arguments = arguments
        var environment = tools.environment
        // macOS libc keeps stdout buffered on a pipe unless told to flush each line.
        environment["STDBUF1"] = "L"
        process.environment = environment
        process.standardOutput = output
        process.standardError = output
        phase = .starting
        output.fileHandleForReading.readabilityHandler = { [weak self] file in
            let data = file.availableData
            if data.isEmpty {
                file.readabilityHandler = nil
            } else {
                let events = reader.consume(data)
                if !events.isEmpty {
                    Task { @MainActor [weak self] in self?.handleOutput(events, id: id, cap: preset.fps) }
                }
            }
        }
        process.terminationHandler = { [weak self] ended in
            let code = ended.terminationStatus
            Task { @MainActor [weak self] in
                self?.playerExited(code, id: id, canReconnect: canReconnect)
            }
        }
        do {
            try process.run()
            player = process
        } catch {
            output.fileHandleForReading.readabilityHandler = nil
            phase = .failed("Could not start scrcpy. Check your installation.")
        }
    }

    private func remember(_ headset: Headset) {
        preferences.set(headset.serial, forKey: "headsetSerial")
        if let target = headset.address?.value {
            let previous = preferences.stringArray(forKey: "addresses") ?? []
            preferences.set(Array(([target] + previous.filter { $0 != target }).prefix(8)), forKey: "addresses")
        }
    }

    private func handleOutput(_ events: [StreamOutput.Event], id: UUID, cap: Int) {
        guard runID == id, !restartForPreset, let player, player.isRunning else { return }
        for event in events {
            switch event {
            case .ready:
                phase = .streaming
            case .frames(let rendered, let skipped):
                guard selectedPreset == AdaptiveQuality.id, phase == .streaming else { continue }
                guard NSWorkspace.shared.frontmostApplication?.processIdentifier == player.processIdentifier else {
                    adaptive.beginStream()
                    continue
                }
                if adaptive.sample(rendered: rendered, skipped: skipped, cap: cap,
                                   lastIndex: StreamPreset.catalog.count - 1) {
                    restartPlayer()
                    return
                }
            }
        }
    }

    private func playerExited(_ code: Int32, id: UUID, canReconnect: Bool) {
        guard runID == id else { return }
        player = nil
        if restartForPreset {
            connect()
        } else if code == 2 && canReconnect {
            connect(canReconnect: false)
        } else if code == 0 {
            stop()
        } else {
            phase = .failed(code == 2
                ? "Lost the connection to Quest. Check Wi-Fi and retry."
                : "Could not start the stream. Keep Quest awake and check that scrcpy 5.0.1 or later is installed.")
        }
    }
}

private final class StreamOutput {
    enum Event {
        case ready
        case frames(rendered: Int, skipped: Int)
    }

    private var tail = ""

    func consume(_ bytes: Data) -> [Event] {
        tail += String(decoding: bytes, as: UTF8.self)
        var lines = tail.components(separatedBy: "\n")
        tail = String((lines.popLast() ?? "").suffix(1024))
        return lines.compactMap { line in
            if line.hasPrefix("INFO: Texture:") { return .ready }
            let fields = line.split(whereSeparator: \.isWhitespace)
            guard fields.count >= 3, fields[0] == "INFO:", fields[2] == "fps",
                  let rendered = Int(fields[1]), (0...1000).contains(rendered) else { return nil }
            if fields.count == 3 { return .frames(rendered: rendered, skipped: 0) }
            guard fields.count == 6, fields[3].hasPrefix("(+"),
                  fields[4] == "frames", fields[5] == "skipped)",
                  let skipped = Int(fields[3].dropFirst(2)), (0...1000).contains(skipped) else { return nil }
            return .frames(rendered: rendered, skipped: skipped)
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    weak var session: Session?

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) { session?.stop() }
}

@main
struct MonoRelayApp: App {
    @StateObject private var session = Session()
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        Window("MonoRelay", id: "home") {
            HomeView(session: session)
                .onAppear {
                    delegate.session = session
                    session.startAtLaunch()
                }
        }
        .windowResizability(.contentSize)
    }
}

struct HomeView: View {
    @ObservedObject var session: Session
    @State private var enteringAddress = false
    @State private var address = ""
    @FocusState private var addressFocused: Bool
    @Environment(\.colorScheme) private var colorScheme

    private let accent = Color(red: 0.03, green: 0.38, blue: 0.84)
    private var surface: Color {
        colorScheme == .dark ? Color(red: 0.094, green: 0.102, blue: 0.125) : Color(red: 0.988, green: 0.988, blue: 0.992)
    }
    private var controlFill: Color {
        colorScheme == .dark ? .white.opacity(0.07) : Color(red: 0.945, green: 0.953, blue: 0.97)
    }

    var body: some View {
        VStack(spacing: 24) {
            VStack(spacing: 8) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable().interpolation(.high).frame(width: 80, height: 80)
                    .accessibilityHidden(true)
                Text("MonoRelay")
                    .font(.system(size: 34, weight: .semibold))
                    .accessibilityAddTraits(.isHeader)
            }
            status
            VStack(spacing: 8) {
                HStack(spacing: 2) {
                    segment("Auto", id: AdaptiveQuality.id)
                    ForEach(StreamPreset.catalog) { preset in
                        segment(preset.title, id: preset.id)
                    }
                }
                .padding(3)
                .background(controlFill, in: RoundedRectangle(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.primary.opacity(0.10)))
                .accessibilityElement(children: .contain)
                .accessibilityLabel("Stream quality")
                Text(qualityDetail)
                    .font(.callout).foregroundStyle(.secondary)
            }
            Divider()
            connectionActions
        }
        .padding(28)
        .frame(width: 460)
        .background(surface)
    }

    private var status: some View {
        VStack(spacing: 6) {
            HStack(spacing: 8) {
                if isBusy {
                    ProgressView().controlSize(.small).accessibilityHidden(true)
                } else {
                    Circle()
                        .fill(session.phase == .streaming ? Color.green : (failed ? accent : Color.secondary))
                        .frame(width: 8, height: 8)
                        .accessibilityHidden(true)
                }
                Text(statusText)
                    .font(.system(size: 15, weight: .medium))
                    .fixedSize(horizontal: false, vertical: true)
            }
            if case .setup(let step) = session.phase {
                Text(step.instruction).font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if failed {
                Link("Setup guide", destination: URL(string: "https://github.com/CHXZAVARKA/MonoRelay/blob/main/SETUP.md")!)
                    .font(.callout)
            }
        }
        .frame(maxWidth: .infinity)
        .multilineTextAlignment(.center)
    }

    private func segment(_ title: String, id: String) -> some View {
        let selected = session.selectedPreset == id
        return Button {
            session.selectPreset(id)
        } label: {
            Text(title)
                .font(.system(size: 13, weight: selected ? .semibold : .medium))
                .frame(maxWidth: .infinity, minHeight: 32)
        }
        .buttonStyle(RelayButtonStyle(fill: selected ? accent : .clear, filled: selected, radius: 9))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private var qualityDetail: String {
        StreamPreset.catalog.first { $0.id == session.selectedPreset }?.detail ?? "Adjusts quality as you watch"
    }

    private var connectionActions: some View {
        HStack(spacing: 12) {
            if enteringAddress {
                TextField("Quest IP address", text: $address, prompt: Text("Quest IP address"))
                    .textFieldStyle(.plain)
                    .padding(.horizontal, 12).frame(height: 36)
                    .background(controlFill, in: RoundedRectangle(cornerRadius: 10))
                    .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.primary.opacity(0.12)))
                    .focused($addressFocused)
                Button("Cancel") { enteringAddress = false }
                    .keyboardShortcut(.cancelAction)
            } else if failed {
                Button("Enter IP") {
                    enteringAddress = true
                    addressFocused = true
                }
                .buttonStyle(RelayButtonStyle(fill: controlFill, filled: false, radius: 10))
            }
            Spacer(minLength: 0)
            if isBusy || session.phase == .streaming {
                Button("Stop") { session.stop() }
                    .keyboardShortcut(.cancelAction)
                    .buttonStyle(RelayButtonStyle(fill: controlFill, filled: false, radius: 10))
            } else {
                Button(failed && !enteringAddress ? "Retry" : "Connect", action: connect)
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(RelayButtonStyle(fill: accent, filled: true, radius: 10))
                    .disabled(enteringAddress && Endpoint(address) == nil)
            }
        }
    }

    private func connect() {
        let endpoint = enteringAddress ? Endpoint(address) : nil
        if enteringAddress && endpoint == nil { return }
        enteringAddress = false
        session.start(manual: endpoint)
    }

    private var isBusy: Bool { session.phase == .searching || session.phase == .starting }

    private var failed: Bool {
        switch session.phase {
        case .setup, .failed: true
        default: false
        }
    }

    private var statusText: String {
        switch session.phase {
        case .idle: "Ready to connect"
        case .searching: "Looking for Quest…"
        case .starting: "Waiting for video…"
        case .streaming: "Streaming"
        case .setup(let step): step.title
        case .failed(let message): message
        }
    }
}

private struct RelayButtonStyle: ButtonStyle {
    var fill: Color
    var filled: Bool
    var radius: CGFloat
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold))
            .padding(.horizontal, 14)
            .frame(minHeight: radius == 9 ? 32 : 36)
            .foregroundStyle(filled ? Color.white : Color.primary)
            .background(fill, in: RoundedRectangle(cornerRadius: radius))
            .contentShape(RoundedRectangle(cornerRadius: radius))
            .opacity(!enabled ? 0.48 : configuration.isPressed ? 0.76 : 1)
    }
}
