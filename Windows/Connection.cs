using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MonoRelay.Windows;

internal sealed record Endpoint(IPAddress Address, int Port)
{
    public override string ToString() => $"{Address}:{Port}";

    internal static Endpoint? Parse(string? input)
    {
        var parts = input?.Trim().Split(':');
        if (parts is null || parts.Length is < 1 or > 2 ||
            parts[0].Count(c => c == '.') != 3 ||
            !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            ip.GetAddressBytes()[0] is 0 or 127 or > 223) return null;
        if (parts.Length == 1) return new(ip, 5555);
        return int.TryParse(parts[1], out var port) && port is >= 1 and <= 65535 ? new(ip, port) : null;
    }

    internal bool IsLocal()
    {
        var target = Address.GetAddressBytes();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is not (NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)) continue;
            foreach (var local in nic.GetIPProperties().UnicastAddresses)
            {
                if (local.Address.AddressFamily != AddressFamily.InterNetwork || local.Address.Equals(Address)) continue;
                var mask = local.IPv4Mask.GetAddressBytes();
                var bytes = local.Address.GetAddressBytes();
                if (mask.Any(b => b != 0) && Enumerable.Range(0, 4)
                    .All(i => (bytes[i] & mask[i]) == (target[i] & mask[i]))) return true;
            }
        }
        return false;
    }
}

internal sealed record Headset(string Transport, string Serial, Endpoint? Address, string? Crop);

internal enum SetupStep { None, PlugIn, Approve, EnableWiFi }

internal sealed record SearchResult(Headset? Device, SetupStep Step = SetupStep.None);

internal sealed class QuestConnection(string engine)
{
    internal string Adb => Path.Combine(engine, "adb.exe");
    internal string Player => Path.Combine(engine, "scrcpy.exe");
    internal bool IsInstalled => File.Exists(Adb) && File.Exists(Player);

    internal ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = engine };
        foreach (var item in arguments) info.ArgumentList.Add(item);
        return info;
    }

    private async Task<string> AdbCall(CancellationToken token, params string[] arguments)
    {
        var info = StartInfo(Adb, arguments);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Process.Start(info) ?? throw new InvalidOperationException();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return process.ExitCode == 0 ? (await output).Trim() : (await errors).Trim();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            token.ThrowIfCancellationRequested();
            return "";
        }
    }

    private async Task<Headset?> Verify(string transport, string? known, Endpoint? endpoint, CancellationToken token)
    {
        var serial = await AdbCall(token, "-s", transport, "shell", "getprop", "ro.serialno");
        if (serial.Length is < 5 or > 64 || serial.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            known is not null && serial != known) return null;
        var model = await AdbCall(token, "-s", transport, "shell", "getprop", "ro.product.model");
        if (!model.StartsWith("Quest", StringComparison.OrdinalIgnoreCase)) return null;
        var size = (await AdbCall(token, "-s", transport, "shell", "wm", "size"))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Split('x');
        string? crop = null;
        if (size?.Length == 2 && int.TryParse(size[0], out var width) && int.TryParse(size[1], out var height) && width >= 2 && height > 0)
            crop = (width, height) == (10992, 8000) ? "4800:3750:348:2125" : $"{width / 2}:{height}:0:0";
        return new(transport, serial, endpoint, crop);
    }

    private static string[][] Rows(string output) => output.Split('\n')
        .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(fields => fields.Length >= 2).ToArray();

    private async Task<Endpoint?> WiFiFromUSB(string usb, CancellationToken token)
    {
        var port = await AdbCall(token, "-s", usb, "shell", "getprop", "service.adb.tcp.port");
        if (!int.TryParse(port, out int portNumber) || portNumber is < 1 or > 65535) return null;
        var rows = Rows(await AdbCall(token, "-s", usb, "shell", "ip", "-4", "addr", "show", "wlan0"));
        var address = rows.FirstOrDefault(row => row[0] == "inet")?[1].Split('/')[0];
        return address is null ? null : Endpoint.Parse($"{address}:{portNumber}");
    }

    internal async Task<SearchResult> Find(string? known, IReadOnlyList<string> history, Endpoint? manual,
                                           bool reconnecting, CancellationToken token)
    {
        var attempted = new Dictionary<string, int>();
        bool awaitingApproval = false;
        bool needsWiFi = false;
        var passes = reconnecting ? 7 : 3;
        for (int pass = 0; pass < passes; pass++)
        {
            token.ThrowIfCancellationRequested();
            var devices = Rows(await AdbCall(token, "devices", "-l"));
            var usb = devices.Where(d => d[1] == "device" && d.Any(t => t.StartsWith("model:Quest", StringComparison.OrdinalIgnoreCase)) && d.Any(t => t.StartsWith("usb:"))).ToArray();
            if (known is null && usb.Length == 1)
            {
                var verifiedUsb = await Verify(usb[0][0], null, null, token);
                if (verifiedUsb is not null) known = verifiedUsb.Serial;
            }
            awaitingApproval |= devices.Any(d => d[1] == "unauthorized" &&
                (d[0] == known || d.Any(field => field.StartsWith("model:Quest", StringComparison.OrdinalIgnoreCase))));

            if (manual is null)
            {
                var active = devices.Where(d => d[1] == "device" && d.Any(t => t.StartsWith("model:Quest")) &&
                    (Endpoint.Parse(d[0]) is not null || d[0].Contains("_adb-tls-connect._tcp"))).ToArray();
                if (known is not null || active.Length == 1)
                    foreach (var d in active)
                        if (await Verify(d[0], known, Endpoint.Parse(d[0]), token) is { } connected) return new(connected);
            }

            var candidates = new List<Endpoint>();
            if (manual is not null) candidates.Add(manual);
            else
            {
                var advertised = new HashSet<Endpoint>();
                string? service = known is null ? null : "adb-" + known;
                foreach (var fields in Rows(await AdbCall(token, "mdns", "services")))
                {
                    if (fields.Length != 3 || !fields[0].StartsWith("adb-")) continue;
                    var type = fields[1].TrimEnd('.');
                    if (service is null)
                    {
                        if (type != "_adb._tcp") continue;
                    }
                    else if ((fields[0] != service && !fields[0].StartsWith(service + "-")) ||
                             type is not ("_adb._tcp" or "_adb-tls-connect._tcp")) continue;
                    if (Endpoint.Parse(fields[2]) is { } endpoint && endpoint.IsLocal()) advertised.Add(endpoint);
                }
                if (known is not null || advertised.Count == 1) candidates.AddRange(advertised);
                if (usb.FirstOrDefault(d => d[0] == known) is { } attached)
                {
                    if (await WiFiFromUSB(attached[0], token) is { } usbAddress) candidates.Add(usbAddress);
                    else needsWiFi = true;
                }
                candidates.AddRange(history.Take(8).Select(Endpoint.Parse).OfType<Endpoint>());
            }
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (!candidate.IsLocal()) continue;
                var address = candidate.ToString();
                if (attempted.TryGetValue(address, out var previous) && (!reconnecting || pass - previous < 3)) continue;
                attempted[address] = pass;
                if (devices.Any(fields => fields[0] == address && fields[1] == "offline"))
                    await AdbCall(token, "disconnect", address);
                var output = await AdbCall(token, "connect", address);
                if (await Verify(address, known, candidate, token) is { } connected) return new(connected);
                awaitingApproval |= output.Contains("authenticate", StringComparison.OrdinalIgnoreCase) ||
                                    output.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
            }
            if (pass + 1 < passes) await Task.Delay(800, token);
        }
        if (awaitingApproval) return new(null, SetupStep.Approve);
        if (manual is null && needsWiFi) return new(null, SetupStep.EnableWiFi);
        if (manual is null && known is null) return new(null, SetupStep.PlugIn);
        return new(null);
    }
}
