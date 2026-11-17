using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MonoRelay.Windows;

internal sealed record Preset(string Id, string Title, string Detail, int MaxSize, int Fps, int BitRateMbps,
                              int VideoBufferMs, int AudioBufferMs);

internal sealed record Preferences(string Preset = AdaptiveQuality.Id, string? Serial = null, string[]? Addresses = null)
{
    private static string Location => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                   "MonoRelay", "settings.json");
    internal static Preferences Load()
    {
        try { return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Location)) ?? new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }
    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
            var temporary = Location + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, Location, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, "Local\\MonoRelay", out bool isFirst);
        if (!isFirst) return;
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        using var data = Assembly.GetExecutingAssembly().GetManifestResourceStream("stream-presets.json")!;
        var presets = JsonSerializer.Deserialize<Preset[]>(data, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Application.Run(new RelayForm(presets));
    }
}

internal sealed class RelayForm : Form
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private AdaptiveQuality adaptive = new();
    private readonly QuestConnection connection = new(Path.Combine(AppContext.BaseDirectory, "Tools"));
    private readonly Label status = new() {
        AutoSize = true, MinimumSize = new Size(0, 22), MaximumSize = new Size(360, 0),
        TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty,
    };
    private readonly Panel statusDot = new() { Size = new Size(8, 22), Margin = new Padding(0, 0, 8, 0) };
    private readonly Label instruction = new() { AutoSize = true, Anchor = AnchorStyles.None, MaximumSize = new Size(376, 0),
        TextAlign = ContentAlignment.MiddleCenter, ForeColor = LauncherColors.Secondary, Margin = Padding.Empty };
    private readonly Label quality = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = LauncherColors.Secondary, Margin = Padding.Empty };
    private readonly LinkLabel guide = new() { AutoSize = true, Text = "Setup guide", Anchor = AnchorStyles.None,
        LinkColor = LauncherColors.Surface.GetBrightness() < 0.5f ? Color.FromArgb(144, 190, 255) : LauncherColors.Accent };
    private string selectedQuality = AdaptiveQuality.Id;
    private readonly RelayAction action = new() { Dock = DockStyle.Fill, Text = "Connect", Primary = true, Margin = Padding.Empty };
    private readonly RelayAction manual = new() { Dock = DockStyle.Fill, Text = "Enter IP", Margin = Padding.Empty, Visible = false };
    private readonly TextBox address = new() { PlaceholderText = "Quest IP address",
        Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(8, 0, 8, 0),
        AccessibleName = "Quest IP address", Visible = false };
    private bool enteringAddress;
    private readonly System.Windows.Forms.Timer frame = new() { Interval = 200 };
    private Preferences preferences = Preferences.Load();
    private CancellationTokenSource? pending;
    private Process? player;
    private bool restartForPreset;
    private Guid generation;
    private bool sawWindow;
    private readonly Preset[] presets;

    internal RelayForm(Preset[] presets)
    {
        this.presets = presets;
        Text = "MonoRelay";
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = LauncherColors.Surface;
        ForeColor = LauncherColors.Text;
        using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MonoRelay.ico");
        using var largeIcon = iconStream is null ? null : new Icon(iconStream, 128, 128);
        var mark = largeIcon?.ToBitmap();
        if (mark is not null) Disposed += (_, _) => mark.Dispose();

        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(28),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 80, 50, 88, 42, 40, 20, 40 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        ClientSize = new Size(460, 416);
        layout.Controls.Add(new PictureBox {
            Image = mark, Size = new Size(80, 80), SizeMode = PictureBoxSizeMode.Zoom,
            Anchor = AnchorStyles.None, Margin = Padding.Empty,
        }, 0, 0);
        layout.Controls.Add(new Label {
            Text = "MonoRelay", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 25), Margin = Padding.Empty,
        }, 0, 1);

        var state = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        state.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        state.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        state.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        var statusRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.None,
            Margin = Padding.Empty };
        statusDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int diameter = statusDot.LogicalToDeviceUnits(8);
            using var brush = new SolidBrush(sawWindow && !SystemInformation.HighContrast
                ? Color.FromArgb(26, 141, 79) : LauncherColors.Accent);
            e.Graphics.FillEllipse(brush, 0, (statusDot.Height - diameter) / 2, diameter, diameter);
        };
        statusRow.Controls.AddRange([statusDot, status]);
        state.Controls.Add(statusRow, 0, 0);
        state.Controls.Add(instruction, 0, 1);
        state.Controls.Add(guide, 0, 2);
        layout.Controls.Add(state, 0, 2);
        layout.Controls.Add(CreateModes(), 0, 3);
        layout.Controls.Add(quality, 0, 4);
        layout.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Top, BackColor = LauncherColors.Divider,
            Margin = new Padding(0, 5, 0, 0) }, 0, 5);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        buttons.Controls.Add(manual, 0, 0);
        buttons.Controls.Add(address, 1, 0);
        buttons.Controls.Add(action, 2, 0);
        layout.Controls.Add(buttons, 0, 6);
        Controls.Add(layout);
        UpdateQualityDetail();
        ShowState("Ready to connect", "Connect");
        guide.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(
            "https://github.com/CHXZAVARKA/MonoRelay/blob/main/SETUP.md") { UseShellExecute = true });
        address.Text = preferences.Addresses?.FirstOrDefault() ?? "";
        address.TextChanged += (_, _) => action.Enabled = !enteringAddress || Endpoint.Parse(address.Text) is not null;
        action.Click += async (_, _) =>
        {
            if (player is not null || pending is not null) Stop();
            else if (!enteringAddress) await Start();
            else if (Endpoint.Parse(address.Text) is { } endpoint) await Start(endpoint);
        };
        manual.Click += (_, _) =>
        {
            SetAddressEntry(!enteringAddress);
            action.Text = enteringAddress ? "Connect" : "Retry";
            if (enteringAddress) address.Select();
        };
        frame.Tick += (_, _) =>
        {
            if (player is null || player.HasExited || sawWindow) return;
            player.Refresh();
            if (player.MainWindowHandle == IntPtr.Zero) return;
            sawWindow = true;
            ShowState("Streaming", "Stop");
            frame.Stop();
        };
        Shown += async (_, _) => await Start();
        FormClosing += (_, _) => Stop();
        AcceptButton = action;
    }

    private Control CreateModes()
    {
        var choices = new[] { (Id: AdaptiveQuality.Id, Title: "Auto") }
            .Concat(presets.Select(preset => (preset.Id, preset.Title))).ToArray();
        var modes = new RelayTrack { Dock = DockStyle.Fill, ColumnCount = choices.Length, RowCount = 1,
            Margin = Padding.Empty, Padding = new Padding(3), BackColor = LauncherColors.Track,
            AccessibleName = "Stream quality" };
        modes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selectedQuality = choices.FirstOrDefault(choice => choice.Id == preferences.Preset).Id ?? AdaptiveQuality.Id;
        foreach (var choice in choices)
        {
            modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / choices.Length));
            var button = new RelaySegment { Text = choice.Title,
                Dock = DockStyle.Fill, Margin = Padding.Empty, Checked = choice.Id == selectedQuality };
            button.CheckedChanged += (_, _) =>
            {
                if (!button.Checked || selectedQuality == choice.Id) return;
                selectedQuality = choice.Id;
                SetPreset();
            };
            modes.Controls.Add(button);
        }
        return modes;
    }

    private bool IsAuto => selectedQuality == AdaptiveQuality.Id;
    private Preset Current => IsAuto ? presets[adaptive.Index] : presets.First(p => p.Id == selectedQuality);

    private void UpdateQualityDetail() => quality.Text = IsAuto ? "Adjusts quality as you watch" : Current.Detail;

    private void ShowState(string message, string button, bool canEnterIP = false, string detail = "")
    {
        status.Text = message;
        instruction.Text = detail;
        instruction.Visible = detail.Length != 0;
        statusDot.Invalidate();
        action.Text = button;
        action.Primary = button != "Stop";
        action.Invalidate();
        manual.Visible = canEnterIP;
        guide.Visible = canEnterIP;
        if (!canEnterIP) SetAddressEntry(false);
    }

    private void SetAddressEntry(bool visible)
    {
        enteringAddress = visible;
        address.Visible = visible;
        manual.Text = visible ? "Cancel" : "Enter IP";
        action.Enabled = !visible || Endpoint.Parse(address.Text) is not null;
    }

    private void SetPreset()
    {
        adaptive = new();
        UpdateQualityDetail();
        preferences = preferences with { Preset = selectedQuality };
        preferences.Save();
        RestartPlayer();
    }

    private void RestartPlayer()
    {
        if (player is null || restartForPreset) return;
        restartForPreset = true;
        ShowState("Changing quality…", "Stop");
        EndPlayer(player);
    }

    private void Stop()
    {
        generation = Guid.NewGuid();
        restartForPreset = false;
        pending?.Cancel();
        pending = null;
        frame.Stop();
        sawWindow = false;
        var previous = player;
        player = null;
        if (previous is not null) EndPlayer(previous);
        ShowState("Ready to connect", "Connect");
    }

    private static void EndPlayer(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }

    private async Task Start(Endpoint? endpoint = null, bool retry = false, bool keepAdaptive = false)
    {
        if (!keepAdaptive) adaptive = new();
        Stop();
        if (!connection.IsInstalled)
        {
            ShowState("Extract the whole ZIP, including the Tools folder.", "Retry");
            return;
        }
        if (endpoint is not null && !endpoint.IsLocal())
        {
            ShowState("This IP address is outside your local network.", "Retry", true);
            return;
        }

        var id = generation;
        using var operation = new CancellationTokenSource();
        pending = operation;
        ShowState("Looking for Quest…", "Stop");
        try
        {
            var result = await Task.Run(async () => await connection.Find(preferences.Serial,
                preferences.Addresses ?? [], endpoint, retry, operation.Token), operation.Token);
            if (id != generation) return;
            pending = null;
            if (result.Device is not { } found)
            {
                var (message, detail) = result.Step switch
                {
                    SetupStep.PlugIn => ("Connect Quest by USB", "Approve the USB debugging prompt in your headset."),
                    SetupStep.Approve => ("Confirm in the headset", "Approve debugging, then click Retry."),
                    SetupStep.EnableWiFi => ("Enable ADB over Wi-Fi", "Use Meta VR Developer Hub, then unplug USB and click Retry."),
                    _ => ("Quest not found", "Check Wi-Fi and ADB over Wi-Fi."),
                };
                ShowState(message, "Retry", true, detail);
                return;
            }
            var addresses = preferences.Addresses ?? [];
            if (found.Address is not null)
                addresses = new[] { found.Address.ToString() }.Concat(addresses).Distinct().Take(8).ToArray();
            preferences = preferences with { Serial = found.Serial, Addresses = addresses };
            preferences.Save();
            LaunchPlayer(found, id, retry);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or NetworkInformationException)
        {
            if (id != generation) return;
            pending = null;
            ShowState("Could not connect. Check the Tools folder.", "Retry", true);
        }
    }

    private void LaunchPlayer(Headset headset, Guid id, bool retry)
    {
        var preset = Current;
        var args = new List<string> { $"--serial={headset.Transport}", $"--max-size={preset.MaxSize}",
            $"--max-fps={preset.Fps}", $"--video-bit-rate={preset.BitRateMbps}M", $"--video-buffer={preset.VideoBufferMs}",
            "--no-control", "--hwdec=disabled", "--pause-on-exit=false",
            "--window-title=MonoRelay · Stream", "--window-width=880", "--window-height=495",
            "--audio-source=playback", "--audio-dup", $"--audio-buffer={preset.AudioBufferMs}" };
        var automatic = IsAuto;
        if (automatic)
        {
            adaptive.BeginStream();
            args.Add("--print-fps");
        }
        if (headset.Crop is not null) args.AddRange([$"--crop={headset.Crop}", "--render-fit=stretched"]);
        var info = connection.StartInfo(connection.Player, args);
        info.Environment["ADB"] = connection.Adb;
        info.RedirectStandardOutput = automatic;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (automatic)
        {
            process.OutputDataReceived += (_, sample) =>
            {
                if (AdaptiveQuality.Parse(sample.Data) is { } frames && !IsDisposed && IsHandleCreated)
                    BeginInvoke(() => ObserveFrames(id, frames, preset.Fps));
            };
        }
        process.Exited += (_, _) =>
        {
            var exit = process.ExitCode;
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke(async () =>
                {
                    process.Dispose();
                    if (id != generation) return;
                    player = null;
                    frame.Stop();
                    if (restartForPreset) await Start(keepAdaptive: true);
                    else if (exit == 2 && !retry) await Start(retry: true, keepAdaptive: true);
                    else if (exit == 0) Stop();
                    else ShowState("Lost the connection to Quest.", "Retry", true);
                });
        };
        try
        {
            if (!process.Start()) throw new InvalidOperationException();
        }
        catch
        {
            process.Dispose();
            throw;
        }
        player = process;
        if (automatic) process.BeginOutputReadLine();
        sawWindow = false;
        ShowState("Waiting for video…", "Stop");
        frame.Start();
    }

    private void ObserveFrames(Guid id, (int Rendered, int Skipped) frames, int cap)
    {
        if (id != generation || player is null || player.HasExited || !sawWindow || !IsAuto || restartForPreset) return;
        player.Refresh();
        if (GetForegroundWindow() != player.MainWindowHandle)
        {
            adaptive.BeginStream();
            return;
        }
        if (adaptive.Sample(frames.Rendered, frames.Skipped, cap, presets.Length - 1)) RestartPlayer();
    }
}
