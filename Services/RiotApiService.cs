using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace LolCoach.Services;

// Wraps the official Riot Games API for enriching coach context with rank info.
// Requires RIOT_API_KEY env var (personal dev key from developer.riotgames.com).
public class RiotApiService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string? _key;
    private readonly string _platform;   // e.g. euw1, na1, kr
    private readonly string _regional;   // e.g. europe, americas, asia
    private readonly Dictionary<string, string?> _rankByRiotId = new();
    private readonly Dictionary<string, string?> _puuidByRiotId = new();
    private readonly Dictionary<string, string?> _summIdByPuuid = new();

    public RiotApiService(string platform, string regional)
    {
        _platform = platform.ToLowerInvariant();
        _regional = regional.ToLowerInvariant();
        _key = Environment.GetEnvironmentVariable("RIOT_API_KEY");
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_key);

    // Returns short rank string ("EMERALD II 67LP, 53% WR") or null.
    public async Task<string?> GetRankAsync(string gameName, string tagLine, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(gameName) || string.IsNullOrWhiteSpace(tagLine))
            return null;
        var key = $"{gameName}#{tagLine}".ToLowerInvariant();
        if (_rankByRiotId.TryGetValue(key, out var cached)) return cached;

        var puuid = await GetPuuidAsync(gameName, tagLine, ct);
        if (puuid == null) { _rankByRiotId[key] = null; return null; }

        var summId = await GetSummonerIdAsync(puuid, ct);
        if (summId == null) { _rankByRiotId[key] = null; return null; }

        var rank = await GetSoloRankAsync(summId, ct);
        _rankByRiotId[key] = rank;
        return rank;
    }

    private async Task<string?> GetPuuidAsync(string gameName, string tagLine, CancellationToken ct)
    {
        var k = $"{gameName}#{tagLine}".ToLowerInvariant();
        if (_puuidByRiotId.TryGetValue(k, out var cached)) return cached;

        var url = $"https://{_regional}.api.riotgames.com/riot/account/v1/accounts/by-riot-id/{Uri.EscapeDataString(gameName)}/{Uri.EscapeDataString(tagLine)}";
        var json = await GetJsonAsync(url, ct);
        var puuid = json?.RootElement.TryGetProperty("puuid", out var p) == true ? p.GetString() : null;
        _puuidByRiotId[k] = puuid;
        return puuid;
    }

    private async Task<string?> GetSummonerIdAsync(string puuid, CancellationToken ct)
    {
        if (_summIdByPuuid.TryGetValue(puuid, out var cached)) return cached;

        var url = $"https://{_platform}.api.riotgames.com/lol/summoner/v4/summoners/by-puuid/{puuid}";
        var json = await GetJsonAsync(url, ct);
        var id = json?.RootElement.TryGetProperty("id", out var v) == true ? v.GetString() : null;
        _summIdByPuuid[puuid] = id;
        return id;
    }

    private async Task<string?> GetSoloRankAsync(string summonerId, CancellationToken ct)
    {
        var url = $"https://{_platform}.api.riotgames.com/lol/league/v4/entries/by-summoner/{summonerId}";
        var json = await GetJsonAsync(url, ct);
        if (json == null) return null;
        var arr = json.RootElement;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return "UNRANKED";

        // Prefer RANKED_SOLO_5x5, fall back to first entry.
        JsonElement? best = null;
        foreach (var e in arr.EnumerateArray())
        {
            var queue = e.TryGetProperty("queueType", out var q) ? q.GetString() : "";
            if (queue == "RANKED_SOLO_5x5") { best = e; break; }
            best ??= e;
        }
        if (best is not { } entry) return "UNRANKED";

        var tier = entry.TryGetProperty("tier", out var t) ? t.GetString() : "?";
        var rank = entry.TryGetProperty("rank", out var r) ? r.GetString() : "";
        var lp = entry.TryGetProperty("leaguePoints", out var l) ? l.GetInt32() : 0;
        var wins = entry.TryGetProperty("wins", out var w) ? w.GetInt32() : 0;
        var losses = entry.TryGetProperty("losses", out var ls) ? ls.GetInt32() : 0;
        var total = wins + losses;
        var wr = total > 0 ? (int)Math.Round(100.0 * wins / total) : 0;
        return $"{tier} {rank} {lp}LP, {wr}% WR ({wins}W-{losses}L)";
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Riot-Token", _key);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(text);
        }
        catch { return null; }
    }

    public void Dispose() => _http.Dispose();
}
