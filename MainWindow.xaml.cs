using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LolCoach.Models;
using LolCoach.Services;

namespace LolCoach;

public partial class MainWindow : Window
{
    private readonly LiveClientService _live = new();
    private readonly CoachService _coach;
    private readonly RiotApiService _riot;
    private readonly MetaDataService _meta = new();
    private readonly UggMatchupsService _ugg = new();
    private readonly UggBuildService _builds = new(AppContext.BaseDirectory);
    private readonly TipsService _tips = new();
    private readonly JungleTrackerService _jungle = new();
    private string _playbook = "";
    private string _playbookForRoster = "";
    private readonly DispatcherTimer _timer;
    private readonly int _coachCooldownMs;
    private DateTime _lastCoachCall = DateTime.MinValue;
    private string _lastStateHash = "";
    private string _ranksFetchedForGame = "";
    private bool _coachRunning;
    private readonly LinkedList<(DateTime At, string Text)> _history = new();
    private LiveGameData? _previousData;

    private bool _clickThrough;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public MainWindow()
    {
        InitializeComponent();
        var cfg = LoadConfig();
        var credPath = Environment.ExpandEnvironmentVariables(cfg.OAuthCredentialsPath ?? "");
        _coach = new CoachService(new CoachConfig(cfg.Provider, cfg.GroqModel, cfg.ClaudeModel, cfg.OllamaUrl, cfg.OllamaModel, credPath, cfg.Language));
        _riot = new RiotApiService(cfg.RiotPlatform, cfg.RiotRegional);
        _coachCooldownMs = cfg.CoachCooldownMs;

        // Lower process priority so we don't compete with LoL for CPU.
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }

        Loaded += (_, _) => ApplyOverlayStyles();
        KeyDown += OnGlobalKeyDown;
        // Also catch F8 even when window doesn't have focus.
        PreviewKeyDown += OnGlobalKeyDown;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(cfg.PollIntervalMs) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        _ = Task.Run(() => _coach.PrewarmAsync()); // keep model hot in VRAM
        _ = Task.Run(() => _meta.EnsureInitAsync()); // pre-warm Data Dragon
        Closed += (_, _) => { _live.Dispose(); _coach.Dispose(); _riot.Dispose(); _meta.Dispose(); _ugg.Dispose(); };
    }

    private void ApplyOverlayStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        // Mark as a tool window that never steals focus — matches Mobalytics/Blitz overlay pattern.
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    private void SetClickThrough(bool on)
    {
        _clickThrough = on;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (on) ex |= WS_EX_TRANSPARENT;
        else    ex &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        TxtStatus.Text = on ? "  · click-through ON (F8 to toggle)" : "  · live";
    }

    private void OnGlobalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F8)
        {
            SetClickThrough(!_clickThrough);
            e.Handled = true;
        }
        else if (e.Key == Key.F9)
        {
            // Toggle visibility entirely.
            Visibility = Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
            e.Handled = true;
        }
    }

    private static AppCfg LoadConfig()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppCfg>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppCfg();
            }
        }
        catch { }
        return new AppCfg();
    }

    private async Task TickAsync()
    {
        var data = await _live.GetAllGameDataAsync();
        if (data?.GameData == null || data.ActivePlayer == null)
        {
            TxtStatus.Text = "  · no game";
            TxtGameInfo.Text = "Waiting for League client (Live Client Data on :2999)…";
            return;
        }

        var time = data.GameData.GameTime;
        var riotTag = _riot.Enabled ? "  riot:on" : "  riot:off";
        TxtStatus.Text = $"  · live  {(int)(time / 60)}:{(int)(time % 60):D2}{riotTag}";

        var me = data.AllPlayers.FirstOrDefault(p => p.SummonerName == data.ActivePlayer.SummonerName);
        TxtGameInfo.Text = me != null
            ? $"{me.ChampionName} lvl{data.ActivePlayer.Level} {me.Position}  ·  KDA {me.Scores?.Kills}/{me.Scores?.Deaths}/{me.Scores?.Assists}  ·  CS {me.Scores?.CreepScore}  ·  Gold {data.ActivePlayer.CurrentGold:F0}"
            : $"{data.GameData.GameMode} on {data.GameData.MapName}";

        UpdateLaneOpponentPanel(data, me);
        await FetchRanksOnceAsync(data);
        await EnsurePlaybookAsync(data);

        var hash = ComputeStateHash(data);
        var sinceLast = (DateTime.UtcNow - _lastCoachCall).TotalMilliseconds;
        if (!_coachRunning && (hash != _lastStateHash || sinceLast > _coachCooldownMs * 3))
        {
            _coachRunning = true;
            _lastCoachCall = DateTime.UtcNow;
            _lastStateHash = hash;
            // Push the previous task into history before clearing.
            PushHistory(TxtAdvice.Text);
            TxtAdvice.Text = "";
            try
            {
                var combinedPlaybook = _playbook;
                var jungleBlock = _jungle.BuildBlock(data);
                if (!string.IsNullOrWhiteSpace(jungleBlock))
                    combinedPlaybook = combinedPlaybook + "\n" + jungleBlock;
                var tipsBlock = _tips.SelectAndFormat(data, max: 4);
                if (!string.IsNullOrWhiteSpace(tipsBlock))
                    combinedPlaybook = combinedPlaybook + "\n" + tipsBlock;
                var deltaBlock = ComputeStateDelta(_previousData, data);
                var recent = _history.Select(h => h.Text);
                var advice = await _coach.AdviseAsync(data, chunk =>
                {
                    Dispatcher.BeginInvoke(new Action(() => TxtAdvice.Text += chunk));
                }, metaPlaybook: combinedPlaybook, recentAdvice: recent, deltaBlock: deltaBlock);
                _previousData = data;
                if (string.IsNullOrWhiteSpace(TxtAdvice.Text))
                    TxtAdvice.Text = string.IsNullOrWhiteSpace(advice) ? "(no advice)" : advice.Trim();
            }
            finally { _coachRunning = false; }
        }
    }

    private async Task EnsurePlaybookAsync(LiveGameData d)
    {
        var mePlayer = d.AllPlayers.FirstOrDefault(p => p.SummonerName == d.ActivePlayer?.SummonerName);
        var myChamp = mePlayer?.ChampionName;
        var myPos = mePlayer?.Position;
        var myTeam = mePlayer?.Team;
        var enemyPlayers = d.AllPlayers
            .Where(p => p.SummonerName != d.ActivePlayer?.SummonerName && p.Team != myTeam)
            .ToList();
        var enemies = enemyPlayers.Select(p => p.ChampionName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var key = $"{myChamp}|{myPos}|{string.Join(",", enemies.OrderBy(x => x))}";
        if (key == _playbookForRoster && !string.IsNullOrEmpty(_playbook)) return;
        _playbookForRoster = key;

        string playbook;
        try { playbook = await _meta.BuildPlaybookAsync(myChamp, enemies); }
        catch { playbook = ""; }

        // Fire-and-forget: scrape pro build from u.gg (cached 24h). When ready,
        // future ticks will pick it up via GetIfReady() and inject.
        if (!string.IsNullOrEmpty(myChamp) && !string.IsNullOrEmpty(myPos))
            _ = _builds.EnsureBuildAsync(myChamp, myPos);
        // If already cached, inject now.
        if (!string.IsNullOrEmpty(myChamp) && !string.IsNullOrEmpty(myPos))
        {
            var roleMapped = (myPos ?? "").ToUpperInvariant() switch
            {
                "TOP"     => "top",
                "JUNGLE"  => "jungle",
                "MIDDLE"  => "middle",
                "BOTTOM"  => "adc",
                "UTILITY" => "support",
                "SUPPORT" => "support",
                _         => ""
            };
            if (!string.IsNullOrEmpty(roleMapped))
            {
                var ready = _builds.GetIfReady(myChamp, roleMapped);
                if (ready != null)
                    playbook += "\n" + _builds.Format(ready);
            }
        }

        // Append u.gg matchup winrates: player vs each enemy (lane opponent first, then others).
        try
        {
            var patchShort = _meta.PatchShort;
            var myKey = myChamp != null ? await _meta.GetChampionKeyAsync(myChamp) : null;
            var roleIdx = UggMatchupsService.RoleIndex(myPos);
            if (!string.IsNullOrEmpty(patchShort) && !string.IsNullOrEmpty(myKey) && roleIdx > 0)
            {
                var lines = new List<string>();
                var lanePos = (myPos ?? "").ToUpperInvariant();
                // Lane opponent first
                var ordered = enemyPlayers
                    .OrderByDescending(p => (p.Position ?? "").ToUpperInvariant() == lanePos)
                    .ToList();
                foreach (var en in ordered)
                {
                    var enKeyStr = await _meta.GetChampionKeyAsync(en.ChampionName);
                    if (!int.TryParse(enKeyStr, out var enKey)) continue;
                    var m = await _ugg.GetMatchupAsync(patchShort, myKey, roleIdx, enKey);
                    if (m == null) continue;
                    var tag = (en.Position ?? "").ToUpperInvariant() == lanePos ? "LANE" : en.Position?.ToUpperInvariant() ?? "??";
                    lines.Add($"  vs {en.ChampionName} ({tag}): {(m.Value.winrate * 100):0.#}% WR over {m.Value.games:N0} games");
                }
                if (lines.Count > 0)
                    playbook += "\nU.GG matchups (your champ in your role, current patch):\n" + string.Join("\n", lines);
            }
        }
        catch { /* best-effort */ }

        _playbook = playbook;
    }

    private void UpdateLaneOpponentPanel(LiveGameData d, PlayerEntry? me)
    {
        if (me == null) { TxtLane.Text = ""; return; }
        var lane = (me.Position ?? "").ToUpperInvariant();
        var enemy = d.AllPlayers.FirstOrDefault(p =>
            p.Team != me.Team && (p.Position ?? "").ToUpperInvariant() == lane);
        if (enemy == null) { TxtLane.Text = ""; return; }
        var items = enemy.Items.Count > 0
            ? string.Join(", ", enemy.Items.Take(6).Select(i => i.DisplayName))
            : "(no items)";
        var dead = enemy.IsDead ? $"  DEAD {enemy.RespawnTimer:F0}s" : "";
        TxtLane.Text = $"vs {enemy.ChampionName} ({lane}) lvl{enemy.Level}  KDA {enemy.Scores?.Kills}/{enemy.Scores?.Deaths}/{enemy.Scores?.Assists}  CS{enemy.Scores?.CreepScore}{dead}\n  items: {items}";
    }

    private void PushHistory(string? previousTask)
    {
        if (string.IsNullOrWhiteSpace(previousTask)) return;
        if (previousTask.StartsWith("Start a custom") || previousTask.StartsWith("Waiting") || previousTask.StartsWith("(no")) return;
        _history.AddFirst((DateTime.Now, previousTask.Trim()));
        while (_history.Count > 4) _history.RemoveLast();
        TxtHistory.Text = string.Join("\n", _history.Select(h => $"· {h.At:HH:mm:ss}  {h.Text}"));
    }

    private async Task FetchRanksOnceAsync(LiveGameData d)
    {
        if (!_riot.Enabled) return;
        // Key on the roster signature so we re-fetch when a new game starts.
        var rosterKey = string.Join("|", d.AllPlayers.Select(p => p.RiotIdGameName + "#" + p.RiotIdTagLine).OrderBy(x => x));
        if (rosterKey == _ranksFetchedForGame) return;
        _ranksFetchedForGame = rosterKey;

        foreach (var p in d.AllPlayers)
        {
            if (string.IsNullOrWhiteSpace(p.RiotIdGameName) || string.IsNullOrWhiteSpace(p.RiotIdTagLine)) continue;
            _ = Task.Run(async () =>
            {
                var rank = await _riot.GetRankAsync(p.RiotIdGameName!, p.RiotIdTagLine!);
                if (rank != null) p.CachedRank = rank;
            });
        }
        await Task.CompletedTask;
    }

    private static string ComputeStateDelta(LiveGameData? prev, LiveGameData curr)
    {
        if (prev == null) return "";
        var sb = new System.Text.StringBuilder();
        var pMe = prev.AllPlayers.FirstOrDefault(p => p.SummonerName == prev.ActivePlayer?.SummonerName);
        var cMe = curr.AllPlayers.FirstOrDefault(p => p.SummonerName == curr.ActivePlayer?.SummonerName);
        if (pMe != null && cMe != null)
        {
            if (prev.ActivePlayer?.Level != curr.ActivePlayer?.Level)
                sb.AppendLine($"  YOU LEVELED UP: lvl {prev.ActivePlayer?.Level} → {curr.ActivePlayer?.Level}");
            var pGold = prev.ActivePlayer?.CurrentGold ?? 0;
            var cGold = curr.ActivePlayer?.CurrentGold ?? 0;
            if (cGold - pGold < -200) sb.AppendLine($"  YOU SPENT GOLD: {pGold:F0} → {cGold:F0} (purchase made)");
            else if (cGold - pGold > 300) sb.AppendLine($"  YOU GAINED GOLD: +{cGold - pGold:F0} (kill/objective)");
            if ((pMe.Scores?.Kills ?? 0) < (cMe.Scores?.Kills ?? 0))
                sb.AppendLine("  YOU GOT A KILL");
            if ((pMe.Scores?.Deaths ?? 0) < (cMe.Scores?.Deaths ?? 0))
                sb.AppendLine("  YOU DIED");
            if ((pMe.Scores?.Assists ?? 0) < (cMe.Scores?.Assists ?? 0))
                sb.AppendLine("  YOU GOT AN ASSIST");
            var pItems = pMe.Items.Select(i => i.ItemId).ToHashSet();
            var cItems = cMe.Items.Select(i => i.ItemId).ToHashSet();
            foreach (var n in cMe.Items.Where(i => !pItems.Contains(i.ItemId)))
                sb.AppendLine($"  YOU BOUGHT: {n.DisplayName}");
        }
        var myTeam = cMe?.Team;
        foreach (var ce in curr.AllPlayers)
        {
            var pe = prev.AllPlayers.FirstOrDefault(x => x.SummonerName == ce.SummonerName);
            if (pe == null) continue;
            var sideLabel = ce.Team == myTeam ? "ally" : "enemy";
            if (pe.Level != ce.Level)
                sb.AppendLine($"  {sideLabel} {ce.ChampionName} leveled: {pe.Level}→{ce.Level}");
            if ((pe.Scores?.Kills ?? 0) < (ce.Scores?.Kills ?? 0))
                sb.AppendLine($"  {sideLabel} {ce.ChampionName} got a kill");
            if (!pe.IsDead && ce.IsDead)
                sb.AppendLine($"  {sideLabel} {ce.ChampionName} just DIED (respawn {ce.RespawnTimer:F0}s)");
            var pItems = pe.Items.Select(i => i.ItemId).ToHashSet();
            var newItems = ce.Items.Where(i => !pItems.Contains(i.ItemId)).Take(2).ToList();
            foreach (var n in newItems)
                sb.AppendLine($"  {sideLabel} {ce.ChampionName} bought: {n.DisplayName}");
        }
        // Recent events from live stream (last 15s only — focused window).
        var time = curr.GameData?.GameTime ?? 0;
        var recent = curr.Events?.Events?.Where(e => e.EventTime >= time - 15).TakeLast(5).ToList();
        if (recent?.Count > 0)
        {
            sb.AppendLine("  Events in last 15s:");
            foreach (var e in recent)
                sb.AppendLine($"    {e.EventName}{(e.KillerName != null ? $" by {e.KillerName}" : "")}{(e.VictimName != null ? $" on {e.VictimName}" : "")}{(e.DragonType != null ? $" ({e.DragonType} drake)" : "")}");
        }
        return sb.Length == 0 ? "" : sb.ToString();
    }

    private static string ComputeStateHash(LiveGameData d)
    {
        var me = d.AllPlayers.FirstOrDefault(p => p.SummonerName == d.ActivePlayer?.SummonerName);
        var items = me == null ? "" : string.Join(",", me.Items.Select(i => i.ItemId).OrderBy(x => x));
        var lastEventId = d.Events?.Events?.Count > 0 ? d.Events.Events.Max(e => e.EventId) : 0;
        return $"{d.ActivePlayer?.Level}|{items}|{me?.Scores?.Kills}-{me?.Scores?.Deaths}-{me?.Scores?.Assists}|{me?.Scores?.CreepScore}|{lastEventId}";
    }

    private async void OnRefreshNow(object sender, RoutedEventArgs e)
    {
        _lastStateHash = "";
        await TickAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private class AppCfg
    {
        public string Provider { get; set; } = "claudecode";
        public string OAuthCredentialsPath { get; set; } = "%USERPROFILE%\\.claude\\.credentials.json";
        public string OllamaUrl { get; set; } = "http://localhost:11434";
        public string OllamaModel { get; set; } = "gemma3:12b";
        public string GroqModel { get; set; } = "llama-3.3-70b-versatile";
        public string ClaudeModel { get; set; } = "claude-opus-4-7";
        public int PollIntervalMs { get; set; } = 2000;
        public int CoachCooldownMs { get; set; } = 4000;
        public string Language { get; set; } = "ro";
        public string RiotPlatform { get; set; } = "euw1";
        public string RiotRegional { get; set; } = "europe";
    }
}
