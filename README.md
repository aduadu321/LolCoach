# LolCoach

Real-time League of Legends coach overlay. Transparent WPF window that polls
Riot's Live Client Data API and streams a single concrete task to do
right now, grounded in current-patch data from multiple real sources.

## Data sources injected into every prompt

- **Riot Live Client Data** (`localhost:2999`) — your champ HP/items/KDA/abilities, full 10-player state, last 30s of events.
- **Riot Games API** (your dev key, if `RIOT_API_KEY` env var set) — per-player solo-queue rank + WR.
- **Riot Data Dragon** — current patch number + base ability text + cooldowns for every champ in the game.
- **Meraki Analytics** (`cdn.merakianalytics.com`) — exact per-rank scaling numbers (`30/60/90/120/150 (+50% AD)`).
- **u.gg matchups** — your champ's WR vs each enemy in your role.
- **u.gg pro build** (Playwright scrape, sprite-map decoded against Data Dragon items) — starting items, boots, core path, 4th/5th/6th options, keystone rune, skill priority, summoner spells.
- **Local Romanian macro tip library** — 30 hand-curated tips RAG-selected by phase + role + active objectives.

## Brain (switchable in `appsettings.json` → `Provider`)

- `claudecode` (default) — reads `~/.claude/.credentials.json`, calls Anthropic API with the OAuth beta header. Uses your Claude Max subscription, no per-call cost. ~2.5s.
- `groq` — `llama-3.3-70b-versatile`, free tier (14400 req/day). ~1-2s.
- `ollama` — fully local (`gemma3:12b` default). ~9-16s.
- `claude` — Anthropic API with `ANTHROPIC_API_KEY`. Paid.

All providers stream tokens so the task appears letter-by-letter.

## UI

- One short task at a time (max 80 chars) at the top, streaming.
- History strip with last 4 tasks + timestamps.
- Lane-opponent panel: enemy champ name, KDA, current items (live).
- Drag the header to reposition. ↻ refresh. ✕ close.

## Run

```powershell
& "$env:LOCALAPPDATA\Programs\LolCoach\LolCoach.exe"
```

Or use the Start Menu / Desktop shortcut created by the MSI.

## Build

```powershell
dotnet build LolCoach.csproj -c Release
```

For the MSI:
```powershell
dotnet publish LolCoach.csproj -c Release -r win-x64 --self-contained false -o installer\publish
wix build installer\LolCoach.wxs -arch x64 -d "PublishDir=installer\publish" -ext WixToolset.UI.wixext -out LolCoach-1.0.0.msi
```

## Legal

This is a read-only overlay in the same category as Blitz.gg / U.GG Companion / Mobalytics / OP.GG Desktop. It uses Riot's officially-provided Live Client Data + Games API endpoints, does not inject into the game process, does not automate any input, does not modify game files or memory.

u.gg page scraping uses Playwright with a desktop browser user-agent. That touches u.gg's ToS, not Riot's; practical risk is at most an IP block on u.gg.

## Optional dependencies

- Python 3.10+ with `playwright` and Chromium installed (`python -m playwright install chromium`) — only for the u.gg pro-build scraper.
- Ollama (only if you switch to `Provider: ollama`).
