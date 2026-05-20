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

    public async Task<string> AdviseAsync(LiveGameData data, Action<string>? onChunk = null, string? metaPlaybook = null, CancellationToken ct = default)
    {
        var context = BuildContext(data);
        if (!string.IsNullOrWhiteSpace(metaPlaybook))
            context = "REAL-DATA PLAYBOOK (current-patch facts from Riot Data Dragon):\n" + metaPlaybook + "\nLIVE STATE:\n" + context;
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
            You are a real-time League of Legends coach with current-patch meta knowledge.
            Output EXACTLY ONE single concrete next action for the player.
            Hard rules:
              - Max 80 characters total. ONE imperative sentence.
              - No bullets, no lists, no markdown, no preamble, no "you should".
              - Pick the SINGLE highest-priority thing right now using this priority:
                  1) immediate combat/threat (low HP, all-in window, gank incoming)
                  2) recall+buy timing (cite the specific item to purchase by name)
                  3) wave/objective/recall window
                  4) macro positioning (roam/TP/teleport)
              - When recommending an item, pick from current-patch S-tier builds for the champion + role + matchup.
              - When the enemy is building defensive (Plated Steelcaps, Tabi, MR items), recommend penetration items (Black Cleaver, Serylda's, Voidstaff).
              - Be specific: name the item/lane/objective/enemy by name when relevant.
            Examples of good output:
              "Recall acum, ai 1300g — cumpără Stridebreaker (Darius are doar Phage)."
              "Freeze waveul la turn, Darius e lvl 9 cu 2 kills, nu trade."
              "Ajută drake în 25s, jungler e top side."
              "Next item: Black Cleaver — Darius are 200 armor."
            {lang}
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
            max_tokens = 60,
            stream = true,
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
