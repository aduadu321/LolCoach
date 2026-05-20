using System.IO;
using System.Text;
using System.Text.Json;
using LolCoach.Models;

namespace LolCoach.Services;

public record MacroTip(string Id, List<string> Tags, string Tip);

// Loads the local macro tip library and selects a small, state-relevant subset
// to inject into each coach prompt (RAG-lite).
public class TipsService
{
    private readonly List<MacroTip> _tips = new();

    public TipsService()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "macro_tips.json");
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("tips", out var arr)) return;
            foreach (var e in arr.EnumerateArray())
            {
                var tags = new List<string>();
                if (e.TryGetProperty("tags", out var t))
                    foreach (var x in t.EnumerateArray()) tags.Add(x.GetString() ?? "");
                _tips.Add(new MacroTip(
                    Id: e.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                    Tags: tags,
                    Tip: e.TryGetProperty("tip", out var ti) ? ti.GetString() ?? "" : ""));
            }
        }
        catch { /* ignore — tips are optional */ }
    }

    public int Count => _tips.Count;

    public string SelectAndFormat(LiveGameData data, int max = 5)
    {
        if (_tips.Count == 0) return "";
        var time = data.GameData?.GameTime ?? 0;
        var phase = time < 14 * 60 ? "phase:early" : time < 25 * 60 ? "phase:mid" : "phase:late";
        var me = data.AllPlayers.FirstOrDefault(p => p.SummonerName == data.ActivePlayer?.SummonerName);
        var roleTag = (me?.Position ?? "").ToLowerInvariant() switch
        {
            "top"     => "role:top",
            "jungle"  => "role:jg",
            "middle"  => "role:mid",
            "bottom"  => "role:adc",
            "utility" => "role:sup",
            "support" => "role:sup",
            _         => ""
        };

        // Score each tip by (#matching context tags).
        // Bonus: drake/baron tags get boosted if respective objectives are active soon.
        bool drakeWindow = time > 4 * 60 && time < 35 * 60;
        bool baronWindow = time > 19 * 60;
        bool heraldWindow = time > 7 * 60 && time < 14 * 60;

        IEnumerable<(MacroTip Tip, int Score)> scored = _tips.Select(t =>
        {
            int s = 0;
            if (t.Tags.Contains(phase)) s += 2;
            if (!string.IsNullOrEmpty(roleTag) && t.Tags.Contains(roleTag)) s += 3;
            if (drakeWindow && t.Tags.Contains("drake")) s += 1;
            if (baronWindow && t.Tags.Contains("baron")) s += 1;
            if (heraldWindow && t.Tags.Contains("herald")) s += 1;
            // Light prior so role-agnostic tips can still appear when nothing else matches.
            if (s == 0 && (t.Tags.Contains("wave") || t.Tags.Contains("vision") || t.Tags.Contains("recall"))) s = 1;
            return (t, s);
        });

        var picked = scored
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(_ => Guid.NewGuid()) // small shuffle within ties so tips rotate
            .Take(max)
            .ToList();

        if (picked.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("MACRO TIPS (relevant to current state, use as guidance):");
        foreach (var (t, _) in picked)
            sb.AppendLine($"- {t.Tip}");
        return sb.ToString();
    }
}
