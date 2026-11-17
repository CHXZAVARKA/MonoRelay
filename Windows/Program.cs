using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MonoRelay.Windows;

internal sealed record Preset(string Id, string Title, int MaxSize, int Fps, int BitRateMbps,
                              int VideoBufferMs, int AudioBufferMs)
{
    internal string Detail => $"{MaxSize} px · до {Fps} FPS · {BitRateMbps} Мбит/с";
}

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
    private readonly Label status = new() { AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    private readonly Label statusDetail = new() { AutoSize = true, MaximumSize = new Size(365, 0), ForeColor = SystemColors.GrayText };
    private readonly Label statusIcon = new() { AutoSize = true, Font = new Font("Segoe MDL2 Assets", 12), Margin = new Padding(0, 3, 8, 0) };
    private readonly Label hint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(1, 5, 0, 0) };
    private string selectedQuality = AdaptiveQuality.Id;
    private readonly Button action = new() { AutoSize = true, Text = "Подключить" };
    private readonly Button manual = new() { AutoSize = true, Text = "Ввести IP…" };
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
        Text = "MonoRelay"; ClientSize = new Size(440, 270);
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(24) };
        foreach (int height in new[] { 42, 13, 65, 62 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
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
        var messages = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Margin = Padding.Empty };
        messages.Controls.AddRange([status, statusDetail]);
        state.Controls.Add(statusIcon, 0, 0);
        state.Controls.Add(messages, 1, 0);
        layout.Controls.Add(state, 0, 2);

        var quality = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        quality.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        quality.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        quality.Controls.Add(CreateModes(), 0, 0);
        quality.Controls.Add(hint, 0, 1);
        layout.Controls.Add(quality, 0, 3);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        manual.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        action.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        buttons.Controls.Add(manual, 0, 0);
        buttons.Controls.Add(action, 1, 0);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        UpdateQualityDetail();
        ShowState("Готово к подключению", "Подключи компьютер и Quest к одной сети.", "Подключить", true);
        action.Click += async (_, _) =>
        {
            if (player is not null || pending is not null) Stop();
            else await Start();
        };
        manual.Click += async (_, _) => await EnterAddress();
        frame.Tick += (_, _) =>
        {
            if (player is null || player.HasExited || sawWindow) return;
            player.Refresh();
            if (player.MainWindowHandle == IntPtr.Zero) return;
            sawWindow = true;
            UpdateQualityDetail();
            ShowState("Трансляция запущена", "Изображение открыто в отдельном окне.", "Остановить", false);
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

    private void UpdateQualityDetail()
    {
        var preset = Current;
        hint.Text = !IsAuto ? preset.Detail
            : sawWindow ? $"Сейчас {preset.Title.ToLowerInvariant()} · {preset.Detail}" : "Сам подберёт качество";
    }

    private void ShowState(string message, string detail, string button, bool canEnterIP)
    {
        status.Text = message;
        statusDetail.Text = detail;
        statusIcon.Text = sawWindow ? "\ue73e" : "\ue701";
        action.Text = button;
        manual.Enabled = canEnterIP;
    }

    private async Task EnterAddress()
    {
        using var prompt = new Form { Text = "Подключиться по адресу", ClientSize = new Size(355, 135),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, Font = Font };
        var label = new Label { Text = "IP-адрес Quest", Location = new Point(16, 14), AutoSize = true };
        var address = new TextBox { Text = preferences.Addresses?.FirstOrDefault() ?? "",
            PlaceholderText = "192.168.1.42:5555", Location = new Point(16, 42), Width = 320, AccessibleName = label.Text };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(138, 86), Width = 82 };
        var ok = new Button { Text = "Подключить", DialogResult = DialogResult.OK, Location = new Point(225, 86), Width = 111 };
        address.TextChanged += (_, _) => ok.Enabled = Endpoint.Parse(address.Text) is not null;
        ok.Enabled = Endpoint.Parse(address.Text) is not null;
        prompt.Controls.AddRange([label, address, cancel, ok]);
        prompt.AcceptButton = ok; prompt.CancelButton = cancel;
        if (prompt.ShowDialog(this) == DialogResult.OK && Endpoint.Parse(address.Text) is { } endpoint)
            await Start(endpoint);
    }

    private void SetPreset()
    {
        adaptive = new();
        preferences = preferences with { Preset = selectedQuality };
        preferences.Save();
        UpdateQualityDetail();
        RestartPlayer();
    }

    private void RestartPlayer()
    {
        if (player is null || restartForPreset) return;
        restartForPreset = true;
        ShowState("Применяем режим…", "Стрим коротко переподключится.", "Отменить", false);
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
        UpdateQualityDetail();
        var previous = player;
        player = null;
        if (previous is not null) EndPlayer(previous);
        ShowState("Готово к подключению", "Подключи компьютер и Quest к одной сети.", "Подключить", true);
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
            ShowState("Не удалось запустить MonoRelay", "Распакуй ZIP целиком. Рядом с приложением нужна папка Tools.", "Подключить", false);
            return;
        }
        if (endpoint is not null && !endpoint.IsLocal())
        {
            ShowState("Не удалось подключиться", "Этот IP не в локальной сети компьютера.", "Подключить", true);
            return;
        }

        var id = generation;
        using var operation = new CancellationTokenSource();
        pending = operation;
        ShowState("Ищем Quest в локальной сети…", "Проверяем подключение и сохранённые адреса.", "Отменить", false);
        try
        {
            var found = await Task.Run(async () => await connection.Find(preferences.Serial,
                preferences.Addresses ?? [], endpoint, retry, operation.Token), operation.Token);
            if (id != generation) return;
            pending = null;
            if (found is null)
            {
                ShowState("Не удалось подключиться", "Проверь Wi-Fi и отладку на шлеме. Можно ввести IP вручную.", "Подключить", true);
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
            ShowState("Не удалось подключиться", "Проверь содержимое папки Tools.", "Подключить", true);
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
                    else ShowState("Трансляция прервалась", "Проверь Wi-Fi и состояние Quest.", "Подключить", true);
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
        ShowState("Ожидаем изображение с Quest…", "Если очки спят, разбуди их.", "Отменить", false);
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
