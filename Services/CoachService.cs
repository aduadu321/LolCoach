using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LolCoach.Models;

namespace LolCoach.Services;

public record CoachConfig(string Provider, string GroqModel, string ClaudeModel, string OllamaUrl, string OllamaModel, string OAuthCredentialsPath, string Language);

public class CoachService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly CoachConfig _config;
    private readonly string? _groqKey;
    private readonly string? _anthropicKey;

    public CoachService(CoachConfig config)
    {
        _config = config;
        _groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        _anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    }

    public async Task<string> AdviseAsync(LiveGameData data, Action<string>? onChunk = null, string? metaPlaybook = null, IEnumerable<string>? recentAdvice = null, string? deltaBlock = null, CancellationToken ct = default)
    {
        var context = BuildContext(data);
        var pre = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(metaPlaybook))
            pre.AppendLine("REAL-DATA PLAYBOOK (current-patch facts):\n" + metaPlaybook);
        if (!string.IsNullOrWhiteSpace(deltaBlock))
            pre.AppendLine("WHAT CHANGED since last tick:\n" + deltaBlock);
        if (recentAdvice != null)
        {
            var prev = recentAdvice.Where(s => !string.IsNullOrWhiteSpace(s)).Take(5).ToList();
            if (prev.Count > 0)
            {
                pre.AppendLine("RECENT ADVICE you already gave (do NOT repeat verbatim — pick a NEW thing):");
                foreach (var p in prev) pre.AppendLine("  - " + p);
            }
        }
        pre.AppendLine("LIVE STATE:");
        context = pre + context;
        var systemPrompt = BuildSystemPrompt();

        try
        {
            return _config.Provider.ToLowerInvariant() switch
            {
                "claudecode" or "claude-code" or "oauth" => await CallClaudeCodeOAuthStreamingAsync(systemPrompt, context, onChunk, ct),
                "claude" => await CallClaudeAsync(systemPrompt, context, ct),
                "groq" => await CallGroqStreamingAsync(systemPrompt, context, onChunk, ct),
                _ => await CallOllamaStreamingAsync(systemPrompt, context, onChunk, ct)
            };
        }
        catch (Exception ex)
        {
            return $"[coach error] {ex.Message}";
        }
    }

    public async Task PrewarmAsync(CancellationToken ct = default)
    {
        if (!_config.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var body = new { model = _config.OllamaModel, prompt = "", keep_alive = "30m" };
            var url = _config.OllamaUrl.TrimEnd('/') + "/api/generate";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(req, ct);
            _ = await resp.Content.ReadAsStringAsync(ct);
        }
        catch { /* best effort */ }
    }

    private string BuildSystemPrompt()
    {
        var lang = _config.Language.Equals("ro", StringComparison.OrdinalIgnoreCase)
            ? "Răspunde în română."
            : "Reply in English.";
        return $"""
            You are a high-elo League of Legends coach with current-patch meta knowledge.
            You are given: champion-vs-champion ability text + scalings, u.gg matchup winrates, u.gg pro build,
            macro tips, recent events, what JUST changed, and what advice you already gave.

            REASON SILENTLY then output exactly ONE concrete next action.

            DECISION PROCEDURE (apply in order, pick the first that fires):
              (1) Combat threat: HP < 40% AND enemy in range OR you see 2+ enemies in your screen → "Recall/Disengage/Cleanse/Heal/Flash to X".
              (2) Skill window vs lane opponent: their key ability on cooldown (you saw it used in events) → "Trade now: Q+E, his W is down ~Xs".
              (3) Power spike NOW: you just hit lvl 6/11/16 or finished an item → "All-in lvl 6 — R combo for kill".
              (4) Gold threshold: gold ≥ next item component cost AND lane is safe → "Recall, buy [specific component]".
              (5) Objective timer: drake/herald/baron up in <60s → "Push wave, rotate drake in Xs".
              (6) Wave state: 3+ waves crashing into your turret → "Clear wave under turret, don't lose plates".
              (7) Side-lane tempo (mid game): no objective, lanes equal → "Push side, ward enemy jungle".
              (8) Fallback ONLY if nothing else fires: "Last-hit, ward [specific spot] before Xs".

            STYLE RULES (non-negotiable):
              - Max 90 characters. ONE imperative sentence.
              - Mention SPECIFIC names: item, ability key (Q/W/E/R), enemy champ, objective.
              - NEVER repeat advice already given in the "RECENT ADVICE" block — pick a DIFFERENT priority.
              - NEVER say generic "play safe" / "be careful" / "focus" / "play smart" — those are forbidden filler.
              - When suggesting an item, pick one that COUNTERS what the enemy laner is building (e.g. Plated → Black Cleaver/Serylda; Spectre's Cowl → Voidstaff).
              - {lang}

            EXAMPLES of strong output:
              "Recall acum, ai 1320g — Stridebreaker, Darius nu are MR."
              "All-in: E+Q+R, Darius W e down (folosit acum 8s)."
              "Drake în 35s — push waveul ăsta apoi rotate bot prin râu."
              "Cumpără Serylda — Darius are Plated, ai nevoie de armor pen."
              "Wave crash în turn — clear sub turelă, evită gank top side."
            EXAMPLES of BANNED output:
              "Play smart." | "Focus on farming." | "Be careful of Darius." | "Stay safe."
            """;
    }

    private static string BuildContext(LiveGameData d)
    {
        var sb = new StringBuilder();
        var time = d.GameData?.GameTime ?? 0;
        sb.AppendLine($"Time: {time:F0}s ({(int)(time / 60)}:{(int)(time % 60):D2})  Map: {d.GameData?.MapName}  Mode: {d.GameData?.GameMode}");

        if (d.ActivePlayer is { } me)
        {
            var meEntry = d.AllPlayers.FirstOrDefault(p => p.SummonerName == me.SummonerName);
            sb.AppendLine($"ME: {meEntry?.ChampionName} (lvl {me.Level}, pos {meEntry?.Position}) HP {me.ChampionStats?.CurrentHealth:F0}/{me.ChampionStats?.MaxHealth:F0}  Gold {me.CurrentGold:F0}");
            if (meEntry?.Scores != null)
                sb.AppendLine($"KDA: {meEntry.Scores.Kills}/{meEntry.Scores.Deaths}/{meEntry.Scores.Assists}  CS {meEntry.Scores.CreepScore}  Wards {meEntry.Scores.WardScore:F1}");
            if (meEntry?.Items?.Count > 0)
                sb.AppendLine("Items: " + string.Join(", ", meEntry.Items.Select(i => i.DisplayName)));
            if (me.Abilities != null)
                sb.AppendLine($"Abilities Q{me.Abilities.Q?.AbilityLevel} W{me.Abilities.W?.AbilityLevel} E{me.Abilities.E?.AbilityLevel} R{me.Abilities.R?.AbilityLevel}");
        }

        var myTeam = d.AllPlayers.FirstOrDefault(p => p.SummonerName == d.ActivePlayer?.SummonerName)?.Team ?? "ORDER";
        var allies = d.AllPlayers.Where(p => p.Team == myTeam && p.SummonerName != d.ActivePlayer?.SummonerName);
        var enemies = d.AllPlayers.Where(p => p.Team != myTeam);

        sb.AppendLine("ALLIES:");
        foreach (var a in allies)
            sb.AppendLine($"  {a.ChampionName} ({a.Position}) lvl{a.Level} {a.Scores?.Kills}/{a.Scores?.Deaths}/{a.Scores?.Assists} CS{a.Scores?.CreepScore}{(a.IsDead ? $" DEAD {a.RespawnTimer:F0}s" : "")}{(a.CachedRank != null ? $" [{a.CachedRank}]" : "")}");

        sb.AppendLine("ENEMIES:");
        foreach (var e in enemies)
            sb.AppendLine($"  {e.ChampionName} ({e.Position}) lvl{e.Level} {e.Scores?.Kills}/{e.Scores?.Deaths}/{e.Scores?.Assists} CS{e.Scores?.CreepScore}{(e.IsDead ? $" DEAD {e.RespawnTimer:F0}s" : "")}{(e.CachedRank != null ? $" [{e.CachedRank}]" : "")}");

        var recent = d.Events?.Events?.Where(e => e.EventTime >= time - 30).TakeLast(8).ToList();
        if (recent?.Count > 0)
        {
            sb.AppendLine("Recent events (last 30s):");
            foreach (var e in recent)
                sb.AppendLine($"  [{e.EventTime:F0}s] {e.EventName}{(e.KillerName != null ? $" by {e.KillerName}" : "")}{(e.VictimName != null ? $" on {e.VictimName}" : "")}{(e.DragonType != null ? $" ({e.DragonType})" : "")}");
        }

        return sb.ToString();
    }

    private async Task<string> CallGroqStreamingAsync(string system, string user, Action<string>? onChunk, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_groqKey))
            return "[GROQ_API_KEY missing — set env var]";

        var body = new
        {
            model = _config.GroqModel,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            temperature = 0.4,
            max_tokens = 60,
            stream = true
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _groqKey);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return $"[groq {(int)resp.StatusCode}] {err}";
        }

        var sb = new StringBuilder();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line.Substring(5).Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;
            string chunk;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("choices", out var ch)) continue;
                if (ch.GetArrayLength() == 0) continue;
                if (!ch[0].TryGetProperty("delta", out var delta)) continue;
                if (!delta.TryGetProperty("content", out var content)) continue;
                chunk = content.GetString() ?? "";
            }
            catch { continue; }
            if (chunk.Length == 0) continue;
            sb.Append(chunk);
            onChunk?.Invoke(chunk);
        }
        return sb.ToString().Trim();
    }

    private async Task<string> CallClaudeAsync(string system, string user, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_anthropicKey))
            return "[ANTHROPIC_API_KEY missing — set env var]";

        var body = new
        {
            model = _config.ClaudeModel,
            max_tokens = 200,
            system,
            messages = new object[]
            {
                new { role = "user", content = user }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", _anthropicKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return $"[claude {(int)resp.StatusCode}] {text}";

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private async Task<string> CallOllamaStreamingAsync(string system, string user, Action<string>? onChunk, CancellationToken ct)
    {
        // /no_think disables qwen3's reasoning mode (other models ignore the line).
        var userPayload = user + "\n\n/no_think";

        var body = new
        {
            model = _config.OllamaModel,
            stream = true,
            keep_alive = "30m",
            options = new { temperature = 0.4, num_predict = 60, num_ctx = 3072 },
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userPayload }
            }
        };

        var url = _config.OllamaUrl.TrimEnd('/') + "/api/chat";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return $"[ollama {(int)resp.StatusCode}] {err}";
        }

        var sb = new StringBuilder();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        bool inThink = false;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string chunk;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("message", out var msg)) continue;
                if (!msg.TryGetProperty("content", out var c)) continue;
                chunk = c.GetString() ?? "";
            }
            catch { continue; }
            if (chunk.Length == 0) continue;

            // Strip <think>…</think> spans inline.
            var visible = StripThinkInline(chunk, ref inThink);
            if (visible.Length == 0) continue;
            sb.Append(visible);
            onChunk?.Invoke(visible);
        }
        return sb.ToString().Trim();
    }

    private string? ReadOAuthToken()
    {
        try
        {
            var path = _config.OAuthCredentialsPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            if (!oauth.TryGetProperty("accessToken", out var tok)) return null;
            return tok.GetString();
        }
        catch { return null; }
    }

    private async Task<string> CallClaudeCodeOAuthStreamingAsync(string system, string user, Action<string>? onChunk, CancellationToken ct)
    {
        var token = ReadOAuthToken();
        if (string.IsNullOrWhiteSpace(token))
            return "[Claude Code OAuth token not found — run `claude /login`]";

        var body = new
        {
            model = _config.ClaudeModel,
            max_tokens = 120,
            stream = true,
            metadata = new { user_id = "lolcoach" },
            system = new object[]
            {
                new { type = "text", text = "You are Claude Code, Anthropic's official CLI for Claude." },
                new { type = "text", text = system }
            },
            messages = new object[]
            {
                new { role = "user", content = user }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            return $"[claudecode {(int)resp.StatusCode}] {err}";
        }

        var sb = new StringBuilder();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line.Substring(5).Trim();
            if (payload.Length == 0) continue;
            string chunk;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("type", out var t)) continue;
                if (t.GetString() != "content_block_delta") continue;
                if (!doc.RootElement.TryGetProperty("delta", out var delta)) continue;
                if (!delta.TryGetProperty("text", out var txt)) continue;
                chunk = txt.GetString() ?? "";
            }
            catch { continue; }
            if (chunk.Length == 0) continue;
            sb.Append(chunk);
            onChunk?.Invoke(chunk);
        }
        return sb.ToString().Trim();
    }

    private static string StripThinkInline(string s, ref bool inThink)
    {
        var outSb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (!inThink)
            {
                var openIdx = s.IndexOf("<think>", i, StringComparison.OrdinalIgnoreCase);
                if (openIdx < 0) { outSb.Append(s, i, s.Length - i); break; }
                outSb.Append(s, i, openIdx - i);
                inThink = true;
                i = openIdx + "<think>".Length;
            }
            else
            {
                var closeIdx = s.IndexOf("</think>", i, StringComparison.OrdinalIgnoreCase);
                if (closeIdx < 0) { i = s.Length; break; }
                inThink = false;
                i = closeIdx + "</think>".Length;
            }
        }
        return outSb.ToString();
    }

    private static string StripThinkBlocks(string s)
    {
        // Qwen3 and similar emit <think>…</think> chains-of-thought; strip them.
        int start;
        while ((start = s.IndexOf("<think>", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = s.IndexOf("</think>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) { s = s[..start]; break; }
            s = s.Remove(start, end - start + "</think>".Length);
        }
        return s.Trim();
    }

    public void Dispose() => _http.Dispose();
}
