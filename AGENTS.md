# AGENTS.md — Jellyfin **Track Rules** Plugin

> Per‑user playback defaults for **audio language** and **subtitle language/mode** with three scopes and strict precedence: **Series/Show** → **Library** → **Global**. Applied server‑side at playback start by sending session commands to switch tracks.

---

## 0) TL;DR for Codex

* Language: **C# / .NET 8**. Target: Jellyfin 10.9+ plugin.
* Core invariant: For each play, pick audio/subtitle indices using **per‑user rules** with precedence **Series > Library > Global**; **never** mutate media files.
* Apply choice by sending session general commands: **SetAudioStreamIndex** then **SetSubtitleStreamIndex** to the active session.
* Provide a **Series/Show page widget** so users can set per‑series defaults via two dropdowns (Audio/Subs), plus a small **Admin & Self‑Service dashboard** for Global/Library rules.

---

## 1) Problem & Goals

**Problem**: Jellyfin lets users set global language preferences, but cannot enforce different defaults per **library** and **series** for each user.

**Goals**

1. Per‑user rules at 3 scopes with fixed precedence: Series → Library → Global.
2. Server‑side enforcement at playback start (no client mods).
3. User‑friendly UI directly on the **Series/Show page** (two dropdowns, optional chip priority), plus a dashboard page.
4. Preview/"Apply now" to test on the current session without restarting playback.
5. Guardrail: optional "Don’t transcode" rule flag.

**Non‑Goals**

* Not modifying container flags (no mkvpropedit).
* Not scraping/downloading subtitles.

---

## 2) High‑Level Architecture

```
TrackRules.Plugin
├─ Core/
│  ├─ RuleModels.cs          # Rule, UserRules, enums
│  ├─ RuleStore.cs           # Load/save per‑user rules (JSON)
│  ├─ LanguageNormalizer.cs  # ISO639 mapping + aliases (en, eng, english)
│  ├─ Resolver.cs            # Precedence & stream selection
│  ├─ TranscodeGuard.cs      # “would this transcode?” check via PlaybackInfo
│  └─ SessionHook.cs         # Subscribe to playback events, send commands
├─ Api/
│  ├─ TrackRulesController.cs  # REST: get/put rules, preview, apply, helpers
│  └─ Dtos.cs                  # API DTOs separate from domain
├─ Ui/
│  ├─ dashboard.html + .js     # Admin/self‑service rule editor
│  └─ series-widget.js         # Injected widget on Series/Show page
├─ Plugin.cs                  # BasePlugin<T>, registration, web assets
└─ tests/
   ├─ ResolverTests.cs
   └─ TranscodeGuardTests.cs
```

### Data Persistence

* Store per‑user rules in `data/TrackRules/{userId}.json` (plugin data dir), schema below.
* Maintain a **version** field. Migrate on load if needed.

### Event Hook

* Subscribe to playback start (server session manager). On event:

  1. Resolve selection (audioIndex?, subIndex?).
  2. If guard says selection would transcode and rule has `dontTranscode=true`, skip.
  3. Send commands to session: `SetAudioStreamIndex`, then `SetSubtitleStreamIndex`.
  4. Log outcome.

---

## 3) Rule Model & Resolution

### 3.1 Rule Types

```jsonc
// Persisted per user
{
  "version": 1,
  "userId": "GUID",
  "rules": [
    { "scope": "Global",  "audio": ["eng","any"], "subs": ["none"], "subsMode": "Default", "dontTranscode": false, "enabled": true },
    { "scope": "Library", "targetId": "LIB_GUID",   "audio": ["jpn","eng"], "subs": ["eng"],  "subsMode": "PreferForced", "dontTranscode": false, "enabled": true },
    { "scope": "Series",  "targetId": "SER_GUID",   "audio": ["eng"],        "subs": ["eng"],  "subsMode": "Always",        "dontTranscode": true,  "enabled": true }
  ]
}
```

* `scope`: `Global | Library | Series`
* `targetId`: `LibraryId` or `SeriesId` for scoped rules
* `audio`: ordered language priorities; use `"any"` to accept any language
* `subs`: ordered languages or `["none"]`
* `subsMode`: `None | Default | PreferForced | Always | OnlyIfAudioNotPreferred`
* `dontTranscode`: if true, skip applying a change that would trigger a transcode
* `enabled`: soft disable without deleting

### 3.2 Resolution Algorithm (deterministic)

1. **Find rule** in precedence order: Series(match by SeriesId) → Library(match by Item’s LibraryId) → Global.
2. **Normalize languages** via `LanguageNormalizer` (alias table: `en → eng`, `jp → jpn`, case/spacing tolerant).
3. **Select audio track**:

   * From media streams, pick first track where `lang ∈ audio list` (respect order). If tie, prefer:

     1. `IsDefault=true`
     2. Higher channel count
     3. Codec preference (AAC/AC3/EAC3/DTS selectable order in app settings)
4. **Select subtitle track** using `subsMode`:

   * `None`: no subs → `subIndex=null`
   * `Default`: first with `IsDefault=true` and lang in list; else first default; else first in list
   * `PreferForced`: prefer `IsForced=true` in listed langs; else fall back like `Default`
   * `Always`: first lang in list; if not found and list ≠ `none`, use first available subtitle
   * `OnlyIfAudioNotPreferred`: apply like `Default` **only if** chosen audio lang ∉ preferred list
5. **Transcode guard** (optional): probe via `TranscodeGuard.WouldTranscode(...)` (see §5). If true and rule has `dontTranscode`, skip sub/audio change that causes it.
6. Return `{ audioIndex?, subIndex? }` (nullable for “no change”).

---

## 4) Public API (Plugin REST)

Base path: `/TrackRules` (subject to plugin name).

* **GET** `/user/{userId}` → `UserRules`
* **PUT** `/user/{userId}` (body: `UserRules`) → upsert rules
* **POST** `/preview` (body: `{ userId, itemId }`) → `{ scope:"Series|Library|Global", audioIndex, subIndex, reason, transcodeRisk }`
* **POST** `/apply` (body: `{ sessionId, audioIndex?, subIndex? }`) → `200` on success
* **GET** `/series/{seriesId}/languages?userId=…` → `{ audio: string[], subs: string[] }` aggregated across episodes (for widget dropdown options)

Auth: respect Jellyfin auth; a non‑admin may **only** read/write their own userId.

---

## 5) Transcode Guard

**Purpose**: Avoid degrading QoS unintentionally when switching tracks (some clients will transcode on audio/sub change).

**Implementation**

* Call server playback info with hypothetical indices and the known device profile to see if the result is DirectPlay/Remux/Transcode.
* If the hypothetical route is worse than the current route and the rule has `dontTranscode=true`, skip that selection.
* Heuristic fallback: if current route is DirectPlay and codec/container mismatch is suspected for the new stream, treat as risky.

---

## 6) UI

### 6.1 Series/Show Widget (user‑facing, simple)

**Placement**: Series/Show page right column (below overview) — titled **“Playback defaults (for you)”**.

Controls:

* **Audio** dropdown: `Inherit (Library)` | `Custom…` → chip row appears `[ENG][JPN][ANY]` (drag to reorder, autocomplete languages)
* **Subtitles** dropdown: `Off` | `Prefer forced` | `Full` | `Inherit (Library)`
* Toggle: `Don’t transcode` (per series)
* Buttons: `Save`, `Reset to inherit`, `Apply now`
* **Preview row**: “Will pick: **ENG** audio, **ENG (forced)** subs”

**Data flow**

* On open: `GET /series/{id}/languages` & `GET /user/{userId}` (extract the effective rule).
* On save: `PUT /user/{userId}` with Series rule upsert.
* On apply: `POST /apply` to current session.

### 6.2 Dashboard (admin & self‑service)

Tabs: **Global**, **Library**, **Series**.

* Rule cards read like sentences: “For **Movies**: Audio **ENG→JPN**, Subs **ENG forced**, Don’t transcode: **off**”.
* Quick templates: *Anime (JPN + ENG subs)*, *Dubs (ENG, no subs)*, *Kids (ENG, subs off)*.
* Admin extras: clone rules to users, import/export JSON.

---

## 7) Coding Standards

* C#: nullable enabled, `async`/`await`, guard clauses.
* Logging: structured (`ILogger<T>`). **No PII** in logs beyond userId (hash if needed).
* Unit tests for `Resolver` cover: precedence, forced subs, `OnlyIfAudioNotPreferred`, tie‑breakers, guard behavior.
* Public DTOs are versioned; do not leak internal models.
* Fail closed: if anything is uncertain, **do not** change tracks.

---

## 8) Definition of Done (per milestone)

**M1 – Core**

* [x] Rule models + JSON store
* [x] Resolver returns stable indices given test fixtures
* [x] SessionHook switches tracks on playback start

**M2 – REST & Preview**

* [x] `/user/{id}` CRUD
* [x] `/preview` computes selection & risk
* [x] `/apply` targets a session reliably

**M3 – Series Widget**

* [ ] Minimal two‑dropdown UI with preview & save
* [ ] Aggregated language lists across series

**M4 – Dashboard**

* [ ] Global/Library/Series tabs
* [ ] Templates + clone + import/export

**M5 – Guard & Tests**

* [ ] TranscodeGuard working against PlaybackInfo
* [ ] ≥ 90% coverage on Resolver

---

## 9) Test Fixtures (Streams)

Create JSON fixtures for media streams with combinations:

* Audio: `eng default 6ch`, `eng 2ch`, `jpn default`, `jpn commentary`, `fra`
* Subs: `eng forced`, `eng full`, `jpn`, `und`
* Edge: no subtitles; multiple forced tracks; mislabeled language.

Expected selections per rule variant are documented in `tests/expectations.md`.

---

## 10) Developer Workflow

**Prereqs**: .NET 8 SDK; a Jellyfin dev server (Docker or native).

**Build**

```bash
dotnet restore
dotnet build -c Release
```

**Install (Docker example)**

* Copy output DLLs to a bind mount: `-v $(pwd)/out:/config/plugins/TrackRules`
* Start Jellyfin; enable plugin in dashboard.

**UI dev**

* Serve `Ui/series-widget.js` with live reload (optional). Bundle to `/web/` assets for plugin.

---

## 11) Security & Privacy

* Only admins may manage other users’ rules.
* Regular users may read/write **only their own** rules.
* Rate‑limit `/apply` per session to avoid spam.
* No external network calls. No telemetry.

---

## 12) Known Client Quirks & Mitigations

* Some clients ignore subtitle index changes mid‑playback. Mitigation: send subtitle command twice with small delay; log if ignored.
* Web clients may switch from direct play to remux/transcode on audio change. Guard default is **off**; exposed as a toggle in rules.

---

## 13) Roadmap (Post‑MVP)

* Per‑folder overrides within a library.
* Rule import from `.mkv` tags (read‑only hints).
* Admin reports: show “rules with no effect” (e.g., language not present in series).
* Optional PR to `jellyfin-web` to host the Series widget natively.

---

## 14) Glossary

* **Series/Show**: Jellyfin item representing a TV series (parent of episodes).
* **Library**: Top‑level collection in Jellyfin.
* **Forced subtitles**: subs containing only on‑screen text/foreign dialogue.
* **PlaybackInfo**: Jellyfin’s API response indicating direct play/remux/transcode route.

---

## 15) Maintainer Notes

* Keep resolver pure and deterministic (no I/O). Makes testing trivial.
* Prefer additive migrations for rule JSON (map old fields → new without data loss).
* Feature flags via plugin config for risky behaviors (e.g., double‑send subtitle command).

---

## 16) Quick Issue Templates

**feat:** Series widget: add "Use current episode tracks as series default" button.

**bug:** Subtitle command ignored on WebOS vX — add retry + client capability check.

**perf:** Cache aggregated languages per series (invalidate on library scan).

---
