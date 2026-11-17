using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MonoRelay.Windows;

internal sealed record Preset(string Id, string Title, int MaxSize, int Fps, int BitRateMbps,
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
    private readonly Label title = new() { Text = "Трансляция Quest", AutoSize = true, Font = new Font("Segoe UI", 17, FontStyle.Bold) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(360, 0) };
    private readonly Label statusIcon = new() { AutoSize = true, Font = new Font("Segoe MDL2 Assets", 12), Margin = new Padding(0, 3, 8, 0) };
    private string selectedQuality = AdaptiveQuality.Id;
    private readonly Button action = new() { AutoSize = true, Text = "Подключить" };
    private readonly Button manual = new() { AutoSize = true, Text = "Ввести IP", Visible = false };
    private readonly TextBox address = new() { PlaceholderText = "192.168.1.42", Dock = DockStyle.Top,
        AccessibleName = "IP Quest", Visible = false };
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
        Text = "MonoRelay"; ClientSize = new Size(440, 242);
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(24) };
        foreach (int height in new[] { 42, 8, 48, 34 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        header.Controls.Add(new PictureBox { Image = Icon?.ToBitmap(), Size = new Size(28, 28),
            SizeMode = PictureBoxSizeMode.StretchImage, Margin = new Padding(0, 0, 10, 0) });
        title.Margin = Padding.Empty;
        header.Controls.Add(title);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Top, BackColor = SystemColors.ControlDark,
            Margin = Padding.Empty }, 0, 1);

        var state = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26));
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        state.Controls.Add(statusIcon, 0, 0);
        state.Controls.Add(status, 1, 0);
        layout.Controls.Add(state, 0, 2);
        layout.Controls.Add(CreateModes(), 0, 3);
        layout.Controls.Add(address, 0, 4);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        manual.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        action.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        buttons.Controls.Add(manual, 0, 0);
        buttons.Controls.Add(action, 1, 0);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        ShowState("Готово к подключению", "Подключить");
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
            action.Text = enteringAddress ? "Подключить" : "Повторить";
            if (enteringAddress) address.Select();
        };
        frame.Tick += (_, _) =>
        {
            if (player is null || player.HasExited || sawWindow) return;
            player.Refresh();
            if (player.MainWindowHandle == IntPtr.Zero) return;
            sawWindow = true;
            ShowState("Трансляция идёт", "Стоп");
            frame.Stop();
        };
        Shown += async (_, _) => await Start();
        FormClosing += (_, _) => Stop();
        AcceptButton = action;
    }

    private Control CreateModes()
    {
        var choices = new[] { (Id: AdaptiveQuality.Id, Title: "Авто") }
            .Concat(presets.Select(preset => (preset.Id, preset.Title))).ToArray();
        var modes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = choices.Length, RowCount = 1,
            Margin = Padding.Empty, AccessibleName = "Режим трансляции" };
        selectedQuality = choices.FirstOrDefault(choice => choice.Id == preferences.Preset).Id ?? AdaptiveQuality.Id;
        foreach (var choice in choices)
        {
            modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / choices.Length));
            var button = new RadioButton { Text = choice.Title, Appearance = Appearance.Button,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty,
                Checked = choice.Id == selectedQuality };
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

    private void ShowState(string message, string button, bool canEnterIP = false)
    {
        status.Text = message;
        statusIcon.Text = sawWindow ? "\ue73e" : "\ue701";
        action.Text = button;
        manual.Visible = canEnterIP;
        if (!canEnterIP) SetAddressEntry(false);
    }

    private void SetAddressEntry(bool visible)
    {
        enteringAddress = visible;
        address.Visible = visible;
        manual.Text = visible ? "Скрыть IP" : "Ввести IP";
        action.Enabled = !visible || Endpoint.Parse(address.Text) is not null;
    }

    private void SetPreset()
    {
        adaptive = new();
        preferences = preferences with { Preset = selectedQuality };
        preferences.Save();
        RestartPlayer();
    }

    private void RestartPlayer()
    {
        if (player is null || restartForPreset) return;
        restartForPreset = true;
        ShowState("Меняем качество…", "Стоп");
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
        ShowState("Готово к подключению", "Подключить");
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
            ShowState("Распакуй весь ZIP, включая папку Tools.", "Повторить");
            return;
        }
        if (endpoint is not null && !endpoint.IsLocal())
        {
            ShowState("Этот IP вне локальной сети компьютера.", "Повторить", true);
            return;
        }

        var id = generation;
        using var operation = new CancellationTokenSource();
        pending = operation;
        ShowState("Ищем Quest…", "Стоп");
        try
        {
            var found = await Task.Run(async () => await connection.Find(preferences.Serial,
                preferences.Addresses ?? [], endpoint, retry, operation.Token), operation.Token);
            if (id != generation) return;
            pending = null;
            if (found is null)
            {
                ShowState("Quest не найден. Проверь Wi-Fi и отладку.", "Повторить", true);
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
            ShowState("Не удалось подключиться. Проверь папку Tools.", "Повторить", true);
        }
    }

    private void LaunchPlayer(Headset headset, Guid id, bool retry)
    {
        var preset = Current;
        var args = new List<string> { $"--serial={headset.Transport}", $"--max-size={preset.MaxSize}",
            $"--max-fps={preset.Fps}", $"--video-bit-rate={preset.BitRateMbps}M", $"--video-buffer={preset.VideoBufferMs}",
            "--no-control", "--hwdec=disabled", "--pause-on-exit=false",
            "--window-title=MonoRelay · Трансляция", "--window-width=880", "--window-height=495",
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
                    else ShowState("Связь с Quest прервалась.", "Повторить", true);
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
        ShowState("Ждём видео…", "Стоп");
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
