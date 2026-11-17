import AppKit
import SwiftUI

struct StreamPreset: Decodable, Identifiable {
    let id: String
    let title: String
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
        case idle, searching, starting, streaming, failed(String)
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
            phase = .failed("Установи scrcpy и Android Platform Tools, затем попробуй снова.")
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
        case .unavailable(let message):
            phase = .failed(message)
        case .found(let headset):
            remember(headset)
            launchPlayer(headset, tools: tools, id: id, canReconnect: canReconnect)
        }
    }

    private func launchPlayer(_ headset: Headset, tools: Toolchain, id: UUID, canReconnect: Bool) {
        guard let preset = activePreset else {
            phase = .failed("Файл режимов трансляции отсутствует. Переустанови MonoRelay.")
            return
        }
        var arguments = [
            "--serial=\(headset.transport)", "--max-size=\(preset.maxSize)", "--max-fps=\(preset.fps)",
            "--video-bit-rate=\(preset.bitRateMbps)M", "--video-buffer=\(preset.videoBufferMs)",
            "--no-control", "--hwdec=disabled", "--pause-on-exit=false",
            "--window-title=MonoRelay · Трансляция", "--window-width=880", "--window-height=495",
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
            phase = .failed("Не удалось запустить scrcpy. Проверь его установку.")
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
                ? "Связь с Quest прервалась. Проверь Wi‑Fi и подключись снова."
                : "Не удалось открыть трансляцию. Проверь, что Quest активен и стоит scrcpy 5.0.1 или новее.")
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

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Трансляция Quest")
                .font(.title2.bold())
                .accessibilityAddTraits(.isHeader)

            HStack(spacing: 8) {
                if isBusy {
                    ProgressView().controlSize(.small).accessibilityHidden(true)
                } else {
                    Image(systemName: session.phase == .streaming ? "checkmark.circle.fill" : "wifi")
                        .foregroundStyle(session.phase == .streaming ? Color.green : Color.secondary)
                        .accessibilityHidden(true)
                }
                Text(statusText)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary, in: RoundedRectangle(cornerRadius: 8))
            .accessibilityElement(children: .combine)

            Picker("Качество", selection: Binding(
                get: { session.selectedPreset }, set: { session.selectPreset($0) }
            )) {
                Text("Авто").tag(AdaptiveQuality.id)
                ForEach(StreamPreset.catalog) { preset in
                    Text(preset.title).tag(preset.id)
                }
            }
            .labelsHidden()
            .pickerStyle(.segmented)

            if enteringAddress {
                TextField("IP Quest", text: $address, prompt: Text("192.168.1.42"))
                    .textFieldStyle(.roundedBorder)
                    .focused($addressFocused)
            }

            HStack {
                if failed {
                    Button(enteringAddress ? "Скрыть IP" : "Ввести IP") {
                        enteringAddress.toggle()
                        addressFocused = enteringAddress
                    }
                }
                Spacer()
                if isBusy || session.phase == .streaming {
                    Button("Стоп") { session.stop() }.keyboardShortcut(.cancelAction)
                } else {
                    Button(failed && !enteringAddress ? "Повторить" : "Подключить", action: connect)
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                        .disabled(enteringAddress && Endpoint(address) == nil)
                }
            }
        }
        .padding(24)
        .frame(width: 420)
    }

    private func connect() {
        let endpoint = enteringAddress ? Endpoint(address) : nil
        if enteringAddress && endpoint == nil { return }
        enteringAddress = false
        session.start(manual: endpoint)
    }

    private var isBusy: Bool { session.phase == .searching || session.phase == .starting }

    private var failed: Bool {
        if case .failed = session.phase { return true }
        return false
    }

    private var statusText: String {
        switch session.phase {
        case .idle: "Готово к подключению"
        case .searching: "Ищем Quest…"
        case .starting: "Ждём видео…"
        case .streaming: "Трансляция идёт"
        case .failed(let message): message
        }
    }
}
