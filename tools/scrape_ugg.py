"""Scrape u.gg champion build via headless Chromium.

Usage:
  python scrape_ugg.py <champion-slug> <role>

Roles: top|jungle|middle|adc|support
Output: JSON on stdout with starter/core/boots/items/runes/spells/skill_order.

Cache file at %LOCALAPPDATA%\\LolCoach\\cache\\ugg_<slug>_<role>_<patch>.json (24h TTL).
"""
import json
import os
import re
import sys
import time
from pathlib import Path

ROLE_SLUGS = {
    "top": "top",
    "jungle": "jungle",
    "middle": "middle",
    "mid": "middle",
    "adc": "adc",
    "bottom": "adc",
    "support": "support",
    "utility": "support",
}

CACHE_DIR = Path(os.environ["LOCALAPPDATA"]) / "LolCoach" / "cache"
CACHE_TTL_SEC = 24 * 3600


def cache_path(slug: str, role: str) -> Path:
    return CACHE_DIR / f"ugg_{slug.lower()}_{role.lower()}.json"


def load_cache(slug: str, role: str):
    p = cache_path(slug, role)
    if not p.exists():
        return None
    try:
        with p.open("r", encoding="utf-8") as f:
            data = json.load(f)
        if time.time() - data.get("_ts", 0) > CACHE_TTL_SEC:
            return None
        return data
    except Exception:
        return None


def save_cache(slug: str, role: str, data: dict):
    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    data = dict(data)
    data["_ts"] = time.time()
    with cache_path(slug, role).open("w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)


def fetch_sprite_map():
    """Load Data Dragon item data and build (sprite, x, y) -> item-name lookup."""
    import urllib.request
    # Get current patch
    req = urllib.request.Request("https://ddragon.leagueoflegends.com/api/versions.json",
                                 headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=10) as r:
        patch = json.load(r)[0]
    req = urllib.request.Request(
        f"https://ddragon.leagueoflegends.com/cdn/{patch}/data/en_US/item.json",
        headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=15) as r:
        items = json.load(r)["data"]
    sprite_map = {}
    for iid, idata in items.items():
        img = idata.get("image", {})
        sprite = img.get("sprite", "").replace(".png", "")
        key = (sprite, img.get("x", 0), img.get("y", 0))
        sprite_map[key] = idata.get("name", iid)
    return sprite_map, patch


def scrape(slug: str, role: str):
    from playwright.sync_api import sync_playwright

    sprite_map, patch = fetch_sprite_map()

    url = f"https://u.gg/lol/champions/{slug.lower()}/build?role={role}"
    out = {"source": url, "champion": slug, "role": role, "patch": patch}

    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True)
        ctx = browser.new_context(
            user_agent=("Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                        "AppleWebKit/537.36 (KHTML, like Gecko) "
                        "Chrome/126.0.0.0 Safari/537.36"),
            locale="en-US",
            viewport={"width": 1600, "height": 2400},
        )
        page = ctx.new_page()
        page.goto(url, wait_until="domcontentloaded", timeout=20000)
        page.wait_for_timeout(2000)
        # Dismiss GDPR consent banner if present.
        try:
            btn = page.locator("button.fc-cta-consent").first
            if btn.count() > 0:
                btn.click(timeout=2000)
        except Exception:
            pass
        page.wait_for_timeout(2500)
        # Trigger lazy-loaded sections.
        for y in (400, 1200, 2000, 2800, 3600):
            page.evaluate(f"window.scrollTo(0,{y})")
            page.wait_for_timeout(600)
        page.evaluate("window.scrollTo(0,0)")
        page.wait_for_timeout(400)

        # u.gg renders items as CSS sprite background-position divs (not <img>).
        # Each sprite slot is 48x48 inside item{N}.webp at /img/sprite/.
        sprites_js = """els => els
            .map(e => {
                const s = e.getAttribute('style') || '';
                const m = s.match(/sprite\\/(item\\d+)\\.webp[^)]*\\).*?background-position:\\s*(-?\\d+)(?:\\.\\d+)?px\\s*(-?\\d+)(?:\\.\\d+)?px/);
                if (!m) return null;
                // Climb up to identify the section header.
                let cur = e;
                let label = '';
                for (let i = 0; i < 8 && cur; i++) {
                    const hdr = cur.querySelector?.('.content-section_header');
                    if (hdr) { label = hdr.textContent.trim(); break; }
                    cur = cur.parentElement;
                }
                return { sprite: m[1], x: -parseInt(m[2]), y: -parseInt(m[3]), section: label };
            })
            .filter(Boolean)"""
        sprites = page.eval_on_selector_all("[style*='background-image']", sprites_js)

        sections = {}
        order_in_section = {}
        for sp in sprites:
            name = sprite_map.get((sp["sprite"], sp["x"], sp["y"]))
            if not name:
                continue
            sec = sp.get("section") or "Other"
            if sec not in sections:
                sections[sec] = []
                order_in_section[sec] = set()
            if name in order_in_section[sec]:
                continue
            order_in_section[sec].add(name)
            sections[sec].append(name)
        out["build_sections"] = sections
        out["items_seen"] = list({n for lst in sections.values() for n in lst})

        # Summoner-spell tiles. u.gg uses '/img/spell-tile/' for the d/f slot images.
        spells = page.eval_on_selector_all(
            "img",
            """els => els
                .filter(e => /\\/spell-tile\\/|\\/img\\/spell\\/(Summoner|summoner|Flash|Ignite|Teleport|Heal|Barrier|Exhaust|Cleanse|Smite|Ghost)/i.test(e.src))
                .map(e => e.alt || '').filter(Boolean)"""
        )
        out["summoner_spells"] = list(dict.fromkeys(spells))[:4]

        # Filter rune perk-image keystones + secondaries (alts already give clean names).
        runes = page.eval_on_selector_all(
            "img",
            """els => els
                .filter(e => /perk-images|\\/runes?\\//i.test(e.src))
                .map(e => e.alt || '').filter(Boolean)"""
        )
        # Reduce noise: rune section has long lists of every rune in the tree. Keep keystone + first
        # few that look like a single chosen path (alts beginning with 'The Keystone' or 'The Rune').
        keystones = [r for r in runes if r.startswith("The Keystone")][:1]
        chosen = []
        for r in runes:
            if r in chosen: continue
            if r.startswith("The Keystone"):
                if chosen and chosen[0].startswith("The Keystone"): continue
            chosen.append(r)
        out["runes_seen"] = chosen[:14]
        out["runes_keystone"] = keystones

        # Skill priority — look for highlighted Q/W/E ordering. u.gg has a table.
        try:
            skill_text = page.locator("[class*='skill-priority'], [class*='SkillPriority']").first.inner_text(timeout=2000)
            out["skill_priority"] = re.sub(r"\s+", " ", skill_text).strip()
        except Exception:
            pass

        # Pick a "headline path" from the strongest-looking sections.
        priority = ["Core Items", "Recommended Build", "Final Build"]
        core_path = []
        for k in priority:
            for sec_name, items in sections.items():
                if k.lower() in sec_name.lower():
                    core_path = items[:6]
                    break
            if core_path: break
        out["core_path"] = core_path
        out["starting_items"] = next((items for sec, items in sections.items() if "starting" in sec.lower()), [])
        out["boots"] = next((items for sec, items in sections.items() if "boot" in sec.lower()), [])

        browser.close()
    return out


def main():
    if len(sys.argv) < 3:
        print(json.dumps({"error": "usage: scrape_ugg.py <champ> <role>"}))
        sys.exit(2)
    champ = sys.argv[1]
    role_raw = sys.argv[2]
    role = ROLE_SLUGS.get(role_raw.lower())
    if role is None:
        print(json.dumps({"error": f"unknown role {role_raw}"}))
        sys.exit(2)

    cached = load_cache(champ, role)
    if cached is not None:
        print(json.dumps(cached, ensure_ascii=False))
        return

    try:
        data = scrape(champ, role)
        save_cache(champ, role, data)
        print(json.dumps(data, ensure_ascii=False))
    except Exception as e:
        print(json.dumps({"error": str(e), "champion": champ, "role": role}))
        sys.exit(1)


if __name__ == "__main__":
    main()
