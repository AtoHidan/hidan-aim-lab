namespace Hidan.Desktop;

using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;

public partial class Form1 : Form
{
    private readonly WebView2 gameView = new();
    private Rectangle windowedBounds = Rectangle.Empty;
    private string currentDisplayMode = "windowed";
    private bool isFullscreen;
    private string gamePath = string.Empty;
    private string leaderboardPath = string.Empty;

    public Form1()
    {
        InitializeComponent();
        Text = "HIDAN Aim Lab";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 800);
        MinimumSize = new Size(960, 640);
        KeyPreview = true;
        BackColor = Color.FromArgb(21, 24, 21);

        gameView.Dock = DockStyle.Fill;
        Controls.Add(gameView);
        Shown += InitializeGame;
    }

    private async void InitializeGame(object? sender, EventArgs eventArgs)
    {
        gamePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        if (!File.Exists(gamePath))
        {
            MessageBox.Show(this, "The HIDAN game files are missing. Reinstall the game or verify its installed files.", "HIDAN could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        try
        {
            var userDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HIDAN", "WebView2");
            Directory.CreateDirectory(userDataPath);
            leaderboardPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HIDAN", "leaderboard.json");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataPath);
            await gameView.EnsureCoreWebView2Async(environment);

            var settings = gameView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            gameView.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            gameView.CoreWebView2.WebMessageReceived += HandleWebMessageReceived;
            gameView.Source = new Uri(gamePath);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"HIDAN needs the Microsoft Edge WebView2 Runtime to run.\n\n{exception.Message}", "HIDAN could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void HandleWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        if (!IsGamePage(eventArgs.Source)) return;

        var message = eventArgs.TryGetWebMessageAsString();
        if (message == "toggle-fullscreen")
        {
            ToggleFullscreen();
            return;
        }
        if (message == "load-leaderboard")
        {
            var data = LoadLeaderboard();
            gameView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "leaderboard-loaded", records = data.Records }, JsonOptions));
            return;
        }

        try
        {
            using var payload = JsonDocument.Parse(message);
            var type = payload.RootElement.GetProperty("type").GetString();
            if (type == "save-leaderboard")
            {
                var entries = payload.RootElement.GetProperty("records").Deserialize<List<LeaderboardEntry>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                SaveLeaderboard(entries);
            }
            else if (type == "set-display-mode")
            {
                SetDisplayMode(payload.RootElement.GetProperty("mode").GetString() ?? "windowed-fullscreen");
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            System.Diagnostics.Debug.WriteLine($"Ignored invalid HIDAN web message: {exception.Message}");
        }
    }

    private bool IsGamePage(string source)
    {
        return Uri.TryCreate(source, UriKind.Absolute, out var sourceUri)
            && string.Equals(Path.GetFullPath(sourceUri.LocalPath), Path.GetFullPath(gamePath), StringComparison.OrdinalIgnoreCase);
    }

    private LeaderboardData LoadLeaderboard()
    {
        try
        {
            if (!File.Exists(leaderboardPath)) return new LeaderboardData();
            return JsonSerializer.Deserialize<LeaderboardData>(File.ReadAllText(leaderboardPath), JsonOptions) ?? new LeaderboardData();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not read HIDAN leaderboard: {exception.Message}");
            return new LeaderboardData();
        }
    }

    private void SaveLeaderboard(List<LeaderboardEntry> entries)
    {
        var validModes = new HashSet<string>(["grid", "micro", "reflex"], StringComparer.Ordinal);
        var normalized = entries
            .Where(entry => validModes.Contains(entry.Mode) && double.IsFinite(entry.Score) && entry.Score >= 0 && entry.Score <= 100000)
            .Select(entry => new LeaderboardEntry
            {
                Mode = entry.Mode,
                Player = string.IsNullOrWhiteSpace(entry.Player) ? "Player" : entry.Player.Trim()[..Math.Min(24, entry.Player.Trim().Length)],
                Score = entry.Score,
                Accuracy = Math.Clamp(entry.Accuracy, 0, 100),
                Misses = Math.Clamp(entry.Misses, 0, 100000),
                ReactionMs = Math.Clamp(entry.ReactionMs, 0, 100000),
                PlayedAt = entry.PlayedAt
            })
            .GroupBy(entry => entry.Mode)
            .SelectMany(group => group.OrderByDescending(entry => entry.Score).ThenByDescending(entry => entry.Accuracy).Take(5))
            .ToList();

        var data = new LeaderboardData { Records = normalized };
        Directory.CreateDirectory(Path.GetDirectoryName(leaderboardPath)!);
        var temporaryPath = leaderboardPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temporaryPath, leaderboardPath, true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public sealed class LeaderboardData
    {
        public int Version { get; set; } = 1;
        public List<LeaderboardEntry> Records { get; set; } = [];
    }

    public sealed class LeaderboardEntry
    {
        public string Mode { get; set; } = "grid";
        public string Player { get; set; } = "Player";
        public double Score { get; set; }
        public int Accuracy { get; set; }
        public double Misses { get; set; }
        public int ReactionMs { get; set; }
        public string PlayedAt { get; set; } = string.Empty;
    }

    private void ToggleFullscreen()
    {
        SetDisplayMode(isFullscreen ? "windowed-fullscreen" : "fullscreen");
    }

    private void SetDisplayMode(string mode)
    {
        if (mode is not ("windowed" or "windowed-fullscreen" or "fullscreen")) return;
        if (currentDisplayMode == "windowed" && WindowState == FormWindowState.Normal) windowedBounds = Bounds;

        var screen = Screen.FromControl(this);
        switch (mode)
        {
            case "windowed":
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Normal;
                Bounds = windowedBounds.IsEmpty ? DefaultWindowBounds(screen.WorkingArea) : windowedBounds;
                isFullscreen = false;
                break;
            case "windowed-fullscreen":
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                Bounds = screen.WorkingArea;
                isFullscreen = false;
                break;
            case "fullscreen":
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                Bounds = screen.Bounds;
                isFullscreen = true;
                break;
        }

        currentDisplayMode = mode;
        gameView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "display-mode-changed", mode }));
    }

    private static Rectangle DefaultWindowBounds(Rectangle workingArea)
    {
        var width = Math.Min(1280, workingArea.Width - 80);
        var height = Math.Min(800, workingArea.Height - 80);
        return new Rectangle(workingArea.Left + (workingArea.Width - width) / 2, workingArea.Top + (workingArea.Height - height) / 2, width, height);
    }
}
