using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;

namespace MonoRelay.Windows;

internal sealed record Preset(string Id, string Title, string Description, int MaxSize, int Fps, int BitRateMbps);

internal sealed record Preferences(string Preset = "balance", string? Serial = null, string[]? Addresses = null)
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
    private readonly QuestConnection connection = new(Path.Combine(AppContext.BaseDirectory, "Tools"));
    private readonly Label title = new() { Text = "Трансляция Quest", AutoSize = true, Font = new Font("Segoe UI", 17, FontStyle.Bold) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(400, 0), Text = "Готово к подключению" };
    private readonly Label hint = new() { AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = SystemColors.GrayText };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 174, AccessibleName = "Режим трансляции" };
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
        Text = "MonoRelay"; ClientSize = new Size(445, 238);
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(22, 15, 22, 15) };
        var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        modes.Controls.Add(new Label { Text = "Режим", AutoSize = true, Margin = new Padding(0, 6, 8, 0), UseMnemonic = false });
        modes.Controls.Add(mode);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(action);
        buttons.Controls.Add(manual);
        layout.Controls.Add(title);
        layout.Controls.Add(status);
        layout.Controls.Add(modes);
        layout.Controls.Add(hint);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        mode.DisplayMember = nameof(Preset.Title);
        mode.DataSource = presets;
        mode.SelectedIndex = Math.Max(0, Array.FindIndex(presets, profile => profile.Id == preferences.Preset));
        hint.Text = Current.Description;
        mode.SelectedIndexChanged += (_, _) => SetPreset();
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
            ShowState("Трансляция запущена", "Остановить", false);
            frame.Stop();
        };
        Shown += async (_, _) => await Start();
        FormClosing += (_, _) => Stop();
        AcceptButton = action;
    }

    private Preset Current => mode.SelectedItem as Preset ?? presets[1];

    private void ShowState(string message, string button, bool canEnterIP)
    {
        status.Text = message;
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
        preferences = preferences with { Preset = Current.Id };
        preferences.Save();
        hint.Text = Current.Description;
        if (player is null || restartForPreset) return;
        restartForPreset = true;
        ShowState("Применяем режим…", "Отменить", false);
        EndPlayer(player);
    }

    private void Stop()
    {
        generation = Guid.NewGuid();
        restartForPreset = false;
        pending?.Cancel();
        pending = null;
        frame.Stop();
        var previous = player;
        player = null;
        if (previous is not null) EndPlayer(previous);
        ShowState("Готово к подключению", "Подключить", true);
    }

    private static void EndPlayer(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }

    private async Task Start(Endpoint? endpoint = null, bool retry = false)
    {
        Stop();
        if (!connection.IsInstalled)
        {
            ShowState("Распакуй ZIP целиком: рядом с приложением нужна папка Tools.", "Подключить", false);
            return;
        }
        if (endpoint is not null && !endpoint.IsLocal())
        {
            ShowState("Этот адрес вне локальной сети компьютера.", "Подключить", true);
            return;
        }

        var id = generation;
        using var operation = new CancellationTokenSource();
        pending = operation;
        ShowState("Ищем Quest в локальной сети…", "Отменить", false);
        try
        {
            var found = await Task.Run(async () => await connection.Find(preferences.Serial,
                preferences.Addresses ?? [], endpoint, retry, operation.Token), operation.Token);
            if (id != generation) return;
            pending = null;
            if (found is null)
            {
                ShowState("Quest не найден. Проверь Wi-Fi и беспроводную отладку.", "Подключить", true);
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
            ShowState("Не удалось запустить подключение. Проверь папку Tools.", "Подключить", true);
        }
    }

    private void LaunchPlayer(Headset headset, Guid id, bool retry)
    {
        var preset = Current;
        var args = new List<string> { $"--serial={headset.Transport}", $"--max-size={preset.MaxSize}",
            $"--max-fps={preset.Fps}", $"--video-bit-rate={preset.BitRateMbps}M", "--no-control", "--hwdec=disabled",
            "--window-title=MonoRelay · Трансляция", "--window-width=880", "--window-height=495",
            "--audio-source=playback", "--audio-dup" };
        if (headset.Crop is not null) args.AddRange([$"--crop={headset.Crop}", "--render-fit=stretched"]);
        var info = connection.StartInfo(connection.Player, args);
        info.Environment["ADB"] = connection.Adb;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
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
                    if (restartForPreset) await Start();
                    else if (exit == 2 && !retry) await Start(retry: true);
                    else if (exit == 0) Stop();
                    else ShowState("Трансляция прервалась. Проверь Wi-Fi и состояние Quest.", "Подключить", true);
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
        sawWindow = false;
        ShowState("Ожидаем изображение с Quest…", "Отменить", false);
        frame.Start();
    }
}
