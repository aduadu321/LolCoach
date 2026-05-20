using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LolCoach.Services;

// Pulls live champion / item facts from Riot's official Data Dragon CDN
// (no auth, no key, no scraping) so the coach reasons over CURRENT-PATCH ability
// descriptions instead of whatever was in the LLM training data.
public class MetaDataService : IDisposable
{
    private const string DdragonBase = "https://ddragon.leagueoflegends.com";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private string? _patch;
    private Dictionary<string, ChampionShort>? _champIndex; // name -> {id,key}
    private readonly Dictionary<string, ChampionDetails> _detailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public MetaDataService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
    }

    public string? Patch => _patch;
    public string? PatchShort => _patch == null ? null : string.Join("_", _patch.Split('.').Take(2));
    public bool Ready => _patch != null && _champIndex != null;

    public async Task<string?> GetChampionKeyAsync(string name, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        if (_champIndex != null && _champIndex.TryGetValue(name, out var s)) return s.Key;
        return null;
    }

    public async Task EnsureInitAsync(CancellationToken ct = default)
    {
        if (Ready) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (Ready) return;
            var versions = await GetJsonAsync($"{DdragonBase}/api/versions.json", ct);
            if (versions == null) return;
            _patch = versions.RootElement[0].GetString();

            var champs = await GetJsonAsync($"{DdragonBase}/cdn/{_patch}/data/en_US/champion.json", ct);
            if (champs == null) return;
            var data = champs.RootElement.GetProperty("data");
            var idx = new Dictionary<string, ChampionShort>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in data.EnumerateObject())
            {
                var v = e.Value;
                idx[v.GetProperty("name").GetString() ?? e.Name] = new ChampionShort(
                    Id: v.GetProperty("id").GetString() ?? e.Name,
                    Key: v.GetProperty("key").GetString() ?? "",
                    Name: v.GetProperty("name").GetString() ?? e.Name);
                idx[v.GetProperty("id").GetString() ?? e.Name] = idx[v.GetProperty("name").GetString() ?? e.Name];
            }
            _champIndex = idx;
        }
        finally { _initLock.Release(); }
    }

    public async Task<ChampionDetails?> GetChampionDetailsAsync(string championName, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        if (_patch == null || _champIndex == null) return null;
        if (_detailCache.TryGetValue(championName, out var cached)) return cached;
        if (!_champIndex.TryGetValue(championName, out var shortInfo)) return null;

        var ddTask = GetJsonAsync($"{DdragonBase}/cdn/{_patch}/data/en_US/champion/{shortInfo.Id}.json", ct);
        var mkTask = GetJsonAsync($"https://cdn.merakianalytics.com/riot/lol/resources/latest/en-US/champions/{shortInfo.Id}.json", ct);
        await Task.WhenAll(ddTask, mkTask);
        var doc = await ddTask;
        var meraki = await mkTask;
        if (doc == null) return null;
        var c = doc.RootElement.GetProperty("data").GetProperty(shortInfo.Id);

        var tags = new List<string>();
        if (c.TryGetProperty("tags", out var t))
            foreach (var tag in t.EnumerateArray()) tags.Add(tag.GetString() ?? "");

        string passiveName = "", passiveDesc = "";
        if (c.TryGetProperty("passive", out var p))
        {
            passiveName = p.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
            passiveDesc = p.TryGetProperty("description", out var pd) ? Sanitize(pd.GetString()) : "";
        }

        // If Meraki has a richer passive description, prefer it.
        var merakiAbilities = meraki?.RootElement.TryGetProperty("abilities", out var mAb) == true ? (JsonElement?)mAb : null;
        if (merakiAbilities is { } mab && mab.TryGetProperty("P", out var mp) && mp.GetArrayLength() > 0)
        {
            var passive = mp[0];
            if (passive.TryGetProperty("name", out var pn)) passiveName = pn.GetString() ?? passiveName;
            passiveDesc = FormatMerakiAbility(passive, "P");
        }

        var spells = new List<SpellDetails>();
        var slots = new[] { "Q", "W", "E", "R" };
        if (c.TryGetProperty("spells", out var sps))
        {
            var spArr = sps.EnumerateArray().ToArray();
            for (int i = 0; i < spArr.Length; i++)
            {
                var s = spArr[i];
                string ddName = s.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
                string ddDesc = s.TryGetProperty("description", out var sd) ? Sanitize(sd.GetString()) : "";
                string cd = s.TryGetProperty("cooldownBurn", out var sc) ? sc.GetString() ?? "" : "";
                string rng = s.TryGetProperty("rangeBurn", out var sr) ? sr.GetString() ?? "" : "";

                // Overlay Meraki numbers if available.
                string mkDesc = "";
                if (merakiAbilities is { } ma && i < slots.Length && ma.TryGetProperty(slots[i], out var mSlot) && mSlot.GetArrayLength() > 0)
                    mkDesc = FormatMerakiAbility(mSlot[0], slots[i]);

                spells.Add(new SpellDetails(
                    Key: s.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : "",
                    Name: ddName,
                    Description: !string.IsNullOrEmpty(mkDesc) ? mkDesc : ddDesc,
                    Cooldown: cd,
                    Range: rng));
            }
        }

        var details = new ChampionDetails(
            Id: shortInfo.Id,
            Name: shortInfo.Name,
            Title: c.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "",
            Tags: tags,
            PassiveName: passiveName,
            PassiveDescription: passiveDesc,
            Spells: spells);
        _detailCache[championName] = details;
        return details;
    }

    // Meraki abilities are nested { effects: [{description, leveling:[{attribute, modifiers:[{values,units}]}]}] }
    // We format into something compact and dense like "30/60/90/120/150 (+50% AD) phys dmg".
    private static string FormatMerakiAbility(JsonElement ability, string slot)
    {
        var sb = new StringBuilder();
        string? cdStr = null;
        if (ability.TryGetProperty("cooldown", out var cdEl) &&
            cdEl.TryGetProperty("modifiers", out var cdMods) &&
            cdMods.GetArrayLength() > 0 &&
            cdMods[0].TryGetProperty("values", out var cdVals))
        {
            cdStr = string.Join("/", cdVals.EnumerateArray().Select(v => v.GetDouble().ToString("0.##")));
        }

        if (cdStr != null) sb.Append("cd ").Append(cdStr).Append("s. ");

        if (ability.TryGetProperty("effects", out var effects))
        {
            int n = 0;
            foreach (var e in effects.EnumerateArray())
            {
                if (n++ > 1) break; // first 2 effects only — keeps prompt compact
                if (e.TryGetProperty("description", out var de)) sb.Append(Shorten(Sanitize(de.GetString()), 120));
                if (e.TryGetProperty("leveling", out var lv) && lv.GetArrayLength() > 0)
                {
                    sb.Append(" [");
                    bool firstL = true;
                    foreach (var l in lv.EnumerateArray())
                    {
                        if (!firstL) sb.Append(" / ");
                        firstL = false;
                        var attr = l.TryGetProperty("attribute", out var at) ? at.GetString() ?? "" : "";
                        sb.Append(attr).Append(": ");
                        if (l.TryGetProperty("modifiers", out var mods))
                        {
                            bool firstM = true;
                            foreach (var m in mods.EnumerateArray())
                            {
                                if (!firstM) sb.Append(" + ");
                                firstM = false;
                                if (m.TryGetProperty("values", out var vals))
                                {
                                    // Truncate per-level value list to ranks (5 for Q/W/E, 3 for R, 18 for level scaling).
                                    var arr = vals.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                                    if (arr.Length > 5)
                                    {
                                        // Show min..max for long arrays (level-scaling).
                                        sb.Append($"{arr[0]:0.##}-{arr[^1]:0.##}");
                                    }
                                    else
                                    {
                                        sb.Append(string.Join("/", arr.Select(v => v.ToString("0.##"))));
                                    }
                                }
                                if (m.TryGetProperty("units", out var units) && units.GetArrayLength() > 0)
                                {
                                    var unit = units[0].GetString() ?? "";
                                    if (!string.IsNullOrEmpty(unit)) sb.Append(unit);
                                }
                            }
                        }
                    }
                    sb.Append("]");
                }
                sb.Append("  ");
            }
        }
        return sb.ToString().Trim();
    }

    public async Task<string> BuildPlaybookAsync(string? myChampion, IEnumerable<string> enemyChampions, CancellationToken ct = default)
    {
        await EnsureInitAsync(ct);
        if (_patch == null) return "";

        var sb = new StringBuilder();
        sb.AppendLine($"Patch: {_patch}");

        async Task DumpAsync(string? name, string label)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var d = await GetChampionDetailsAsync(name!, ct);
            if (d == null) return;
            sb.AppendLine($"{label} {d.Name} ({string.Join("/", d.Tags)})  Passive: {d.PassiveName} — {Shorten(d.PassiveDescription, 140)}");
            foreach (var sp in d.Spells)
                sb.AppendLine($"  {sp.Key.Substring(Math.Max(0, sp.Key.Length - 1))} {sp.Name} cd {sp.Cooldown}s rng {sp.Range}: {Shorten(sp.Description, 140)}");
        }

        await DumpAsync(myChampion, "ME=");
        foreach (var enemy in enemyChampions) await DumpAsync(enemy, "EN=");
        return sb.ToString();
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return JsonDocument.Parse(bytes);
        }
        catch { return null; }
    }

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Riot ability descriptions contain HTML tags like <br>, <physicalDamage>, etc. Strip them.
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        foreach (var ch in s)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; sb.Append(' '); continue; }
            if (!inTag) sb.Append(ch);
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    public void Dispose() { _http.Dispose(); _initLock.Dispose(); }
}

public record ChampionShort(string Id, string Key, string Name);
public record SpellDetails(string Key, string Name, string Description, string Cooldown, string Range);
public record ChampionDetails(
    string Id, string Name, string Title, List<string> Tags,
    string PassiveName, string PassiveDescription,
    List<SpellDetails> Spells);
