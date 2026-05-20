using System.Text.Json.Serialization;

namespace LolCoach.Models;

public class LiveGameData
{
    [JsonPropertyName("activePlayer")] public ActivePlayer? ActivePlayer { get; set; }
    [JsonPropertyName("allPlayers")] public List<PlayerEntry> AllPlayers { get; set; } = new();
    [JsonPropertyName("events")] public EventsBlock? Events { get; set; }
    [JsonPropertyName("gameData")] public GameDataBlock? GameData { get; set; }
}

public class ActivePlayer
{
    [JsonPropertyName("summonerName")] public string SummonerName { get; set; } = "";
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("currentGold")] public double CurrentGold { get; set; }
    [JsonPropertyName("championStats")] public ChampionStats? ChampionStats { get; set; }
    [JsonPropertyName("abilities")] public Abilities? Abilities { get; set; }
    [JsonPropertyName("fullRunes")] public object? FullRunes { get; set; }
}

public class ChampionStats
{
    [JsonPropertyName("currentHealth")] public double CurrentHealth { get; set; }
    [JsonPropertyName("maxHealth")] public double MaxHealth { get; set; }
    [JsonPropertyName("resourceValue")] public double ResourceValue { get; set; }
    [JsonPropertyName("resourceMax")] public double ResourceMax { get; set; }
    [JsonPropertyName("resourceType")] public string ResourceType { get; set; } = "";
    [JsonPropertyName("attackDamage")] public double AttackDamage { get; set; }
    [JsonPropertyName("abilityPower")] public double AbilityPower { get; set; }
    [JsonPropertyName("armor")] public double Armor { get; set; }
    [JsonPropertyName("magicResist")] public double MagicResist { get; set; }
    [JsonPropertyName("moveSpeed")] public double MoveSpeed { get; set; }
}

public class Abilities
{
    [JsonPropertyName("Q")] public AbilitySlot? Q { get; set; }
    [JsonPropertyName("W")] public AbilitySlot? W { get; set; }
    [JsonPropertyName("E")] public AbilitySlot? E { get; set; }
    [JsonPropertyName("R")] public AbilitySlot? R { get; set; }
    [JsonPropertyName("Passive")] public AbilitySlot? Passive { get; set; }
}

public class AbilitySlot
{
    [JsonPropertyName("abilityLevel")] public int AbilityLevel { get; set; }
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
}

public class PlayerEntry
{
    [JsonPropertyName("summonerName")] public string SummonerName { get; set; } = "";
    [JsonPropertyName("riotId")] public string? RiotId { get; set; }
    [JsonPropertyName("riotIdGameName")] public string? RiotIdGameName { get; set; }
    [JsonPropertyName("riotIdTagLine")] public string? RiotIdTagLine { get; set; }
    [JsonPropertyName("championName")] public string ChampionName { get; set; } = "";
    [JsonPropertyName("team")] public string Team { get; set; } = "";
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("position")] public string Position { get; set; } = "";
    [JsonPropertyName("isDead")] public bool IsDead { get; set; }
    [JsonPropertyName("respawnTimer")] public double RespawnTimer { get; set; }
    [JsonPropertyName("items")] public List<PlayerItem> Items { get; set; } = new();
    [JsonPropertyName("scores")] public PlayerScores? Scores { get; set; }
    [JsonIgnore] public string? CachedRank { get; set; }
}

public class PlayerItem
{
    [JsonPropertyName("itemID")] public int ItemId { get; set; }
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("price")] public int Price { get; set; }
    [JsonPropertyName("slot")] public int Slot { get; set; }
}

public class PlayerScores
{
    [JsonPropertyName("kills")] public int Kills { get; set; }
    [JsonPropertyName("deaths")] public int Deaths { get; set; }
    [JsonPropertyName("assists")] public int Assists { get; set; }
    [JsonPropertyName("creepScore")] public int CreepScore { get; set; }
    [JsonPropertyName("wardScore")] public double WardScore { get; set; }
}

public class EventsBlock
{
    [JsonPropertyName("Events")] public List<GameEvent> Events { get; set; } = new();
}

public class GameEvent
{
    [JsonPropertyName("EventID")] public int EventId { get; set; }
    [JsonPropertyName("EventName")] public string EventName { get; set; } = "";
    [JsonPropertyName("EventTime")] public double EventTime { get; set; }
    [JsonPropertyName("Assisters")] public List<string>? Assisters { get; set; }
    [JsonPropertyName("KillerName")] public string? KillerName { get; set; }
    [JsonPropertyName("VictimName")] public string? VictimName { get; set; }
    [JsonPropertyName("DragonType")] public string? DragonType { get; set; }
    [JsonPropertyName("Stolen")] public string? Stolen { get; set; }
    [JsonPropertyName("TurretKilled")] public string? TurretKilled { get; set; }
    [JsonPropertyName("InhibKilled")] public string? InhibKilled { get; set; }
    [JsonPropertyName("Recipient")] public string? Recipient { get; set; }
}

public class GameDataBlock
{
    [JsonPropertyName("gameMode")] public string GameMode { get; set; } = "";
    [JsonPropertyName("gameTime")] public double GameTime { get; set; }
    [JsonPropertyName("mapName")] public string MapName { get; set; } = "";
    [JsonPropertyName("mapNumber")] public int MapNumber { get; set; }
}
