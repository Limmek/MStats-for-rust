# MStats – MySQL statistics logger for Rust

MStats is a Rust Dedicated Server plugin for **Oxide/uMod** and **Carbon** that writes player
statistics and server events to a MySQL database. It does one thing only: **log to the database**.
Dashboards, web pages and other plugins read from the tables; MStats itself has no UI.

|                         |                                                                                                                                |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| Plugin version          | 2.0.0                                                                                                                          |
| Database schema version | 1                                                                                                                              |
| Rust                    | Rust Dedicated Server, public branch (protocol 2633)                                                                           |
| Oxide/uMod              | Oxide.Rust 2.0.7683 (Oxide.Core 2.0.4143) with the **Oxide.MySql** extension                                                   |
| Carbon                  | Current production release (uses Carbon's built-in Oxide compatibility layer, including its `Oxide.Core.MySql` implementation) |
| MySQL                   | MySQL 8.0+ or MariaDB 10.6+ (utf8mb4)                                                                                          |

All statements run asynchronously on the framework's MySQL worker thread; the game thread is never
blocked by the database. Every dynamic value is passed as a SQL parameter.

The same `MStats.cs` runs unmodified on both frameworks. Only the folder layout and the reload
command differ:

|             | Oxide/uMod                 | Carbon                       |
| ----------- | -------------------------- | ---------------------------- |
| Plugin file | `oxide/plugins/MStats.cs`  | `carbon/plugins/MStats.cs`   |
| Config file | `oxide/config/MStats.json` | `carbon/configs/MStats.json` |
| Data files  | `oxide/data/`              | `carbon/data/`               |
| Reload      | `oxide.reload MStats`      | `c.reload MStats`            |

`mstats.info` prints which framework is running and the exact config path in use. The rest of this
document uses the Oxide paths.

---

## Installation

1. **Oxide:** make sure the Oxide MySQL extension (`RustDedicated_Data/Managed/Oxide.MySql.dll`) is
   installed. It ships with Oxide.Rust; if it is missing, download it from the Oxide.Rust release page.
   **Carbon:** nothing extra is needed – the MySQL library is part of Carbon itself.
2. Create the database and a user:

   ```sql
   CREATE DATABASE mstats DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
   CREATE USER 'mstats'@'%' IDENTIFIED BY 'a-strong-password';
   GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, DROP ON mstats.* TO 'mstats'@'%';
   ```

   `CREATE`, `ALTER` and `INDEX` are needed so the plugin can create and migrate its own tables.
   `DROP` is only needed for `mstats.drop`, `mstats.empty` and wipe truncation.
   If you enable `CreateDatabaseIfMissing` the user additionally needs the global `CREATE` privilege.
3. Copy `MStats.cs` to `oxide/plugins/` (Carbon: `carbon/plugins/`). The plugin loads, writes
   `oxide/config/MStats.json` (Carbon: `carbon/configs/MStats.json`) and stays **disabled** until you
   configure it.
4. Edit the config file (see below), set `"Enabled": true` and run `oxide.reload MStats`
   (Carbon: `c.reload MStats`, or `mstats.reload` on either).
5. Check the console: `mstats.info` prints connection state, schema state and enabled features.

---

## Configuration (`oxide/config/MStats.json`)

```json
{
  "Enabled": true,
  "Database": {
    "Host": "127.0.0.1",
    "Port": 3306,
    "Database": "mstats",
    "Username": "mstats",
    "Password": "a-strong-password",
    "CreateDatabaseIfMissing": false,
    "UseUtcTimestamps": true,
    "HealthCheckIntervalSeconds": 30,
    "ReconnectIntervalSeconds": 60,
    "MaxBufferedStatements": 20000
  },
  "Wipe": {
    "TruncateOnMapWipe": false,
    "TruncateOnForcedWipe": false
  },
  "Logging": {
    "Connections": true,
    "Gathering": true,
    "QuarryAndExcavator": true,
    "WorldItemPickups": true,
    "Crafting": true,
    "Items": true,
    "WeaponsFired": true,
    "Hits": true,
    "PvpKills": true,
    "PveKills": true,
    "Deaths": true,
    "Building": true,
    "BuildingDestroyed": true,
    "Deployables": true,
    "Cupboards": true,
    "Chat": true,
    "TeamChat": true,
    "Commands": true,
    "RconCommands": true,
    "ServerMessages": true,
    "ServerEvents": true,
    "Teams": true,
    "Missions": true,
    "Activity": true,
    "Moderation": true
  },
  "ServerStatus": {
    "Enabled": true,
    "IntervalSeconds": 300
  },
  "AdminLog": {
    "Enabled": true,
    "Keywords": [ "admin", "mod", "cheat", "hack" ]
  }
}
```

| Key                          | Meaning                                                                                                                                                                                     |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Enabled`                    | Master switch. Nothing is written while `false`.                                                                                                                                            |
| `Database.*`                 | Connection settings. The connection always uses `CharSet=utf8mb4`.                                                                                                                          |
| `CreateDatabaseIfMissing`    | Run `CREATE DATABASE IF NOT EXISTS` before connecting (needs global `CREATE`).                                                                                                              |
| `UseUtcTimestamps`           | Store all `DATETIME`/`DATE` values in UTC (recommended) or in server local time.                                                                                                            |
| `HealthCheckIntervalSeconds` | How often the connection is pinged (min 5).                                                                                                                                                 |
| `ReconnectIntervalSeconds`   | First reconnect delay after a failure (min 10). Doubles per failed attempt, up to 15 minutes.                                                                                               |
| `MaxBufferedStatements`      | Statements kept in memory while MySQL is unreachable (min 100). Oldest are dropped when full.                                                                                               |
| `Wipe.TruncateOnMapWipe`     | Empty all statistics tables when a map wipe is detected.                                                                                                                                    |
| `Wipe.TruncateOnForcedWipe`  | Empty all statistics tables only on the forced (monthly) wipe = new map **and** new Rust protocol.                                                                                          |
| `Logging.*`                  | Per-category switches; see the logging matrix below.                                                                                                                                        |
| `ServerStatus`               | Periodic server snapshot (players online/max, sleepers, join queue, entities, FPS, memory) every `IntervalSeconds` (min 30) into `mstats_server_status`, for population/performance graphs. |
| `AdminLog`                   | Chat messages containing one of the keywords get `is_flagged = 1` and are logged even when chat logging is off.                                                                             |

Invalid values are corrected with a console warning; an unreadable config file falls back to
defaults without overwriting the file.

---

## What is logged

Every row carries the player's 64-bit Steam ID. Per-day counters use
`INSERT ... ON DUPLICATE KEY UPDATE amount = amount + @n`; event tables get one row per event with
position and map grid.

| Category (`Logging.*`)                | Oxide hook(s)                                                                                                                                                                                                                                                                                  | Table                                                      | Notes                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| ------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Connections                           | `OnPlayerConnected`, `OnPlayerDisconnected`, `OnServerSave`                                                                                                                                                                                                                                    | `mstats_players`, `mstats_player_names`, `mstats_sessions` | Online time is checkpointed on every server save, so a crash loses at most one save interval. Sessions still open on startup are closed as `server restart`.                                                                                                                                                                                                                                                                                                                                      |
| Gathering                             | `OnDispenserGathered`                                                                                                                                                                                                                                                                          | `mstats_gather` (`source = dispenser`)                     | Trees, ore nodes, corpses. Amount is the final amount after gather-rate modifiers.                                                                                                                                                                                                                                                                                                                                                                                                                |
| Gathering                             | `OnDispenserBonusReceived`                                                                                                                                                                                                                                                                     | `mstats_gather` (`bonus`)                                  | Finishing bonus. Rust creates the bonus item in `ResourceDispenser.AssignFinishBonus`, separate from the normal gather path, so it is never counted twice.                                                                                                                                                                                                                                                                                                                                        |
| Gathering                             | `OnCollectiblePickedup`                                                                                                                                                                                                                                                                        | `mstats_gather` (`collectible`)                            | Ground collectibles picked up with E: hemp, mushrooms, wood/stone/metal/sulfur piles, pumpkins, corn, berries… Raised once per item created in `CollectibleEntity.DoPickup`, with the final amount the player receives.                                                                                                                                                                                                                                                                           |
| Gathering                             | `OnGrowableGathered`                                                                                                                                                                                                                                                                           | `mstats_gather` (`growable`)                               | Harvesting planted crops.                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| Gathering                             | `OnFishCaught`                                                                                                                                                                                                                                                                                 | `mstats_gather` (`fishing`)                                | One increment per fish.                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| Gathering                             | `OnSurveyGather`                                                                                                                                                                                                                                                                               | `mstats_activity` (`survey_yield`)                         | Survey charge yield is spawned on the ground, so the actual gain is logged when it is picked up (`source = pickup`). The charge itself is recorded as activity `survey_yield` with the revealed item and amount – no double counting.                                                                                                                                                                                                                                                             |
| QuarryAndExcavator                    | `OnQuarryGather`, `OnQuarryToggled`, `OnExcavatorGather`, `OnExcavatorResourceSet`                                                                                                                                                                                                             | `mstats_gather` (`quarry`, `excavator`)                    | Output is attributed to the player who last started the quarry engine / set the excavator arm's resource target. Fallback when nobody did since the plugin loaded: quarry owner (`0` for monument quarries) and `steam_id = 0` for the excavator. Starting/stopping is also logged as activity `quarry_started` / `quarry_stopped` / `excavator_target_set`.                                                                                                                                      |
| WorldItemPickups                      | `OnItemPickup`                                                                                                                                                                                                                                                                                 | `mstats_gather` (`pickup`)                                 | Any world item picked up (dropped items, survey yield lying on the ground, …).                                                                                                                                                                                                                                                                                                                                                                                                                    |
| Crafting                              | `OnItemCraftFinished`                                                                                                                                                                                                                                                                          | `mstats_crafting`                                          | Per item and day.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| Items                                 | `OnItemDropped`, `OnItemAction`, `OnHealingItemUse`, `OnPlayerDrink`, `OnItemRepair`, `OnItemResearched`, `OnTechTreeNodeUnlocked`, `OnGiveSoldItem`, `OnMixingTableFinished`                                                                                                                  | `mstats_items`                                             | `action` = `dropped`, `consume`, `unwrap`, `healed`, `drank`, `repaired`, `researched`, `unlocked`, `bought`, `bought_npc`, `mixed`, … Death drops are not counted as `dropped`.                                                                                                                                                                                                                                                                                                                  |
| WeaponsFired                          | `OnWeaponFired`, `OnRocketLaunched`, `OnExplosiveThrown`, `OnExplosiveDropped`                                                                                                                                                                                                                 | `mstats_shots`                                             | One increment per trigger pull (a shotgun shell counts once), per weapon + ammo + day.                                                                                                                                                                                                                                                                                                                                                                                                            |
| Hits                                  | `OnPlayerAttack`                                                                                                                                                                                                                                                                               | `mstats_hits`                                              | Projectile and melee hits on combat entities (players, NPCs, animals, buildings, vehicles) with body part, headshot count and summed `damage`. Damage is the raw `HitInfo` total before the target's armour is applied (Rust scales it afterwards), so treat it as "damage output", not health removed. Explosive/fire damage does not pass through `OnPlayerAttack` and is not included.                                                                                                         |
| PvpKills                              | `OnEntityDeath`                                                                                                                                                                                                                                                                                | `mstats_pvp_kills`                                         | Player killed by another player: weapon, body part, headshot, distance, both positions, and whether the victim was sleeping (`victim_sleeping`) or already wounded/downed (`victim_wounded`) when killed.                                                                                                                                                                                                                                                                                         |
| PveKills                              | `OnEntityDeath`                                                                                                                                                                                                                                                                                | `mstats_pve_kills`                                         | `victim_type` = `scientist`, `npc`, `animal`, `event` (Patrol Helicopter / Bradley).                                                                                                                                                                                                                                                                                                                                                                                                              |
| Deaths                                | `OnEntityDeath`                                                                                                                                                                                                                                                                                | `mstats_deaths`                                            | Every player death with `cause` (majority damage type) and `killer_type` = `player`, `self`, `npc`, `animal`, `patrol_helicopter`, `bradley`, `turret`, `trap`, `fire`, `vehicle`, `explosive`, `entity`, `environment`, `bleeding`, `unknown`.                                                                                                                                                                                                                                                   |
| Building                              | `OnEntityBuilt`, `OnStructureUpgraded`, `OnStructureDemolish`, `OnStructureRepair`                                                                                                                                                                                                             | `mstats_building`                                          | `action` = `placed`, `upgraded`, `demolished`, `demolished_now`, `repaired`, per block and grade.                                                                                                                                                                                                                                                                                                                                                                                                 |
| Deployables                           | `OnEntityBuilt`, `OnItemDeployed`                                                                                                                                                                                                                                                              | `mstats_deployables`                                       | Everything placed that is not a building block (furnaces, boxes, locks, turrets, …).                                                                                                                                                                                                                                                                                                                                                                                                              |
| BuildingDestroyed                     | `OnEntityDeath`                                                                                                                                                                                                                                                                                | `mstats_building_destroyed`                                | Player-owned blocks, deployables and vehicles destroyed by a player, with owner id.                                                                                                                                                                                                                                                                                                                                                                                                               |
| Cupboards                             | `OnCupboardAuthorize`, `OnCupboardAssign`, `OnCupboardDeauthorize`, `OnCupboardClearList`, `OnEntityKill`                                                                                                                                                                                      | `mstats_cupboards`                                         | Current authorization list per cupboard; rows are marked deauthorized when the list is cleared or the cupboard is destroyed.                                                                                                                                                                                                                                                                                                                                                                      |
| Chat / TeamChat                       | `OnPlayerChat`                                                                                                                                                                                                                                                                                 | `mstats_chat`                                              | `channel` = `Global`, `Team`, `Local`, `Clan`, … Keyword hits set `is_flagged`.                                                                                                                                                                                                                                                                                                                                                                                                                   |
| Commands                              | `OnPlayerCommand`, `OnServerCommand`                                                                                                                                                                                                                                                           | `mstats_commands`                                          | `source` = `chat` (`/command`) or `console` (F1). Automatic client traffic (`inventory.*`, `craft.*`, `chat.*`, …) is skipped.                                                                                                                                                                                                                                                                                                                                                                    |
| RconCommands                          | `OnServerCommand`                                                                                                                                                                                                                                                                              | `mstats_commands`                                          | `source` = `rcon` / `server`, `steam_id = NULL`. Also covers commands run by other plugins/server scripts; disable if this is too noisy.                                                                                                                                                                                                                                                                                                                                                          |
| ServerMessages                        | `OnServerMessage`                                                                                                                                                                                                                                                                              | `mstats_server_messages`                                   | Broadcasts sent by the server itself (`say`, plugin broadcasts).                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| ServerEvents                          | `OnAirdrop`, `OnCargoPlaneSignaled`, `OnSupplyDropLanded`, `OnEntitySpawned`, `OnCargoShipEgress`, `OnHelicopterDropCrate`, `OnHelicopterRetire`, `OnEntityDeath`, `OnCrateHack`, `OnCrateHackEnd`, `OnCrateLanded`, `OnExcavatorMiningToggled`, plugin lifecycle                              | `mstats_server_events`                                     | `event` = `server_started`, `server_shutdown`, `wipe`, `airdrop`, `supply_signal`, `supply_drop_landed`, `patrol_helicopter_spawned/destroyed/retired`, `cargo_ship_spawned/egress`, `bradley_spawned/destroyed`, `ch47_spawned/crate_dropped`, `locked_crate_spawned/landed/hack_started/hack_finished`, `excavator_started/stopped`. Entities are matched by type (`PatrolHelicopter`, `CargoShip`, `BradleyAPC`, `CH47HelicopterAIController`, `HackableLockedCrate`), never by prefab string. |
| Teams                                 | `OnTeamCreated`, `OnTeamAcceptInvite`, `OnTeamLeave`, `OnTeamKick`, `OnTeamMemberPromote`, `OnTeamDisbanded`                                                                                                                                                                                   | `mstats_teams`                                             |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| Missions                              | `OnMissionSucceeded`, `OnMissionFailed`                                                                                                                                                                                                                                                        | `mstats_missions`                                          |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| Activity                              | `OnPlayerRespawned`, `OnPlayerWound`, `OnPlayerRecovered`, `OnPlayerRevive`, `OnPlayerAssist`, `OnLootEntity`, `OnLootPlayer`, `OnEntityMounted`, `OnRidableAnimalClaimed`, `OnSignUpdated`, `OnRecyclerToggle`, `OnOvenToggle`, `OnQuarryToggled`, `OnExcavatorResourceSet`, `OnSurveyGather` | `mstats_activity`                                          | Per action + subject + day counters.                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| Moderation                            | `OnPlayerKicked`, `OnPlayerBanned`, `OnPlayerUnbanned`, `OnPlayerViolation`, `OnPlayerReported`                                                                                                                                                                                                | `mstats_moderation`                                        | `event` = `kicked`, `banned`, `unbanned`, `violation`, `reported`. Bans cover both the server ban list (`ban`/`banid`, with expiry) and anticheat bans (EAC / Steam VAC & game bans, `details` starts with `anticheat`). Anti-hack violations are throttled to one row per player and violation type per 30 s.                                                                                                                                                                                    |
| ServerStatus (`ServerStatus.Enabled`) | timer (`ServerStatus.IntervalSeconds`)                                                                                                                                                                                                                                                         | `mstats_server_status`                                     | Snapshot of `players_online`, `players_max`, `sleepers`, `joining`, `queued`, `entities`, `fps`, `fps_average`, `memory_mb`. Read from counters Rust already maintains (`BasePlayer.activePlayerList`, `ServerMgr.connectionQueue`, `Performance.report`); no world scanning. One row at plugin load, then every interval.                                                                                                                                                                        |

NPC players (scientists, bots) are never logged as players; they appear only as PvE victims or as
`killer_type = npc`. Player kills, player deaths, PvE kills and destroyed buildings each have their
own table.

---

## Database schema (v1)

All tables use `ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci`. Steam IDs are
`BIGINT UNSIGNED`, timestamps `DATETIME` (UTC by default), positions `FLOAT` with a `grid` column
(`VARCHAR(8)`, e.g. `K17`).

| Table                       | Purpose                                                                                                                                                                          | Unique key                                                 |
| --------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------- |
| `mstats_schema`             | Single row: `schema_version`, `plugin_version`, `updated_at`.                                                                                                                    | `id`                                                       |
| `mstats_players`            | One row per player: `name`, `last_ip`, `is_admin`, `is_online`, `first_seen`, `last_seen`, `connections`, `online_seconds`.                                                      | `steam_id`                                                 |
| `mstats_player_names`       | Every name a player has used with `first_used`/`last_used`.                                                                                                                      | `(steam_id, name)`                                         |
| `mstats_sessions`           | One row per connection: `connected_at`, `last_seen_at`, `disconnected_at`, `duration_seconds`, `disconnect_reason`, `ip`.                                                        | `id`                                                       |
| `mstats_gather`             | Daily counters: `item_shortname`, `item_name`, `source`, `amount`, `day`.                                                                                                        | `(steam_id, item_shortname, source, day)`                  |
| `mstats_crafting`           | Daily counters per crafted item.                                                                                                                                                 | `(steam_id, item_shortname, day)`                          |
| `mstats_items`              | Daily counters per `action` + item.                                                                                                                                              | `(steam_id, action, item_shortname, day)`                  |
| `mstats_shots`              | Daily counters per weapon + ammo.                                                                                                                                                | `(steam_id, weapon_shortname, ammo_shortname, day)`        |
| `mstats_hits`               | Daily counters per weapon + `target_type` + `bodypart` with `hits`, `headshots` and summed `damage`.                                                                             | `(steam_id, weapon_shortname, target_type, bodypart, day)` |
| `mstats_pvp_kills`          | Event rows: killer/victim id + name, weapon, `bodypart`, `is_headshot`, `distance`, `victim_sleeping`, `victim_wounded`, killer and victim position, `occurred_at`.              | `id`                                                       |
| `mstats_pve_kills`          | Event rows: `victim_type`, `victim_prefab`, weapon, `bodypart`, `distance`, position.                                                                                            | `id`                                                       |
| `mstats_deaths`             | Event rows: `cause`, `killer_type`, `killer_id`, `killer_name`, weapon, position.                                                                                                | `id`                                                       |
| `mstats_building`           | Daily counters per `action` + `block` + `grade`.                                                                                                                                 | `(steam_id, action, block, grade, day)`                    |
| `mstats_deployables`        | Daily counters per deployed item.                                                                                                                                                | `(steam_id, item_shortname, day)`                          |
| `mstats_building_destroyed` | Event rows: `owner_id`, `entity_type` (`block`/`deployable`/`vehicle`), `entity`, `grade`, weapon, position.                                                                     | `id`                                                       |
| `mstats_cupboards`          | One row per cupboard + player: `is_authorized`, `authorized_at`, `deauthorized_at`, cupboard position.                                                                           | `(cupboard_id, steam_id)`                                  |
| `mstats_chat`               | Event rows: `channel`, `message` (TEXT), `is_admin`, `is_flagged`.                                                                                                               | `id`                                                       |
| `mstats_commands`           | Event rows: `source`, `command`, `arguments`.                                                                                                                                    | `id`                                                       |
| `mstats_server_messages`    | Event rows: `message`.                                                                                                                                                           | `id`                                                       |
| `mstats_server_events`      | Event rows: `event`, `entity`, `entity_id`, optional player, position, `details`.                                                                                                | `id`                                                       |
| `mstats_wipes`              | One row per detected wipe: `kind` (`initial`/`map`/`forced`), `wipe_id`, `level`, `seed`, `world_size`, `protocol_network`, `protocol_save`, `save_created_at`. Never truncated. | `id`                                                       |
| `mstats_activity`           | Daily counters per `action` + `subject`.                                                                                                                                         | `(steam_id, action, subject, day)`                         |
| `mstats_moderation`         | Event rows: `event`, `details`, optional `actor_id`/`actor_name` (e.g. the reporter).                                                                                            | `id`                                                       |
| `mstats_teams`              | Event rows: `team_id`, `event`, member, `actor_id`, `member_count`.                                                                                                              | `id`                                                       |
| `mstats_missions`           | Event rows: `mission`, `result`, `reason`.                                                                                                                                       | `id`                                                       |
| `mstats_server_status`      | Periodic snapshots: `players_online`, `players_max`, `sleepers`, `joining`, `queued`, `entities`, `fps`, `fps_average`, `memory_mb`, `recorded_at`.                              | `id`                                                       |

### Schema versioning

`MStats.DatabaseSchemaVersion` is stored in `mstats_schema`. On every connect the plugin creates
missing tables (`CREATE TABLE IF NOT EXISTS`), runs the forward-only migrations registered in
`Migrations` for versions between the stored and the expected one, and updates the schema row.
Migrations never drop data. If the database schema is *newer* than the plugin expects, the plugin
refuses to write and asks you to update the plugin.

---

## Console commands

All commands work from the server console, RCON and the in-game F1 console (requires admin or the
`mstats.admin` permission).

| Command                | Description                                                                                                                                                                                                                                                                                                    |
| ---------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `mstats.info`          | Diagnostics: plugin/schema version, Rust protocol, framework (Oxide or Carbon) and version, config path, world info, connection state, statement counters (sent/failed/buffered/dropped), on-disk buffer, last error, open sessions, enabled/disabled logging features and per-hook event counters since load. |
| `mstats.reload`        | Reloads the plugin (works on both Oxide and Carbon).                                                                                                                                                                                                                                                           |
| `mstats.empty confirm` | Truncates every statistics table (keeps `mstats_schema` and `mstats_wipes`).                                                                                                                                                                                                                                   |
| `mstats.drop confirm`  | Drops **all** MStats tables. Reload afterwards to recreate the schema.                                                                                                                                                                                                                                         |

Both destructive commands require the literal `confirm` argument and log who ran them.

---

## Wipe handling

On startup MStats compares the current world with `oxide/data/MStats.json`:

* Changed `SaveRestore.WipeId`, `server.seed`, `server.worldsize`, or a fresh save file (`OnNewSave`) → **map wipe**.
* Map wipe **and** a changed `Protocol.network` / `Protocol.save` → **forced wipe** (the monthly update).

Each detected wipe is inserted into `mstats_wipes` and as a `wipe` server event. With
`TruncateOnMapWipe` / `TruncateOnForcedWipe` the statistics tables are emptied right after the
schema check, before any buffered statements are replayed.

---

## Robustness

* MySQL down at startup or during play: the plugin goes *offline*, buffers up to
  `MaxBufferedStatements` statements in memory and reconnects with exponential back-off
  (`ReconnectIntervalSeconds` → 15 min max). One warning is logged on loss and at most one per
  5 minutes afterwards; the server never crashes because of the database.
* The buffer is mirrored to `oxide/data/MStats_pending.json` (Carbon: `carbon/data/`) on every
  server save while offline and
  on unload/shutdown, and replayed on the next load. A hard kill therefore loses at most the
  statistics since the last server save, even when MySQL was unreachable.
* Every failed statement is retried once after reconnect and then dropped with a warning.
* `Unload`/reload closes all open sessions, writes the remaining queue (or saves it to disk) and
  closes the connection.
* Server crash: on the next start, players still marked online are reset and open sessions are
  closed with `disconnect_reason = 'server restart'`; online time up to the last save is kept.
* All values are bound as parameters (`@0 … @n`); nothing is interpolated into SQL text. The
  database name from the config is validated against `^[A-Za-z0-9_$]+$` before being used as an
  identifier.
* Text columns are truncated to their declared lengths before sending.

---

## Known limitations

* If the server is hard-killed while MySQL is unreachable, statistics buffered since the last
  server save are lost (everything up to that save is on disk and replayed).
* Quarry/excavator output is attributed to the player who last started the quarry / set the arm's
  resource target *since the plugin loaded*; after a plugin reload the fallback (owner / `0`) is used
  until someone toggles it again.
* Positions are stored with one decimal; `grid` follows Rust's in-game map grid.
* `mstats_hits.damage` is pre-armour damage and excludes explosives/fire (they do not raise
  `OnPlayerAttack`). Shotgun accuracy computed as hits/shots can exceed 100 % because each pellet
  that connects is a hit while the shell is one shot.
* Not logged because Rust offers no hook for it: kill assists and distance travelled.

---

## Development

* Target: .NET Framework 4.8 / C# latest, referencing `Assembly-CSharp.dll`, `Oxide.Core.dll`,
  `Oxide.Rust.dll`, `Oxide.MySql.dll`, `UnityEngine*.dll` and `Facepunch.*.dll` from
  `RustDedicated_Data/Managed`.
* Add a table: put its `CREATE TABLE IF NOT EXISTS` into `Tables`. Change a table: bump
  `DatabaseSchemaVersion` and register an idempotent step in `Migrations[oldVersion]`.
* Hook signatures are verified against the Oxide.Rust hook definitions and cross-checked against
  Carbon's hook manifest (`https://api.carbonmod.gg/meta/carbon/hooks.json`, every hook used is
  flagged *Oxide compatible*); when adding a hook, check the exact parameter types – a mismatched
  type means the hook silently never fires.
* Carbon compatibility: the plugin only uses `Oxide.Core` APIs that Carbon's compatibility layer
  implements (`Interface.Oxide.GetLibrary<MySql>()`, `Sql.Builder`, `DataFileSystem`, `permission`,
  `timer`, `[ConsoleCommand]`, `covalence`). Overloaded hook methods (e.g. the two `OnPlayerKicked`
  signatures) are dispatched by argument type on both frameworks. Do not add Oxide-only extension
  dependencies.
