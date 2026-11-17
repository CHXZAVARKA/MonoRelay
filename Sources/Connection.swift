import Foundation
import Darwin

struct Endpoint: Equatable {
    let host: String
    let port: Int
    var value: String { "\(host):\(port)" }

    init?(_ input: String) {
        let parts = input.trimmingCharacters(in: .whitespacesAndNewlines)
            .split(separator: ":", omittingEmptySubsequences: false)
        guard (1...2).contains(parts.count) else { return nil }
        let host = String(parts[0])
        var address = in_addr()
        guard inet_pton(AF_INET, host, &address) == 1, let first = host.split(separator: ".").first,
              let octet = Int(first), (1...223).contains(octet), octet != 127 else { return nil }
        guard let port = parts.count == 2 ? Int(parts[1]) : 5555, (1...65535).contains(port) else { return nil }
        self.host = host
        self.port = port
    }

    var isOnLocalNetwork: Bool {
        var address = in_addr()
        guard inet_pton(AF_INET, host, &address) == 1 else { return false }
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0 else { return false }
        defer { if let list { freeifaddrs(list) } }
        var entry = list
        while let current = entry {
            let interface = current.pointee
            entry = interface.ifa_next
            guard String(cString: interface.ifa_name).hasPrefix("en"),
                  interface.ifa_flags & UInt32(IFF_UP) != 0,
                  let ip = interface.ifa_addr, ip.pointee.sa_family == AF_INET,
                  let mask = interface.ifa_netmask else { continue }
            let local = UnsafeRawPointer(ip).assumingMemoryBound(to: sockaddr_in.self).pointee.sin_addr.s_addr
            let bits = UnsafeRawPointer(mask).assumingMemoryBound(to: sockaddr_in.self).pointee.sin_addr.s_addr
            if bits != 0 && local != address.s_addr && local & bits == address.s_addr & bits { return true }
        }
        return false
    }
}

struct Toolchain {
    let adb: URL
    let scrcpy: URL

    static func find() -> Toolchain? {
        let paths = ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin"] +
            (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
        func executable(_ name: String) -> URL? {
            paths.map { URL(fileURLWithPath: $0).appendingPathComponent(name) }
                .first { FileManager.default.isExecutableFile(atPath: $0.path) }
        }
        guard let adb = executable("adb"), let scrcpy = executable("scrcpy") else { return nil }
        return Toolchain(adb: adb, scrcpy: scrcpy)
    }

    var environment: [String: String] {
        var environment = ProcessInfo.processInfo.environment
        let toolPaths = [adb.deletingLastPathComponent().path, scrcpy.deletingLastPathComponent().path]
        environment["PATH"] = (toolPaths + [environment["PATH"] ?? "/usr/bin:/bin"]).joined(separator: ":")
        environment["ADB"] = adb.path
        return environment
    }

    func command(_ arguments: [String], timeout: TimeInterval = 4) -> String {
        let process = Process()
        let output = Pipe()
        process.executableURL = adb
        process.arguments = arguments
        process.environment = environment
        process.standardOutput = output
        process.standardError = output
        do { try process.run() } catch { return "" }

        // These ADB calls produce short replies. Kill only this client on timeout.
        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.04) }
        if process.isRunning {
            kill(process.processIdentifier, SIGKILL)
            process.waitUntilExit()
            return ""
        }
        return String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

struct Headset {
    let transport: String
    let serial: String
    let address: Endpoint?
    let crop: String?
}

enum SetupStep: Equatable {
    case plugIn, approve, enableWiFi

    var title: String {
        switch self {
        case .plugIn: "Подключи Quest по USB"
        case .approve: "Подтверди в очках"
        case .enableWiFi: "Включи ADB по Wi‑Fi"
        }
    }

    var instruction: String {
        switch self {
        case .plugIn: "При первой настройке разреши отладку в очках."
        case .approve: "Разреши отладку и нажми «Повторить»."
        case .enableWiFi: "В Meta VR Developer Hub. Затем вынь кабель и нажми «Повторить»."
        }
    }
}

enum SearchResult {
    case found(Headset)
    case setup(SetupStep)
    case unavailable(String)
}

struct QuestSearch {
    let tools: Toolchain

    private struct Device {
        let transport: String
        let state: String
        let usb: Bool
        let quest: Bool

        init?(_ line: Substring) {
            let words = line.split(whereSeparator: \.isWhitespace)
            guard words.count >= 2, ["device", "offline", "unauthorized"].contains(words[1]) else { return nil }
            transport = String(words[0])
            state = String(words[1])
            usb = words.dropFirst(2).contains { $0.hasPrefix("usb:") }
            quest = words.dropFirst(2).contains { $0.lowercased().hasPrefix("model:quest") }
        }
    }

    private var devices: [Device] {
        tools.command(["devices", "-l"], timeout: 5).split(separator: "\n").compactMap(Device.init)
    }

    private func query(_ transport: String, _ arguments: [String]) -> String {
        tools.command(["-s", transport, "shell"] + arguments)
    }

    private func serial(_ transport: String) -> String? {
        let value = query(transport, ["getprop", "ro.serialno"])
        return value.range(of: #"^[A-Za-z0-9_-]{5,64}$"#, options: .regularExpression) == nil ? nil : value
    }

    private func verify(_ transport: String, expected: String?, address: Endpoint?) -> Headset? {
        guard let serial = serial(transport), expected == nil || expected == serial,
              query(transport, ["getprop", "ro.product.model"]).lowercased().hasPrefix("quest") else { return nil }
        let sizes = query(transport, ["wm", "size"]).split(separator: "\n")
        let dimensions = sizes.last?.split(separator: " ").last?.split(separator: "x")
        var crop: String?
        if let dimensions, dimensions.count == 2, let width = Int(dimensions[0]),
           let height = Int(dimensions[1]), width >= 2, height > 0 {
            crop = (width, height) == (10992, 8000) ? "4800:3750:348:2125" : "\(width / 2):\(height):0:0"
        }
        return Headset(transport: transport, serial: serial, address: address, crop: crop)
    }

    private func advertisedAddresses(for identity: String?) -> [Endpoint] {
        let prefix = identity.map { "adb-" + $0 }
        var seen = Set<String>()
        let addresses = tools.command(["mdns", "services"]).split(separator: "\n").compactMap { line -> Endpoint? in
            let words = line.split(whereSeparator: \.isWhitespace)
            guard words.count == 3, words[0].hasPrefix("adb-") else { return nil }
            let type = words[1].trimmingCharacters(in: CharacterSet(charactersIn: "."))
            if let prefix {
                guard words[0] == prefix || words[0].hasPrefix(prefix + "-"),
                      type == "_adb._tcp" || type == "_adb-tls-connect._tcp" else { return nil }
            } else if type != "_adb._tcp" {
                return nil
            }
            guard let endpoint = Endpoint(String(words[2])), endpoint.isOnLocalNetwork,
                  seen.insert(endpoint.value).inserted else { return nil }
            return endpoint
        }
        return identity == nil && addresses.count != 1 ? [] : addresses
    }

    private func wifiAddressFromUSB(_ usb: Device) -> Endpoint? {
        let output = query(usb.transport, ["ip", "-4", "addr", "show", "wlan0"])
        guard let range = output.range(of: #"(?<=inet )[0-9.]+(?=/)"#, options: .regularExpression),
              let port = Int(query(usb.transport, ["getprop", "service.adb.tcp.port"])),
              port > 0 else { return nil }
        return Endpoint("\(output[range]):\(port)")
    }

    func find(remembered: String?, history: [String], manual: Endpoint?, passes: Int,
              cancelled: () -> Bool) -> SearchResult {
        let initial = devices
        let knownUSB = initial.filter { $0.usb && $0.quest && $0.state == "device" }
        let usb = remembered.flatMap { saved in knownUSB.first { $0.transport == saved } }
            ?? (remembered == nil && knownUSB.count == 1 ? knownUSB.first : nil)
        let identity = remembered ?? usb.flatMap { serial($0.transport) }
        let fallback = usb.flatMap(wifiAddressFromUSB)
        var attempted: [String: Int] = [:]
        var connectionHint: String?
        var awaitingApproval = false
        for pass in 0..<passes {
            if cancelled() { break }
            let current = pass == 0 ? initial : devices
            if current.contains(where: { device in
                device.state == "unauthorized" && (device.quest || device.transport == identity)
            }) { awaitingApproval = true }
            if manual == nil {
                let wireless = current.filter {
                    !$0.usb && $0.quest && $0.state == "device" &&
                    (Endpoint($0.transport) != nil || $0.transport.contains("_adb-tls-connect._tcp"))
                }
                if identity != nil || wireless.count == 1 {
                    for device in wireless {
                        if cancelled() { break }
                        let match = verify(device.transport, expected: identity, address: Endpoint(device.transport))
                        if let match {
                            return .found(match)
                        }
                    }
                }
            }

            var addresses: [Endpoint]
            if let manual {
                addresses = [manual]
            } else {
                addresses = advertisedAddresses(for: identity)
                if let fallback { addresses.append(fallback) }
                addresses += history.compactMap(Endpoint.init)
            }
            for endpoint in addresses {
                if cancelled() { break }
                guard endpoint.isOnLocalNetwork else {
                    if manual != nil { connectionHint = "Этот IP не в локальной сети Mac. Проверь Wi‑Fi и адрес Quest." }
                    continue
                }
                let previousPass = attempted[endpoint.value]
                guard previousPass == nil || (passes > 3 && pass - (previousPass ?? pass) >= 3) else { continue }
                attempted[endpoint.value] = pass
                if current.contains(where: { $0.transport == endpoint.value && $0.state == "offline" }) {
                    _ = tools.command(["disconnect", endpoint.value], timeout: 2)
                }
                let output = tools.command(["connect", endpoint.value], timeout: 3)
                if let match = verify(endpoint.value, expected: identity, address: endpoint) {
                    return .found(match)
                }
                if output.localizedCaseInsensitiveContains("No route to host") {
                    connectionHint = "Mac не может открыть соединение. Проверь доступ MonoRelay к локальной сети в настройках macOS."
                } else if output.localizedCaseInsensitiveContains("authenticate") ||
                            output.localizedCaseInsensitiveContains("unauthorized") {
                    awaitingApproval = true
                }
            }
            if pass < passes - 1 && !cancelled() { Thread.sleep(forTimeInterval: 0.8) }
        }
        if awaitingApproval { return .setup(.approve) }
        if let connectionHint { return .unavailable(connectionHint) }
        if knownUSB.count > 1 || initial.filter({ !$0.usb && $0.quest && $0.state == "device" }).count > 1 {
            return .unavailable("Доступно несколько шлемов. Укажи IP нужного Quest.")
        }
        if manual == nil && usb != nil && fallback == nil { return .setup(.enableWiFi) }
        if manual == nil && identity == nil { return .setup(.plugIn) }
        return .unavailable("Проверь, что Quest включён, ADB по Wi‑Fi активен, а Mac в той же сети.")
    }
}
