using System.Text;
using LolCoach.Models;

namespace LolCoach.Services;

// Live Client Data doesn't expose player coordinates, but we can INFER the
// enemy jungler's whereabouts from CS deltas + events that name them.
// Emits a compact tracker block to inject into the coach prompt so lane
// decisions ("can I push?" / "freeze?") are jungler-aware.
public class JungleTrackerService
{
    private readonly Dictionary<string, List<Snapshot>> _history = new();
    private const int MaxHistory = 60; // ~2 minutes at 2s polls

    private record Snapshot(double Time, int Cs, int Level, int Kills, int Deaths, int Assists, bool Dead, double RespawnTimer);

    public string BuildBlock(LiveGameData d)
    {
        if (d.AllPlayers == null || d.AllPlayers.Count == 0) return "";
        var me = d.AllPlayers.FirstOrDefault(p => p.SummonerName == d.ActivePlayer?.SummonerName);
        if (me == null) return "";
        var enemyJungler = d.AllPlayers.FirstOrDefault(p =>
            p.Team != me.Team && (p.Position ?? "").Equals("JUNGLE", StringComparison.OrdinalIgnoreCase));
        var allyJungler = d.AllPlayers.FirstOrDefault(p =>
            p.Team == me.Team && (p.Position ?? "").Equals("JUNGLE", StringComparison.OrdinalIgnoreCase));

        var now = d.GameData?.GameTime ?? 0;
        UpdateHistory(now, enemyJungler);
        UpdateHistory(now, allyJungler);

        var sb = new StringBuilder();
        sb.AppendLine("JUNGLE TRACKER:");
        if (enemyJungler != null) sb.Append(FormatJungler("ENEMY", enemyJungler, d, now));
        if (allyJungler != null)  sb.Append(FormatJungler("ALLY",  allyJungler, d, now));
        // Concrete lane recommendation based on enemy jungler state.
        if (enemyJungler != null && me.Position != null && !me.Position.Equals("JUNGLE", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"  LANE GUIDANCE for {me.Position}: {LaneGuidance(enemyJungler, d, me, now)}");
        return sb.ToString();
    }

    private void UpdateHistory(double time, PlayerEntry? p)
    {
        if (p == null) return;
        if (!_history.TryGetValue(p.SummonerName, out var list))
        {
            list = new List<Snapshot>();
            _history[p.SummonerName] = list;
        }
        list.Add(new Snapshot(
            Time: time,
            Cs: p.Scores?.CreepScore ?? 0,
            Level: p.Level,
            Kills: p.Scores?.Kills ?? 0,
            Deaths: p.Scores?.Deaths ?? 0,
            Assists: p.Scores?.Assists ?? 0,
            Dead: p.IsDead,
            RespawnTimer: p.RespawnTimer));
        if (list.Count > MaxHistory) list.RemoveAt(0);
    }

    private string FormatJungler(string side, PlayerEntry jg, LiveGameData d, double now)
    {
        var hist = _history.GetValueOrDefault(jg.SummonerName) ?? new();
        var sb = new StringBuilder();
        sb.Append($"  {side} JG: {jg.ChampionName} lvl{jg.Level}  CS {jg.Scores?.CreepScore}  KDA {jg.Scores?.Kills}/{jg.Scores?.Deaths}/{jg.Scores?.Assists}");
        if (jg.IsDead) sb.Append($"  DEAD respawn {jg.RespawnTimer:F0}s");
        sb.AppendLine();

        // CS-rate signal: jungler should farm ~3-4 camps/min in early game.
        if (hist.Count >= 6) // ~12 seconds of data
        {
            var s30 = hist.FirstOrDefault(x => now - x.Time >= 30) ?? hist.First();
            var csDelta30 = (jg.Scores?.CreepScore ?? 0) - s30.Cs;
            var dt = Math.Max(1, now - s30.Time);
            var rate = csDelta30 * 60.0 / dt;
            var verdict = rate < 0.5 ? "MOVING/ganking (CS flatlined)" :
                          rate < 3.0 ? "low farm rate — may be ganking/contesting"
                                     : "farming jungle";
            sb.AppendLine($"    Last 30s: +{csDelta30} CS ({rate:F1}/min) — {verdict}");
        }

        // Last event involving the jungler — gives us a location pin.
        var lastEvt = d.Events?.Events?
            .Where(e => e.KillerName == jg.SummonerName || e.VictimName == jg.SummonerName || e.Assisters?.Contains(jg.SummonerName) == true)
            .OrderByDescending(e => e.EventTime)
            .FirstOrDefault();
        if (lastEvt != null)
        {
            var ago = now - lastEvt.EventTime;
            string loc = InferLocation(lastEvt, d, jg);
            sb.AppendLine($"    Last spotted {ago:F0}s ago: {lastEvt.EventName}{(lastEvt.VictimName != null ? $" on {lastEvt.VictimName}" : "")} → likely {loc}");
        }
        else if (now > 4 * 60)
        {
            sb.AppendLine($"    No event sightings this game — assume in own jungle.");
        }
        return sb.ToString();
    }

    private static string InferLocation(GameEvent e, LiveGameData d, PlayerEntry jg)
    {
        // If kill/assist on a laner whose position we know, use that lane.
        var target = e.VictimName;
        if (!string.IsNullOrEmpty(target))
        {
            var victim = d.AllPlayers.FirstOrDefault(p => p.SummonerName == target);
            if (victim != null && !string.IsNullOrEmpty(victim.Position))
                return $"{victim.Position} lane";
        }
        if (e.DragonType != null) return "DRAGON pit (bot side)";
        if (e.EventName == "HeraldKill" || (e.EventName?.Contains("Herald") ?? false)) return "HERALD pit (top side river)";
        if (e.EventName == "BaronKill" || (e.EventName?.Contains("Baron") ?? false)) return "BARON pit (top side river)";
        if (e.TurretKilled != null) return $"near {e.TurretKilled} turret";
        return "unknown";
    }

    private string LaneGuidance(PlayerEntry jg, LiveGameData d, PlayerEntry me, double now)
    {
        // Rules of thumb based on jungler state — keep terse, the LLM elaborates.
        if (jg.IsDead) return $"jungler DEAD for {jg.RespawnTimer:F0}s → free push window";
        var hist = _history.GetValueOrDefault(jg.SummonerName) ?? new();
        if (hist.Count >= 6)
        {
            var s20 = hist.FirstOrDefault(x => now - x.Time >= 20) ?? hist.First();
            var csDelta = (jg.Scores?.CreepScore ?? 0) - s20.Cs;
            if (csDelta < 1) return "jungler not farming → likely roaming/ganking, ward + don't overextend";
        }
        var lastEvt = d.Events?.Events?
            .Where(e => e.KillerName == jg.SummonerName || e.VictimName == jg.SummonerName || e.Assisters?.Contains(jg.SummonerName) == true)
            .OrderByDescending(e => e.EventTime)
            .FirstOrDefault();
        if (lastEvt != null)
        {
            var ago = now - lastEvt.EventTime;
            if (ago > 60) return "jungler unaccounted for 60s+ → assume rotating, ward river/tribush";
            var loc = InferLocation(lastEvt, d, jg);
            // If they were on the OPPOSITE side of map from you, it's safe to push.
            var lane = (me.Position ?? "").ToUpperInvariant();
            var locUp = loc.ToUpperInvariant();
            if ((lane == "TOP" && (locUp.Contains("BOTTOM") || locUp.Contains("DRAGON"))) ||
                (lane == "BOTTOM" && (locUp.Contains("TOP") || locUp.Contains("HERALD") || locUp.Contains("BARON"))))
                return $"jungler last seen {loc} ({ago:F0}s ago) — SAFE to push your lane hard";
            return $"jungler last seen {loc} ({ago:F0}s ago) — cautious push";
        }
        return "no jungler sightings — play default, ward defensive";
    }
}
