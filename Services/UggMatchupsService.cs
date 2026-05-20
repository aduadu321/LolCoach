using System.Net.Http;
using System.Text.Json;

namespace LolCoach.Services;

// Pulls per-enemy winrate data from u.gg's public stats CDN.
// The JSON is shaped j[region][tier][role] = [[ [enemy_id, wins, games, …deltas], … ], …]
// Decoded empirically: row[0]=enemy champion key, row[1]=wins, row[2]=games played.
public class UggMatchupsService : IDisposable
{
    private const string UrlTemplate = "https://stats2.u.gg/lol/1.5/matchups/{0}/ranked_solo_5x5/{1}/1.5.0.json";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<string, Dictionary<int, (int wins, int games)>> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public UggMatchupsService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
    }

    // Live-client position → u.gg role index. Empirically Garen (top main) had:
    //   role 1: 83 entries (JUNGLE)
    //   role 2: 65  (UTILITY/support)
    //   role 3: 41  (BOTTOM/adc) — fewest, since few champs play adc
    //   role 4: 170 (TOP) — Garen's main, sees all champs
    //   role 5: 147 (MIDDLE)
    public static int RoleIndex(string? livePosition) => (livePosition ?? "").ToUpperInvariant() switch
    {
        "JUNGLE"  => 1,
        "UTILITY" => 2,
        "SUPPORT" => 2,
        "BOTTOM"  => 3,
        "TOP"     => 4,
        "MIDDLE"  => 5,
        _         => 0
    };

    // Returns winrate (0..1) and games count for the player's champ vs enemyChampKey in their role,
    // or null if data missing.
    public async Task<(double winrate, int games)?> GetMatchupAsync(
        string patchShort, string playerChampKey, int roleIdx, int enemyChampKey, CancellationToken ct = default)
    {
        if (roleIdx == 0) return null;
        var cacheKey = $"{patchShort}|{playerChampKey}|{roleIdx}";
        Dictionary<int, (int, int)>? rowByEnemy;

        await _lock.WaitAsync(ct);
        try
        {
            if (!_cache.TryGetValue(cacheKey, out rowByEnemy))
            {
                rowByEnemy = await FetchRoleAsync(patchShort, playerChampKey, roleIdx, ct);
                if (rowByEnemy != null) _cache[cacheKey] = rowByEnemy;
            }
        }
        finally { _lock.Release(); }

        if (rowByEnemy == null) return null;
        if (!rowByEnemy.TryGetValue(enemyChampKey, out var r)) return null;
        if (r.Item2 <= 0) return null;
        return ((double)r.Item1 / r.Item2, r.Item2);
    }

    private async Task<Dictionary<int, (int wins, int games)>?> FetchRoleAsync(
        string patchShort, string playerChampKey, int roleIdx, CancellationToken ct)
    {
        try
        {
            var url = string.Format(UrlTemplate, patchShort, playerChampKey);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            using var doc = JsonDocument.Parse(bytes);

            // j["12"]["10"][roleIdx][0] = list of matchup rows
            if (!doc.RootElement.TryGetProperty("12", out var region)) return null;
            if (!region.TryGetProperty("10", out var tier)) return null;
            if (!tier.TryGetProperty(roleIdx.ToString(), out var roleArr)) return null;
            if (roleArr.GetArrayLength() == 0) return null;
            var matchups = roleArr[0];
            if (matchups.ValueKind != JsonValueKind.Array) return null;

            var result = new Dictionary<int, (int wins, int games)>();
            foreach (var row in matchups.EnumerateArray())
            {
                if (row.GetArrayLength() < 3) continue;
                var enemyKey = row[0].GetInt32();
                var wins = row[1].GetInt32();
                var games = row[2].GetInt32();
                result[enemyKey] = (wins, games);
            }
            return result;
        }
        catch { return null; }
    }

    public void Dispose() { _http.Dispose(); _lock.Dispose(); }
}
