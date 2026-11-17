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
    let caption: String

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
    @Published var showAddress = false
    @Published var address = ""
    @Published private(set) var selectedPreset: String
    let preferences: UserDefaults
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

    var qualityDetail: String {
        guard let preset = activePreset else { return "" }
        if selectedPreset != AdaptiveQuality.id { return preset.caption }
        return phase == .streaming ? "Сейчас: \(preset.title)" : "Подбирает качество"
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
        if preferences.object(forKey: "singleEye") == nil || preferences.bool(forKey: "singleEye"),
           let crop = headset.crop {
            arguments += ["--crop=\(crop)", "--render-fit=stretched"]
        }
        if preferences.object(forKey: "playbackAudio") == nil || preferences.bool(forKey: "playbackAudio") {
            arguments += ["--audio-source=playback", "--audio-dup", "--audio-buffer=\(preset.audioBufferMs)"]
        } else {
            arguments += ["--no-audio"]
        }

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
        .commands {
            CommandGroup(after: .newItem) {
                Button("Остановить трансляцию") { session.stop() }
                    .keyboardShortcut(".", modifiers: [.command])
                    .disabled(session.phase == .idle)
            }
        }
        Settings { PreferencesView() }
    }
}

struct HomeView: View {
    @ObservedObject var session: Session

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(alignment: .top, spacing: 13) {
                Image(systemName: "display")
                    .font(.system(size: 27, weight: .medium))
                    .symbolRenderingMode(.hierarchical)
                    .foregroundStyle(Color.accentColor)
                    .accessibilityHidden(true)
                    .frame(width: 36)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Трансляция Quest")
                        .font(.title2.bold())
                        .accessibilityAddTraits(.isHeader)
                }
            }

            Divider()

            HStack(alignment: .top, spacing: 10) {
                if isBusy {
                    ProgressView().controlSize(.small).accessibilityHidden(true)
                } else {
                    Image(systemName: statusIcon)
                        .foregroundStyle(session.phase == .streaming ? Color.green : Color.secondary)
                        .accessibilityHidden(true)
                }
                VStack(alignment: .leading, spacing: 5) {
                    Text(statusTitle).font(.headline)
                    Text(statusDetail)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 59, alignment: .topLeading)
            .accessibilityElement(children: .combine)

            VStack(alignment: .leading, spacing: 6) {
                Picker("Режим трансляции", selection: Binding(
                    get: { session.selectedPreset }, set: { session.selectPreset($0) }
                )) {
                    Text("Авто").tag(AdaptiveQuality.id)
                    ForEach(StreamPreset.catalog) { preset in
                        Text(preset.title).tag(preset.id)
                    }
                }
                .labelsHidden()
                .pickerStyle(.segmented)
                Text(session.qualityDetail).font(.caption).foregroundStyle(.secondary)
            }

            HStack(spacing: 10) {
                Button("Ввести IP…") { session.showAddress = true }
                    .disabled(isBusy || session.phase == .streaming)
                Spacer()
                if isBusy {
                    Button("Отменить") { session.stop() }.keyboardShortcut(.cancelAction)
                } else if session.phase == .streaming {
                    Button("Остановить") { session.stop() }
                } else {
                    Button("Подключить") { session.start() }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                }
            }
        }
        .padding(24)
        .frame(width: 420)
        .sheet(isPresented: $session.showAddress) {
            AddressSheet(address: $session.address) { endpoint in
                session.showAddress = false
                session.start(manual: endpoint)
            } cancel: {
                session.showAddress = false
            }
        }
    }

    private var isBusy: Bool { session.phase == .searching || session.phase == .starting }

    private var statusIcon: String {
        if case .failed = session.phase { return "exclamationmark.circle" }
        return session.phase == .streaming ? "checkmark.circle.fill" : "wifi"
    }

    private var statusTitle: String {
        switch session.phase {
        case .idle: "Готово к подключению"
        case .searching: "Ищем Quest…"
        case .starting: "Ждём видео…"
        case .streaming: "Трансляция идёт"
        case .failed: "Не удалось подключиться"
        }
    }

    private var statusDetail: String {
        switch session.phase {
        case .idle: "Подключи Mac и Quest к одной сети."
        case .searching: "Quest должен быть включён."
        case .starting: "Если очки спят, разбуди их."
        case .streaming: "Изображение открыто в отдельном окне."
        case .failed(let message): message
        }
    }
}

struct AddressSheet: View {
    @Binding var address: String
    @FocusState private var focused: Bool
    let connect: (Endpoint) -> Void
    let cancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 13) {
            Text("Подключиться по адресу").font(.headline)
            Text("Возьми IP из настроек Wi‑Fi в очках. Если отладка показывает порт, добавь его после двоеточия.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            TextField("IP-адрес Quest", text: $address, prompt: Text("192.168.1.42"))
                .focused($focused)
            HStack {
                Spacer()
                Button("Отмена", action: cancel).keyboardShortcut(.cancelAction)
                Button("Подключить") {
                    if let endpoint = Endpoint(address) { connect(endpoint) }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(Endpoint(address) == nil)
            }
        }
        .padding(24)
        .frame(width: 365)
        .onAppear { focused = true }
    }
}

struct PreferencesView: View {
    @AppStorage("singleEye") private var singleEye = true
    @AppStorage("playbackAudio") private var playbackAudio = true

    var body: some View {
        Form {
            Toggle("Показывать изображение одного глаза", isOn: $singleEye)
            Toggle("Воспроизводить звук на Mac", isOn: $playbackAudio)
            Text("Применится при следующем подключении.")
                .foregroundStyle(.secondary)
        }
        .padding(22)
        .frame(width: 375)
    }
}
