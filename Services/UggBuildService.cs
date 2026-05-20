using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace LolCoach.Services;

// Spawns tools/scrape_ugg.py (Playwright headless Chromium) to scrape u.gg's
// build page for the given champion + role. Cached on disk by the Python side
// (24h TTL). The C# layer fires-and-forgets so the WPF dispatcher never blocks.
public record UggBuildResult(
    string Champion, string Role,
    string? Keystone, string? SkillPriority,
    List<string> StartingItems, List<string> CoreItems,
    List<string> FourthOptions, List<string> FifthOptions, List<string> SixthOptions,
    List<string> SummonerSpells, List<string> Runes,
    string Source);

public class UggBuildService
{
    private readonly string _scriptPath;
    private readonly Dictionary<string, UggBuildResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public UggBuildService(string projectRoot)
    {
        _scriptPath = Path.Combine(projectRoot, "tools", "scrape_ugg.py");
    }

    public UggBuildResult? GetIfReady(string champion, string role)
    {
        lock (_gate)
        {
            return _cache.TryGetValue($"{champion}|{role}", out var r) ? r : null;
        }
    }

    public Task EnsureBuildAsync(string championName, string position)
    {
        var role = MapRole(position);
        if (role == null || string.IsNullOrWhiteSpace(championName))
            return Task.CompletedTask;
        var key = $"{championName}|{role}";
        lock (_gate)
        {
            if (_cache.ContainsKey(key) || _inflight.Contains(key))
                return Task.CompletedTask;
            _inflight.Add(key);
        }

        return Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(_scriptPath)) return;
                var psi = new ProcessStartInfo
                {
                    FileName = "python",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add(_scriptPath);
                psi.ArgumentList.Add(championName);
                psi.ArgumentList.Add(role);

                using var proc = Process.Start(psi);
                if (proc == null) return;
                var stdout = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return;

                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out _)) return;

                List<string> Section(string keyword)
                {
                    if (!root.TryGetProperty("build_sections", out var bs) || bs.ValueKind != JsonValueKind.Object)
                        return new();
                    foreach (var prop in bs.EnumerateObject())
                    {
                        if (prop.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                            prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            return prop.Value.EnumerateArray()
                                .Select(x => x.GetString() ?? "")
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList();
                        }
                    }
                    return new();
                }

                var result = new UggBuildResult(
                    Champion: championName,
                    Role: role,
                    Keystone: ExtractFirst(root, "runes_keystone")?.Replace("The Keystone ", ""),
                    SkillPriority: ExtractString(root, "skill_priority"),
                    StartingItems: Section("Starting"),
                    CoreItems: Section("Core"),
                    FourthOptions: Section("Fourth"),
                    FifthOptions: Section("Fifth"),
                    SixthOptions: Section("Sixth"),
                    SummonerSpells: ExtractList(root, "summoner_spells")
                        .Select(s => s.Replace("Summoner Spell ", "")).ToList(),
                    Runes: ExtractList(root, "runes_seen")
                        .Select(s => s.Replace("The Rune ", "").Replace("The Keystone ", ""))
                        .Take(8).ToList(),
                    Source: ExtractString(root, "source") ?? "u.gg");

                lock (_gate)
                {
                    _cache[key] = result;
                }
            }
            catch { /* swallow — best effort */ }
            finally
            {
                lock (_gate) { _inflight.Remove(key); }
            }
        });
    }

    public string Format(UggBuildResult r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"U.GG PRO BUILD ({r.Champion} {r.Role}):");
        if (!string.IsNullOrEmpty(r.SkillPriority)) sb.AppendLine($"  {r.SkillPriority}");
        if (!string.IsNullOrEmpty(r.Keystone)) sb.AppendLine($"  Keystone: {r.Keystone}");
        if (r.SummonerSpells.Count > 0) sb.AppendLine($"  Summoners: {string.Join(", ", r.SummonerSpells.Take(3))}");
        if (r.StartingItems.Count > 0) sb.AppendLine($"  Starting: {string.Join(", ", r.StartingItems)}");
        if (r.CoreItems.Count > 0) sb.AppendLine($"  Core path: {string.Join(" -> ", r.CoreItems)}");
        if (r.FourthOptions.Count > 0) sb.AppendLine($"  4th: {string.Join(" | ", r.FourthOptions)}");
        if (r.FifthOptions.Count > 0) sb.AppendLine($"  5th: {string.Join(" | ", r.FifthOptions)}");
        if (r.SixthOptions.Count > 0) sb.AppendLine($"  6th: {string.Join(" | ", r.SixthOptions)}");
        if (r.Runes.Count > 0) sb.AppendLine($"  Runes: {string.Join(", ", r.Runes.Take(6))}");
        return sb.ToString();
    }

    private static string? MapRole(string? pos) => (pos ?? "").ToUpperInvariant() switch
    {
        "TOP"     => "top",
        "JUNGLE"  => "jungle",
        "MIDDLE"  => "middle",
        "BOTTOM"  => "adc",
        "UTILITY" => "support",
        "SUPPORT" => "support",
        _         => null
    };

    private static string? ExtractString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static string? ExtractFirst(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        return v.GetArrayLength() > 0 ? v[0].GetString() : null;
    }

    private static List<string> ExtractList(JsonElement el, string name)
    {
        var list = new List<string>();
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.GetString() is { } s) list.Add(s);
        return list;
    }
}
