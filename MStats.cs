using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ConVar;
using Oxide.Core;
using Oxide.Core.Database;
using Oxide.Core.MySql.Libraries;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("MStats", "Limmek", "2.0.0")]
    [Description("Logs player statistics and server events to MySQL")]
    public class MStats : RustPlugin
    {
        #region Constants

        /// <summary>
        /// Version of the MySQL schema this build expects. Independent of the plugin version.
        /// Bump it and register a migration in <see cref="Migrations"/> whenever a table changes.
        /// </summary>
        public const int DatabaseSchemaVersion = 1;

        private const string SchemaTable = "mstats_schema";
        private const string WipeDataFile = "MStats";
        private const string PendingDataFile = "MStats_pending";
        private const string PermissionAdmin = "mstats.admin";
        private const ulong SteamIdOffset = 76561197960265728UL;
        private const int MaxReconnectDelaySeconds = 900;
        private const int ViolationThrottleSeconds = 30;

        /// <summary>Console commands the client sends automatically during normal play; not worth a row each.</summary>
        private static readonly string[] IgnoredCommandPrefixes = { "chat.", "inventory.", "craft.", "client.", "global.respawn", "global.respawn_sleepingbag", "note.", "gametip.", "demo.", "mission." };
        private const string TableOptions = " ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";

        #endregion

        #region Fields

        private ConfigData _config;
        private MySql _mySql;
        private Connection _db;
        private DbState _state = DbState.Disabled;
        private bool _schemaReady;
        private bool _schemaInitRunning;
        private int _schemaVersionInDb = -1;
        private string _schemaError;
        private DateTime _connectedSince;
        private DateTime _nextReconnectAt = DateTime.MinValue;
        private int _reconnectDelaySeconds;
        private Timer _healthTimer;
        private Timer _statusTimer;

        private readonly Queue<PendingStatement> _pending = new Queue<PendingStatement>();
        private long _sentStatements;
        private long _failedStatements;
        private long _droppedStatements;
        private DateTime _lastOfflineLog = DateTime.MinValue;
        private DateTime? _lastErrorAt;
        private string _lastError;

        private readonly Dictionary<ulong, Session> _sessions = new Dictionary<ulong, Session>();
        private readonly Dictionary<string, long> _counters = new Dictionary<string, long>();
        private readonly Dictionary<string, DateTime> _violationSeen = new Dictionary<string, DateTime>();
        /// <summary>Quarry / excavator arm net id -> Steam ID of the player who last started it / set its target.</summary>
        private readonly Dictionary<ulong, ulong> _extractorOperators = new Dictionary<ulong, ulong>();

        private bool _newSaveDetected;
        private bool _truncatePending;
        private string _truncateReason;
        private WipeInfo _wipe;

        private enum DbState
        {
            Disabled,
            Connecting,
            Online,
            Offline
        }

        private sealed class PendingStatement
        {
            public string Text;
            public object[] Args;
            public int Attempts;

            public Sql ToSql() => Sql.Builder.Append(Text, Args ?? new object[0]);
        }

        private sealed class Session
        {
            public DateTime StartedUtc;
            public string StartedStamp;
        }

        private sealed class ItemRef
        {
            public string Shortname;
            public string Name;

            public static readonly ItemRef Unknown = new ItemRef { Shortname = "unknown", Name = "Unknown" };
        }

        #endregion

        #region Configuration

        private sealed class ConfigData
        {
            public bool Enabled = false;
            public DatabaseSettings Database = new DatabaseSettings();
            public WipeSettings Wipe = new WipeSettings();
            public LoggingSettings Logging = new LoggingSettings();
            public ServerStatusSettings ServerStatus = new ServerStatusSettings();
            public AdminLogSettings AdminLog = new AdminLogSettings();
        }

        private sealed class DatabaseSettings
        {
            public string Host = "127.0.0.1";
            public int Port = 3306;
            public string Database = "mstats";
            public string Username = "mstats";
            public string Password = "change-me";
            public bool CreateDatabaseIfMissing = false;
            public bool UseUtcTimestamps = true;
            public int HealthCheckIntervalSeconds = 30;
            public int ReconnectIntervalSeconds = 60;
            public int MaxBufferedStatements = 20000;
        }

        private sealed class WipeSettings
        {
            public bool TruncateOnMapWipe = false;
            public bool TruncateOnForcedWipe = false;
        }

        private sealed class LoggingSettings
        {
            public bool Connections = true;
            public bool Gathering = true;
            public bool QuarryAndExcavator = true;
            public bool WorldItemPickups = true;
            public bool Crafting = true;
            public bool Items = true;
            public bool WeaponsFired = true;
            public bool Hits = true;
            public bool PvpKills = true;
            public bool PveKills = true;
            public bool Deaths = true;
            public bool Building = true;
            public bool BuildingDestroyed = true;
            public bool Deployables = true;
            public bool Cupboards = true;
            public bool Chat = true;
            public bool TeamChat = true;
            public bool Commands = true;
            public bool RconCommands = true;
            public bool ServerMessages = true;
            public bool ServerEvents = true;
            public bool Teams = true;
            public bool Missions = true;
            public bool Activity = true;
            public bool Moderation = true;
        }

        /// <summary>Periodic server snapshots (population, sleepers, queue, entities, FPS, memory) for population graphs.</summary>
        private sealed class ServerStatusSettings
        {
            public bool Enabled = true;
            public int IntervalSeconds = 300;
        }

        private sealed class AdminLogSettings
        {
            public bool Enabled = true;
            public List<string> Keywords = new List<string> { "admin", "mod", "cheat", "hack" };
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file");
            _config = new ConfigData();
            SaveConfigData();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null)
                {
                    throw new Exception("configuration file is empty");
                }

                _config.Database = _config.Database ?? new DatabaseSettings();
                _config.Wipe = _config.Wipe ?? new WipeSettings();
                _config.Logging = _config.Logging ?? new LoggingSettings();
                _config.ServerStatus = _config.ServerStatus ?? new ServerStatusSettings();
                _config.AdminLog = _config.AdminLog ?? new AdminLogSettings();
                _config.AdminLog.Keywords = _config.AdminLog.Keywords ?? new List<string>();
                ValidateConfig(_config);
                SaveConfigData();
            }
            catch (Exception ex)
            {
                PrintError($"Failed to load configuration: {ex.Message}. Using defaults (the file is NOT overwritten).");
                _config = new ConfigData();
            }
        }

        private void SaveConfigData() => Config.WriteObject(_config, true);

        private void ValidateConfig(ConfigData cfg)
        {
            var db = cfg.Database;
            if (db.Port <= 0 || db.Port > 65535)
            {
                PrintWarning($"Invalid Database.Port '{db.Port}', using 3306");
                db.Port = 3306;
            }

            db.HealthCheckIntervalSeconds = Math.Max(5, db.HealthCheckIntervalSeconds);
            db.ReconnectIntervalSeconds = Math.Max(10, db.ReconnectIntervalSeconds);
            db.MaxBufferedStatements = Math.Max(100, db.MaxBufferedStatements);
            cfg.ServerStatus.IntervalSeconds = Math.Max(30, cfg.ServerStatus.IntervalSeconds);

            if (string.IsNullOrEmpty(db.Database) || !Regex.IsMatch(db.Database, @"^[A-Za-z0-9_$]+$"))
            {
                PrintWarning($"Database.Database '{db.Database}' is not a valid identifier; CreateDatabaseIfMissing is disabled.");
                db.CreateDatabaseIfMissing = false;
            }
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermissionAdmin, this);
        }

        private void OnNewSave(string filename)
        {
            // Oxide.Rust raises this when SaveRestore has to start from a fresh save, i.e. the map was wiped.
            _newSaveDetected = true;
            Puts($"New save detected ({filename}).");
        }

        private void OnServerInitialized()
        {
            if (!_config.Enabled)
            {
                PrintWarning($"MStats is disabled (\"Enabled\": false). Configure the Database section in {ConfigFilePath()} and set Enabled to true.");
                return;
            }

            _mySql = Interface.Oxide.GetLibrary<MySql>();
            if (_mySql == null)
            {
                PrintError("The MySQL library (Oxide.Core.MySql) is not available in this modding framework. MStats cannot run without it.");
                return;
            }

            _state = DbState.Connecting;
            _reconnectDelaySeconds = _config.Database.ReconnectIntervalSeconds;

            // Statements that could not be written before the last unload/shutdown are replayed first.
            LoadPendingFromDisk();

            DetectWipe();

            // Crash recovery: nothing can be online before we have seen a connect.
            Write("UPDATE mstats_players SET is_online = 0 WHERE is_online = 1");
            Write("UPDATE mstats_sessions SET disconnected_at = COALESCE(last_seen_at, connected_at), duration_seconds = TIMESTAMPDIFF(SECOND, connected_at, COALESCE(last_seen_at, connected_at)), disconnect_reason = 'server restart' WHERE disconnected_at IS NULL");

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (IsRealPlayer(player))
                {
                    OpenSession(player);
                }
            }

            ServerEvent("server_started", null, Vector3.zero, null, null, $"protocol {Protocol.printable}");

            Puts($"MStats v{Version} | schema v{DatabaseSchemaVersion} | Rust {Protocol.printable} | {FrameworkDescription()}");

            if (_config.Database.CreateDatabaseIfMissing)
            {
                CreateDatabaseThenConnect();
            }
            else
            {
                Connect();
            }

            _healthTimer = timer.Every(_config.Database.HealthCheckIntervalSeconds, HealthCheck);
            if (_config.ServerStatus.Enabled)
            {
                _statusTimer = timer.Every(_config.ServerStatus.IntervalSeconds, RecordServerStatus);
                RecordServerStatus();
            }
        }

        /// <summary>Periodic population/performance snapshot. Cheap: only reads counters Rust already maintains.</summary>
        private void RecordServerStatus()
        {
            if (_state == DbState.Disabled) return;
            try
            {
                var queue = ServerMgr.Instance != null ? ServerMgr.Instance.connectionQueue : null;
                var report = Performance.report;
                Count("status");
                Write("INSERT INTO mstats_server_status (players_online, players_max, sleepers, joining, queued, entities, fps, fps_average, memory_mb, recorded_at) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9)",
                    BasePlayer.activePlayerList.Count, ConVar.Server.maxplayers, BasePlayer.sleepingPlayerList.Count, queue != null ? queue.Joining : 0, queue != null ? queue.Queued : 0,
                    BaseNetworkable.serverEntities.Count, report.frameRate, Math.Round(report.frameRateAverage, 1), report.memoryUsageSystem, Stamp(Now));
            }
            catch (Exception ex)
            {
                PrintError($"RecordServerStatus: {ex.Message}");
            }
        }

        private void OnServerSave()
        {
            // Checkpoint online time so a crash loses at most one save interval.
            if (_state == DbState.Disabled) return;

            var nowUtc = DateTime.UtcNow;
            var stamp = Stamp(Now);
            foreach (var id in _sessions.Keys.ToList())
            {
                var session = _sessions[id];
                var seconds = (long)(nowUtc - session.StartedUtc).TotalSeconds;
                if (seconds <= 0) continue;

                Write("UPDATE mstats_players SET online_seconds = online_seconds + @0, last_seen = @1 WHERE steam_id = @2", seconds, stamp, id);
                Write("UPDATE mstats_sessions SET last_seen_at = @0 WHERE steam_id = @1 AND connected_at = @2 AND disconnected_at IS NULL", stamp, id, session.StartedStamp);
                session.StartedUtc = nowUtc;
            }

            // While MySQL is unreachable, mirror the in-memory buffer to disk so a hard kill loses nothing.
            if (_state != DbState.Online || _pending.Count > 0)
            {
                SavePendingToDisk(false);
            }
        }

        private void OnServerShutdown()
        {
            ServerEvent("server_shutdown", null, Vector3.zero, null, null, null);
        }

        private void Unload()
        {
            _healthTimer?.Destroy();
            _healthTimer = null;
            _statusTimer?.Destroy();
            _statusTimer = null;

            if (_state == DbState.Disabled || _mySql == null) return;

            foreach (var id in _sessions.Keys.ToList())
            {
                var player = BasePlayer.FindByID(id);
                CloseSession(id, player != null ? DisplayName(player) : null, "plugin unloaded");
            }
            _sessions.Clear();
            _violationSeen.Clear();
            _extractorOperators.Clear();

            // Anything still buffered is kept on disk and replayed by the next load.
            SavePendingToDisk(true);
            _pending.Clear();

            var connection = _db;
            var library = _mySql;
            _db = null;
            _state = DbState.Disabled;
            if (connection == null) return;

            // Unregister the connection now (otherwise Oxide warns about an unclosed connection right after
            // Unload). The worker thread re-opens a closed connection for the statements queued above; the
            // trailing no-op query closes the socket again once they have been executed.
            try { library.CloseDb(connection); } catch { }
            library.Query(Sql.Builder.Append("SELECT 1"), connection, _ =>
            {
                try { library.CloseDb(connection); } catch { }
            });
        }

        #endregion

        #region Database connection

        private string BuildConnectionString(bool includeDatabase)
        {
            var db = _config.Database;
            var sb = new StringBuilder();
            sb.Append("Server=").Append(Quote(db.Host)).Append(';');
            sb.Append("Port=").Append(db.Port).Append(';');
            if (includeDatabase)
            {
                sb.Append("Database=").Append(Quote(db.Database)).Append(';');
            }
            sb.Append("User=").Append(Quote(db.Username)).Append(';');
            sb.Append("Password=").Append(Quote(db.Password)).Append(';');
            // Same options Oxide.MySql applies in its own OpenDb(host, port, ...) overload, plus utf8mb4.
            sb.Append("Pooling=false;default command timeout=120;Allow Zero Datetime=true;SslMode=Disabled;AllowPublicKeyRetrieval=true;CharSet=utf8mb4;");
            return sb.ToString();
        }

        private static string Quote(string value) => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

        private void CreateDatabaseThenConnect()
        {
            Connection bootstrap;
            try
            {
                bootstrap = _mySql.OpenDb(BuildConnectionString(false), this, false);
            }
            catch (Exception ex)
            {
                PrintError($"Could not open bootstrap connection: {ex.Message}");
                Connect();
                return;
            }

            var sql = Sql.Builder.Append($"CREATE DATABASE IF NOT EXISTS `{_config.Database.Database}` DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            _mySql.Query(sql, bootstrap, rows =>
            {
                if (rows == null)
                {
                    PrintWarning("CreateDatabaseIfMissing: CREATE DATABASE failed (see the exception above). Continuing with a normal connection attempt.");
                }
                else
                {
                    Puts($"Database `{_config.Database.Database}` exists or was created.");
                }
                try { _mySql.CloseDb(bootstrap); } catch { }
                Connect();
            });
        }

        private void Connect()
        {
            if (_mySql == null || !_config.Enabled || _state == DbState.Disabled) return;

            if (_db != null)
            {
                try { _mySql.CloseDb(_db); } catch { }
                _db = null;
            }

            _state = DbState.Connecting;
            _schemaReady = false;
            try
            {
                _db = _mySql.OpenDb(BuildConnectionString(true), this, true);
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                GoOffline($"could not create MySQL connection: {ex.Message}");
                return;
            }

            var connection = _db;
            Ping(connection, ok =>
            {
                if (connection != _db) return; // superseded by a newer attempt
                if (!ok)
                {
                    GoOffline("MySQL is unavailable");
                    return;
                }

                _connectedSince = DateTime.UtcNow;
                _reconnectDelaySeconds = _config.Database.ReconnectIntervalSeconds;
                _state = DbState.Online;
                Puts($"Connected to MySQL {_config.Database.Host}:{_config.Database.Port}/{_config.Database.Database}");
                EnsureSchema();
            });
        }

        private void Ping(Connection connection, Action<bool> callback)
        {
            try
            {
                _mySql.Query(Sql.Builder.Append("SELECT 1"), connection, rows => callback(rows != null));
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                callback(false);
            }
        }

        private void HealthCheck()
        {
            if (_mySql == null || _state == DbState.Disabled) return;

            switch (_state)
            {
                case DbState.Online:
                    var connection = _db;
                    Ping(connection, ok =>
                    {
                        if (connection != _db) return;
                        if (!ok)
                        {
                            GoOffline("health check failed");
                        }
                        else if (!_schemaReady && !_schemaInitRunning)
                        {
                            EnsureSchema();
                        }
                    });
                    break;

                case DbState.Offline:
                    if (DateTime.UtcNow >= _nextReconnectAt)
                    {
                        Connect();
                    }
                    break;

                case DbState.Connecting:
                    // A ping is still in flight; the worker thread is busy or the server is slow.
                    break;
            }
        }

        /// <summary>
        /// Switches to the offline state and schedules the next reconnect with exponential back-off,
        /// so an unreachable server does not spam the Oxide log with one exception per health check.
        /// </summary>
        private void GoOffline(string why)
        {
            var wasOnline = _state == DbState.Online;
            _state = DbState.Offline;
            _schemaReady = false;

            _nextReconnectAt = DateTime.UtcNow.AddSeconds(_reconnectDelaySeconds);
            var message = wasOnline
                ? $"MySQL connection lost ({why}). Buffering statistics in memory; next reconnect in {_reconnectDelaySeconds}s."
                : $"MySQL connection failed ({why}). Buffering statistics in memory; next reconnect in {_reconnectDelaySeconds}s.";
            _reconnectDelaySeconds = Math.Min(MaxReconnectDelaySeconds, _reconnectDelaySeconds * 2);

            if (wasOnline || (DateTime.UtcNow - _lastOfflineLog).TotalMinutes >= 5)
            {
                _lastOfflineLog = DateTime.UtcNow;
                PrintWarning($"{message} (buffered: {_pending.Count}, dropped: {_droppedStatements})");
            }
        }

        private void RecordError(string message)
        {
            _lastError = message;
            _lastErrorAt = DateTime.UtcNow;
        }

        #endregion

        #region Statement queue

        /// <summary>
        /// Queues a write. Statements run on the Oxide MySQL worker thread and never block the server.
        /// While the database is unavailable they are buffered (bounded) and replayed on reconnect.
        /// </summary>
        private void Write(string sql, params object[] args)
        {
            if (_state == DbState.Disabled || _mySql == null) return;

            var statement = new PendingStatement { Text = sql, Args = args ?? new object[0] };
            if (_state == DbState.Online && _schemaReady)
            {
                Send(statement);
            }
            else
            {
                Buffer(statement);
            }
        }

        private void Buffer(PendingStatement statement)
        {
            var max = _config.Database.MaxBufferedStatements;
            while (_pending.Count >= max)
            {
                _pending.Dequeue();
                _droppedStatements++;
                if (_droppedStatements == 1 || _droppedStatements % 1000 == 0)
                {
                    PrintWarning($"Statement buffer full ({max}); oldest statistics are being dropped (total dropped: {_droppedStatements}).");
                }
            }
            _pending.Enqueue(statement);
        }

        private void Send(PendingStatement statement)
        {
            var connection = _db;
            statement.Attempts++;
            try
            {
                // Query (ExecuteReader) is used for writes too: it is the only path where Oxide.MySql
                // reports failure (null result) instead of silently returning 0 affected rows.
                _mySql.Query(statement.ToSql(), connection, rows =>
                {
                    if (rows != null)
                    {
                        _sentStatements++;
                        return;
                    }
                    StatementFailed(statement, connection);
                });
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                StatementFailed(statement, connection);
            }
        }

        private void StatementFailed(PendingStatement statement, Connection connection)
        {
            _failedStatements++;
            RecordError("statement failed (see the server log for the MySQL exception)");

            if (statement.Attempts < 2)
            {
                Buffer(statement);
            }
            else
            {
                PrintWarning($"Statement dropped after {statement.Attempts} attempts: {Truncate(statement.Text, 160)}");
            }

            if (connection == _db && _state == DbState.Online)
            {
                GoOffline("write failed");
            }
        }

        private void FlushPending()
        {
            if (_state != DbState.Online || !_schemaReady) return;

            var count = _pending.Count;
            while (_pending.Count > 0 && _state == DbState.Online)
            {
                Send(_pending.Dequeue());
            }
            if (count > 0)
            {
                Puts($"Replayed {count} buffered statement(s).");
                SavePendingToDisk(false);
            }
        }

        /// <summary>
        /// Mirrors the in-memory buffer to oxide/data/MStats_pending.json (or clears the file when the
        /// buffer is empty) so statements survive a plugin unload, server restart or hard kill.
        /// </summary>
        private void SavePendingToDisk(bool announce)
        {
            try
            {
                var list = _pending.ToList();
                Interface.Oxide.DataFileSystem.WriteObject(PendingDataFile, list);
                if (announce && list.Count > 0)
                {
                    PrintWarning($"{list.Count} buffered statement(s) could not be written because the database is unavailable; saved to {PendingFilePath()} for replay on the next load.");
                }
            }
            catch (Exception ex)
            {
                PrintError($"Could not save the statement buffer to disk: {ex.Message}");
            }
        }

        private void LoadPendingFromDisk()
        {
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(PendingDataFile)) return;

            try
            {
                var list = Interface.Oxide.DataFileSystem.ReadObject<List<PendingStatement>>(PendingDataFile);
                var restored = 0;
                if (list != null)
                {
                    foreach (var statement in list)
                    {
                        if (statement == null || string.IsNullOrEmpty(statement.Text)) continue;
                        statement.Attempts = 0;
                        Buffer(statement);
                        restored++;
                    }
                }
                if (restored > 0)
                {
                    Puts($"Restored {restored} statement(s) from {PendingFilePath()}; they are written once the database is ready.");
                }
                // Keep the file in sync with the (bounded) in-memory buffer; FlushPending clears it once written.
                SavePendingToDisk(false);
            }
            catch (Exception ex)
            {
                PrintError($"Could not read the saved statement buffer: {ex.Message}. The file is left untouched.");
            }
        }

        /// <summary>Runs a maintenance statement (DDL etc.) directly and reports success.</summary>
        private void Exec(string sql, Action<bool> done, params object[] args)
        {
            var connection = _db;
            if (connection == null || _mySql == null)
            {
                done(false);
                return;
            }
            try
            {
                _mySql.Query(Sql.Builder.Append(sql, args), connection, rows => done(rows != null));
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                done(false);
            }
        }

        private void Select(string sql, Action<List<Dictionary<string, object>>> done, params object[] args)
        {
            var connection = _db;
            if (connection == null || _mySql == null)
            {
                done(null);
                return;
            }
            try
            {
                _mySql.Query(Sql.Builder.Append(sql, args), connection, done);
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                done(null);
            }
        }

        private static void RunSteps(List<Action<Action<bool>>> steps, int index, Action<bool> done)
        {
            if (index >= steps.Count)
            {
                done(true);
                return;
            }
            steps[index](ok =>
            {
                if (!ok)
                {
                    done(false);
                    return;
                }
                RunSteps(steps, index + 1, done);
            });
        }

        #endregion

        #region Schema

        private const string Position = "pos_x FLOAT NULL, pos_y FLOAT NULL, pos_z FLOAT NULL, grid VARCHAR(8) NULL";

        /// <summary>All tables of the current schema. Statements are idempotent (CREATE TABLE IF NOT EXISTS).</summary>
        private static readonly Dictionary<string, string> Tables = new Dictionary<string, string>
        {
            [SchemaTable] =
                "CREATE TABLE IF NOT EXISTS mstats_schema (id TINYINT UNSIGNED NOT NULL, schema_version INT UNSIGNED NOT NULL, plugin_version VARCHAR(32) NOT NULL, updated_at DATETIME NOT NULL, PRIMARY KEY (id))" + TableOptions,

            ["mstats_players"] =
                "CREATE TABLE IF NOT EXISTS mstats_players (steam_id BIGINT UNSIGNED NOT NULL, name VARCHAR(128) NOT NULL, last_ip VARCHAR(45) NULL, is_admin TINYINT(1) NOT NULL DEFAULT 0, is_online TINYINT(1) NOT NULL DEFAULT 0, first_seen DATETIME NOT NULL, last_seen DATETIME NOT NULL, connections INT UNSIGNED NOT NULL DEFAULT 0, online_seconds BIGINT UNSIGNED NOT NULL DEFAULT 0, PRIMARY KEY (steam_id), KEY idx_name (name), KEY idx_last_seen (last_seen))" + TableOptions,

            ["mstats_player_names"] =
                "CREATE TABLE IF NOT EXISTS mstats_player_names (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, name VARCHAR(128) NOT NULL, first_used DATETIME NOT NULL, last_used DATETIME NOT NULL, PRIMARY KEY (id), UNIQUE KEY uq_player_name (steam_id, name))" + TableOptions,

            ["mstats_sessions"] =
                "CREATE TABLE IF NOT EXISTS mstats_sessions (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, name VARCHAR(128) NOT NULL, ip VARCHAR(45) NULL, connected_at DATETIME NOT NULL, last_seen_at DATETIME NULL, disconnected_at DATETIME NULL, duration_seconds INT UNSIGNED NULL, disconnect_reason VARCHAR(255) NULL, PRIMARY KEY (id), KEY idx_player (steam_id, connected_at), KEY idx_connected (connected_at))" + TableOptions,

            ["mstats_gather"] =
                "CREATE TABLE IF NOT EXISTS mstats_gather (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, item_shortname VARCHAR(64) NOT NULL, item_name VARCHAR(128) NOT NULL, source VARCHAR(16) NOT NULL, amount BIGINT UNSIGNED NOT NULL DEFAULT 0, day DATE NOT NULL, PRIMARY KEY (id), UNIQUE KEY uq_gather (steam_id, item_shortname, source, day), KEY idx_day (day))" + TableOptions,

            ["mstats_crafting"] =
                "CREATE TABLE IF NOT EXISTS mstats_crafting (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, item_shortname VARCHAR(64) NOT NULL, item_name VARCHAR(128) NOT NULL, amount BIGINT UNSIGNED NOT NULL DEFAULT 0, day DATE NOT NULL, PRIMARY KEY (id), UNIQUE KEY uq_craft (steam_id, item_shortname, day), KEY idx_day (day))" + TableOptions,

            ["mstats_items"] =
                "CREATE TABLE IF NOT EXISTS mstats_items (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, action VARCHAR(24) NOT NULL, item_shortname VARCHAR(64) NOT NULL, item_name VARCHAR(128) NOT NULL, amount BIGINT UNSIGNED NOT NULL DEFAULT 0, day DATE NOT NULL, PRIMARY KEY (id), UNIQUE KEY uq_item (steam_id, action, item_shortname, day), KEY idx_day (day))" + TableOptions,

            ["mstats_shots"] =
                "CREATE TABLE IF NOT EXISTS mstats_shots (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, weapon_shortname VARCHAR(64) NOT NULL, weapon_name VARCHAR(128) NOT NULL, ammo_shortname VARCHAR(64) NOT NULL, ammo_name VARCHAR(128) NOT NULL, shots BIGINT UNSIGNED NOT NULL DEFAULT 0, day DATE NOT NULL, PRIMARY KEY (id), UNIQUE KEY uq_shots (steam_id, weapon_shortname, ammo_shortname, day), KEY idx_day (day))" + TableOptions,

            ["mstats_hits"] =
                "CREATE TABLE IF NOT EXISTS mstats_hits (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, weapon_shortname VARCHAR(64) NOT NULL, target_type VARCHAR(16) NOT NULL, bodypart VARCHAR(32) NOT NULL, hits BIGINT UNSIGNED NOT NULL DEFAULT 0, headshots BIGINT UNSIGNED NOT NULL DEFAULT 0, damage DECIMAL(14,1) NOT NULL DEFAULT 0, day DATE NOT NULL, PRIMARY KEY (id), UNIQUE KEY uq_hits (steam_id, weapon_shortname, target_type, bodypart, day), KEY idx_day (day))" + TableOptions,

            ["mstats_pvp_kills"] =
                "CREATE TABLE IF NOT EXISTS mstats_pvp_kills (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, killer_id BIGINT UNSIGNED NOT NULL, killer_name VARCHAR(128) NOT NULL, victim_id BIGINT UNSIGNED NOT NULL, victim_name VARCHAR(128) NOT NULL, weapon_shortname VARCHAR(64) NOT NULL, weapon_name VARCHAR(128) NOT NULL, bodypart VARCHAR(32) NOT NULL, is_headshot TINYINT(1) NOT NULL DEFAULT 0, distance DECIMAL(8,1) NOT NULL DEFAULT 0, victim_sleeping TINYINT(1) NOT NULL DEFAULT 0, victim_wounded TINYINT(1) NOT NULL DEFAULT 0, killer_x FLOAT NULL, killer_y FLOAT NULL, killer_z FLOAT NULL, " + Position + ", occurred_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_killer (killer_id, occurred_at), KEY idx_victim (victim_id, occurred_at), KEY idx_occurred (occurred_at))" + TableOptions,

            ["mstats_pve_kills"] =
                "CREATE TABLE IF NOT EXISTS mstats_pve_kills (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, victim_type VARCHAR(16) NOT NULL, victim_prefab VARCHAR(64) NOT NULL, weapon_shortname VARCHAR(64) NOT NULL, weapon_name VARCHAR(128) NOT NULL, bodypart VARCHAR(32) NOT NULL, distance DECIMAL(8,1) NOT NULL DEFAULT 0, " + Position + ", occurred_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_player (steam_id, occurred_at), KEY idx_type (victim_type, victim_prefab), KEY idx_occurred (occurred_at))" + TableOptions,

            ["mstats_deaths"] =
                "CREATE TABLE IF NOT EXISTS mstats_deaths (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, name VARCHAR(128) NOT NULL, cause VARCHAR(32) NOT NULL, killer_type VARCHAR(24) NOT NULL, killer_id BIGINT UNSIGNED NULL, killer_name VARCHAR(128) NULL, weapon_shortname VARCHAR(64) NULL, weapon_name VARCHAR(128) NULL, " + Position + ", occurred_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_player (steam_id, occurred_at), KEY idx_killer_type (killer_type), KEY idx_occurred (occurred_at))" + TableOptions,

            ["mstats_building"] =
                "CREATE TABLE IF NOT EXISTS mstats_building (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, action VARCHAR(16) NOT NULL, block VARCHAR(64) NOT NULL, grade VARCHAR(16) NOT NULL, amount BIGINT UNSIGNED NOT NULL DEFAULT 0, day DATE NOT NULL, PRIMARY KEY (id), UNIQUE KEY uq_building (steam_id, action, block, grade, day), KEY idx_day (day))" + TableOptions,

            ["mstats_deployables"] =
                "CREATE TABLE IF NOT EXISTS mstats_deployables (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, item_shortname VARCHAR(64) NOT NULL, item_name VARCHAR(128) NOT NULL, amount BIGINT UNSIGNED NOT NULL DEFAULT 0, day DATE NOT NULL, PRIMARY KEY (id), UNIQUE KEY uq_deployable (steam_id, item_shortname, day), KEY idx_day (day))" + TableOptions,

            ["mstats_building_destroyed"] =
                "CREATE TABLE IF NOT EXISTS mstats_building_destroyed (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, owner_id BIGINT UNSIGNED NOT NULL DEFAULT 0, entity_type VARCHAR(16) NOT NULL, entity VARCHAR(64) NOT NULL, grade VARCHAR(16) NULL, weapon_shortname VARCHAR(64) NOT NULL, weapon_name VARCHAR(128) NOT NULL, " + Position + ", occurred_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_player (steam_id, occurred_at), KEY idx_owner (owner_id), KEY idx_occurred (occurred_at))" + TableOptions,

            ["mstats_cupboards"] =
                "CREATE TABLE IF NOT EXISTS mstats_cupboards (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, cupboard_id BIGINT UNSIGNED NOT NULL, steam_id BIGINT UNSIGNED NOT NULL, name VARCHAR(128) NOT NULL, is_authorized TINYINT(1) NOT NULL DEFAULT 1, " + Position + ", authorized_at DATETIME NOT NULL, deauthorized_at DATETIME NULL, PRIMARY KEY (id), UNIQUE KEY uq_cupboard_player (cupboard_id, steam_id), KEY idx_player (steam_id))" + TableOptions,

            ["mstats_chat"] =
                "CREATE TABLE IF NOT EXISTS mstats_chat (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, name VARCHAR(128) NOT NULL, channel VARCHAR(16) NOT NULL, message TEXT NOT NULL, is_admin TINYINT(1) NOT NULL DEFAULT 0, is_flagged TINYINT(1) NOT NULL DEFAULT 0, occurred_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_player (steam_id, occurred_at), KEY idx_flagged (is_flagged, occurred_at), KEY idx_occurred (occurred_at))" + TableOptions,

            ["mstats_commands"] =
                "CREATE TABLE IF NOT EXISTS mstats_commands (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NULL, name VARCHAR(128) NOT NULL, source VARCHAR(8) NOT NULL, command VARCHAR(128) NOT NULL, arguments TEXT NULL, occurred_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_player (steam_id, occurred_at), KEY idx_command (command), KEY idx_occurred (occurred_at))" + TableOptions,

            ["mstats_server_messages"] =
                "CREATE TABLE IF NOT EXISTS mstats_server_messages (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, message TEXT NOT NULL, occurred_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_occurred (occurred_at))" + TableOptions,

            ["mstats_server_events"] =
                "CREATE TABLE IF NOT EXISTS mstats_server_events (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, event VARCHAR(48) NOT NULL, entity VARCHAR(64) NULL, entity_id BIGINT UNSIGNED NULL, steam_id BIGINT UNSIGNED NULL, name VARCHAR(128) NULL, " + Position + ", details VARCHAR(255) NULL, occurred_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_event (event, occurred_at), KEY idx_occurred (occurred_at))" + TableOptions,

            ["mstats_wipes"] =
                "CREATE TABLE IF NOT EXISTS mstats_wipes (id INT UNSIGNED NOT NULL AUTO_INCREMENT, kind VARCHAR(16) NOT NULL, wipe_id VARCHAR(64) NULL, level VARCHAR(64) NULL, seed INT UNSIGNED NOT NULL, world_size INT UNSIGNED NOT NULL, protocol_network INT UNSIGNED NOT NULL, protocol_save INT UNSIGNED NOT NULL, save_created_at DATETIME NULL, detected_at DATETIME NOT NULL, PRIMARY KEY (id))" + TableOptions,

            ["mstats_activity"] =
                "CREATE TABLE IF NOT EXISTS mstats_activity (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, action VARCHAR(32) NOT NULL, subject VARCHAR(64) NOT NULL, amount BIGINT UNSIGNED NOT NULL DEFAULT 0, day DATE NOT NULL, PRIMARY KEY (id), UNIQUE KEY uq_activity (steam_id, action, subject, day), KEY idx_day (day))" + TableOptions,

            ["mstats_moderation"] =
                "CREATE TABLE IF NOT EXISTS mstats_moderation (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, name VARCHAR(128) NOT NULL, event VARCHAR(24) NOT NULL, details TEXT NULL, actor_id BIGINT UNSIGNED NULL, actor_name VARCHAR(128) NULL, occurred_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_player (steam_id, occurred_at), KEY idx_event (event, occurred_at))" + TableOptions,

            ["mstats_teams"] =
                "CREATE TABLE IF NOT EXISTS mstats_teams (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, team_id BIGINT UNSIGNED NOT NULL, event VARCHAR(16) NOT NULL, steam_id BIGINT UNSIGNED NULL, name VARCHAR(128) NULL, actor_id BIGINT UNSIGNED NULL, member_count INT UNSIGNED NULL, occurred_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_team (team_id, occurred_at), KEY idx_player (steam_id))" + TableOptions,

            ["mstats_missions"] =
                "CREATE TABLE IF NOT EXISTS mstats_missions (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, steam_id BIGINT UNSIGNED NOT NULL, mission VARCHAR(64) NOT NULL, result VARCHAR(16) NOT NULL, reason VARCHAR(32) NULL, occurred_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_player (steam_id, occurred_at))" + TableOptions,

            ["mstats_server_status"] =
                "CREATE TABLE IF NOT EXISTS mstats_server_status (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT, players_online INT UNSIGNED NOT NULL, players_max INT UNSIGNED NOT NULL, sleepers INT UNSIGNED NOT NULL, joining INT UNSIGNED NOT NULL, queued INT UNSIGNED NOT NULL, entities INT UNSIGNED NOT NULL, fps INT UNSIGNED NOT NULL, fps_average DECIMAL(6,1) NOT NULL, memory_mb BIGINT UNSIGNED NOT NULL, recorded_at DATETIME NOT NULL, PRIMARY KEY (id), KEY idx_recorded (recorded_at))" + TableOptions,
        };

        /// <summary>Tables that are never truncated by wipe handling or mstats.empty.</summary>
        private static readonly HashSet<string> PersistentTables = new HashSet<string> { SchemaTable, "mstats_wipes" };

        /// <summary>
        /// Forward-only migrations keyed by the schema version they upgrade FROM. Each step must be
        /// idempotent and must not destroy data (check information_schema before ALTER TABLE; MySQL has no
        /// ADD COLUMN IF NOT EXISTS). New tables need no migration: <see cref="Tables"/> is always applied
        /// first with CREATE TABLE IF NOT EXISTS. Empty for the first schema version.
        /// </summary>
        private static readonly Dictionary<int, Action<List<Action<Action<bool>>>, MStats>> Migrations = new Dictionary<int, Action<List<Action<Action<bool>>>, MStats>>();

        private void EnsureSchema()
        {
            if (_schemaInitRunning) return;
            _schemaInitRunning = true;
            _schemaError = null;

            Select("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()", rows =>
            {
                if (rows == null)
                {
                    _schemaInitRunning = false;
                    _schemaError = "could not read information_schema";
                    GoOffline("schema inspection failed");
                    return;
                }

                var existing = new HashSet<string>(rows.Select(r => Convert.ToString(r["TABLE_NAME"])), StringComparer.OrdinalIgnoreCase);
                if (!existing.Contains(SchemaTable))
                {
                    ApplySchema(0);
                    return;
                }

                Select("SELECT schema_version FROM " + SchemaTable + " WHERE id = 1", versionRows =>
                {
                    var version = versionRows != null && versionRows.Count > 0 ? Convert.ToInt32(versionRows[0]["schema_version"]) : 0;
                    ApplySchema(version);
                });
            });
        }

        private void ApplySchema(int fromVersion)
        {
            _schemaVersionInDb = fromVersion;

            if (fromVersion > DatabaseSchemaVersion)
            {
                _schemaInitRunning = false;
                _schemaError = $"database schema v{fromVersion} is newer than this plugin supports (v{DatabaseSchemaVersion})";
                PrintError($"{_schemaError}. Update the plugin. Statistics are not written.");
                return;
            }

            var steps = new List<Action<Action<bool>>>();
            foreach (var table in Tables)
            {
                var sql = table.Value;
                steps.Add(done => Exec(sql, done));
            }

            for (var v = fromVersion; v < DatabaseSchemaVersion && v > 0; v++)
            {
                Action<List<Action<Action<bool>>>, MStats> migration;
                if (Migrations.TryGetValue(v, out migration))
                {
                    var from = v;
                    steps.Add(done => { Puts($"Migrating schema v{from} -> v{from + 1}"); done(true); });
                    migration(steps, this);
                }
            }

            steps.Add(done => Exec("INSERT INTO " + SchemaTable + " (id, schema_version, plugin_version, updated_at) VALUES (1, @0, @1, @2) ON DUPLICATE KEY UPDATE schema_version = @0, plugin_version = @1, updated_at = @2",
                done, DatabaseSchemaVersion, Version.ToString(), Stamp(Now)));

            RunSteps(steps, 0, ok =>
            {
                _schemaInitRunning = false;
                if (!ok)
                {
                    _schemaError = "schema creation/migration failed (see the MySQL exception above)";
                    PrintError(_schemaError + ". Retrying on the next health check.");
                    return;
                }

                _schemaVersionInDb = DatabaseSchemaVersion;
                Puts(fromVersion == 0 ? $"Schema v{DatabaseSchemaVersion} created." : fromVersion < DatabaseSchemaVersion ? $"Schema migrated v{fromVersion} -> v{DatabaseSchemaVersion}." : $"Schema v{DatabaseSchemaVersion} verified.");
                SchemaReady();
            });
        }

        private void SchemaReady()
        {
            _schemaReady = true;
            if (_truncatePending)
            {
                _truncatePending = false;
                TruncateStatistics(_truncateReason, FlushPending);
            }
            else
            {
                FlushPending();
            }
        }

        private void TruncateStatistics(string reason, Action done)
        {
            PrintWarning($"Truncating all MStats statistics tables ({reason}).");
            var steps = new List<Action<Action<bool>>>();
            foreach (var table in Tables.Keys.Where(t => !PersistentTables.Contains(t)))
            {
                var name = table;
                steps.Add(ok => Exec($"TRUNCATE TABLE `{name}`", ok));
            }
            RunSteps(steps, 0, ok =>
            {
                if (ok) Puts("Statistics tables truncated.");
                else PrintError("Truncating tables failed part-way; see the MySQL exception above.");
                done?.Invoke();
            });
        }

        #endregion

        #region Wipe detection

        private sealed class WipeInfo
        {
            public string WipeId;
            public string SaveCreated;
            public int Seed;
            public int WorldSize;
            public string Level;
            public int Network;
            public int Save;
        }

        private void DetectWipe()
        {
            _wipe = new WipeInfo
            {
                WipeId = SaveRestore.WipeId,
                SaveCreated = SaveRestore.SaveCreatedTime.ToString("yyyy-MM-dd HH:mm:ss"),
                Seed = ConVar.Server.seed,
                WorldSize = ConVar.Server.worldsize,
                Level = ConVar.Server.level,
                Network = Protocol.network,
                Save = Protocol.save
            };

            WipeInfo stored = null;
            if (Interface.Oxide.DataFileSystem.ExistsDatafile(WipeDataFile))
            {
                try
                {
                    stored = Interface.Oxide.DataFileSystem.ReadObject<WipeInfo>(WipeDataFile);
                    if (stored != null && stored.Seed == 0 && string.IsNullOrEmpty(stored.WipeId)) stored = null;
                }
                catch (Exception ex)
                {
                    PrintWarning($"Could not read wipe data file: {ex.Message}");
                }
            }

            Puts($"World: {_wipe.Level} seed {_wipe.Seed} size {_wipe.WorldSize} | wipe id {_wipe.WipeId ?? "n/a"} | protocol {_wipe.Network}.{_wipe.Save}");

            string kind = null;
            if (stored == null)
            {
                kind = "initial";
            }
            else
            {
                var mapWipe = _newSaveDetected
                              || (!string.IsNullOrEmpty(stored.WipeId) && !string.IsNullOrEmpty(_wipe.WipeId) && stored.WipeId != _wipe.WipeId)
                              || stored.Seed != _wipe.Seed
                              || stored.WorldSize != _wipe.WorldSize;
                var protocolChanged = stored.Network != _wipe.Network || stored.Save != _wipe.Save;

                if (mapWipe)
                {
                    // A new map together with a new Rust protocol is the forced (monthly) wipe.
                    kind = protocolChanged ? "forced" : "map";
                    Puts(kind == "forced" ? "Detected forced wipe: new map and new Rust protocol." : "Detected map wipe: new save/seed.");

                    if ((kind == "map" && _config.Wipe.TruncateOnMapWipe) || (kind == "forced" && (_config.Wipe.TruncateOnForcedWipe || _config.Wipe.TruncateOnMapWipe)))
                    {
                        _truncatePending = true;
                        _truncateReason = kind + " wipe";
                    }
                }
                else if (protocolChanged)
                {
                    Puts("Rust protocol changed without a map wipe; statistics are kept.");
                }
            }

            if (kind != null)
            {
                Write("INSERT INTO mstats_wipes (kind, wipe_id, level, seed, world_size, protocol_network, protocol_save, save_created_at, detected_at) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8)",
                    kind, Truncate(_wipe.WipeId, 64), Truncate(_wipe.Level, 64), _wipe.Seed, _wipe.WorldSize, _wipe.Network, _wipe.Save, _wipe.SaveCreated, Stamp(Now));
                if (kind != "initial")
                {
                    ServerEvent("wipe", null, Vector3.zero, null, null, kind);
                }
            }

            Interface.Oxide.DataFileSystem.WriteObject(WipeDataFile, _wipe);
        }

        #endregion

        #region Hooks: players & sessions

        private void OnPlayerConnected(BasePlayer player)
        {
            if (!IsRealPlayer(player) || !_config.Logging.Connections) return;
            Count("connect");

            var id = SteamId(player);
            if (_sessions.ContainsKey(id))
            {
                CloseSession(id, DisplayName(player), "reconnected");
            }
            OpenSession(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (!IsRealPlayer(player)) return;
            var id = SteamId(player);
            foreach (var key in _violationSeen.Keys.Where(k => k.StartsWith(id + ":", StringComparison.Ordinal)).ToList())
            {
                _violationSeen.Remove(key);
            }
            if (!_config.Logging.Connections) return;
            Count("disconnect");
            CloseSession(id, DisplayName(player), reason);
        }

        private void OpenSession(BasePlayer player)
        {
            var id = SteamId(player);
            var name = DisplayName(player);
            var stamp = Stamp(Now);
            _sessions[id] = new Session { StartedUtc = DateTime.UtcNow, StartedStamp = stamp };

            Write("INSERT INTO mstats_players (steam_id, name, last_ip, is_admin, is_online, first_seen, last_seen, connections) VALUES (@0, @1, @2, @3, 1, @4, @4, 1) ON DUPLICATE KEY UPDATE name = @1, last_ip = @2, is_admin = @3, is_online = 1, last_seen = @4, connections = connections + 1",
                id, name, Ip(player), player.IsAdmin ? 1 : 0, stamp);
            Write("INSERT INTO mstats_player_names (steam_id, name, first_used, last_used) VALUES (@0, @1, @2, @2) ON DUPLICATE KEY UPDATE last_used = @2",
                id, name, stamp);
            Write("INSERT INTO mstats_sessions (steam_id, name, ip, connected_at, last_seen_at) VALUES (@0, @1, @2, @3, @3)",
                id, name, Ip(player), stamp);
        }

        private void CloseSession(ulong id, string name, string reason)
        {
            Session session;
            if (!_sessions.TryGetValue(id, out session)) return;
            _sessions.Remove(id);

            var seconds = Math.Max(0, (long)(DateTime.UtcNow - session.StartedUtc).TotalSeconds);
            var stamp = Stamp(Now);
            Write("UPDATE mstats_players SET online_seconds = online_seconds + @0, is_online = 0, last_seen = @1 WHERE steam_id = @2", seconds, stamp, id);
            Write("UPDATE mstats_sessions SET disconnected_at = @0, last_seen_at = @0, duration_seconds = TIMESTAMPDIFF(SECOND, connected_at, @0), disconnect_reason = @1 WHERE steam_id = @2 AND connected_at = @3 AND disconnected_at IS NULL",
                stamp, Truncate(reason, 255), id, session.StartedStamp);
        }

        private void OnPlayerRespawned(BasePlayer player) => Activity(player, "respawned", "player");

        private void OnPlayerWound(BasePlayer player, HitInfo info) => Activity(player, "wounded", KillerType(player, info));

        private void OnPlayerRecovered(BasePlayer player) => Activity(player, "recovered", "player");

        private void OnPlayerRevive(BasePlayer reviver, BasePlayer target)
        {
            Activity(reviver, "revived_player", "player");
            Activity(target, "was_revived", "player");
        }

        private void OnPlayerAssist(BasePlayer target, BasePlayer assister)
        {
            Activity(assister, "assisted_player", "player");
            Activity(target, "was_assisted", "player");
        }

        #endregion

        #region Hooks: moderation

        private void OnPlayerKicked(BasePlayer player, string reason)
        {
            if (!IsRealPlayer(player)) return;
            Moderation(SteamId(player), DisplayName(player), "kicked", reason, null, null);
        }

        // Raised by Rust when EAC removes a client (the player object may already be gone).
        private void OnPlayerKicked(Network.Connection connection, string reason)
        {
            if (connection == null || connection.userid <= SteamIdOffset) return;
            Moderation(connection.userid, connection.username, "kicked", string.IsNullOrEmpty(reason) ? "EAC" : "EAC: " + reason, null, null);
        }

        // Raised by Oxide.Rust when the server ban list changes (ban / banid / server.ban commands).
        private void OnPlayerBanned(string name, ulong steamId, string address, string reason, long expiry)
        {
            var details = string.IsNullOrEmpty(reason) ? null : reason;
            if (expiry > 0)
            {
                details = $"{details ?? "no reason"} (expires {DateTimeOffset.FromUnixTimeSeconds(expiry):yyyy-MM-dd HH:mm} UTC)";
            }
            Moderation(steamId, name, "banned", details, null, null);
        }

        // Raised by Rust (EAC) and Oxide.Rust (Steam auth: VAC / game ban) for a connecting or connected client.
        private void OnPlayerBanned(Network.Connection connection, string reason)
        {
            if (connection == null || connection.userid <= SteamIdOffset) return;
            Moderation(connection.userid, connection.username, "banned", string.IsNullOrEmpty(reason) ? "anticheat" : "anticheat: " + reason, null, null);
        }

        private void OnPlayerUnbanned(string name, ulong steamId, string address)
        {
            Moderation(steamId, name, "unbanned", null, null, null);
        }

        // Anti-hack violations can be raised many times per second for one player; log each type at most
        // once per ViolationThrottleSeconds per player so a single event cannot flood the database.
        private void OnPlayerViolation(BasePlayer player, AntiHackType type, float amount)
        {
            if (!IsRealPlayer(player)) return;

            var key = SteamId(player) + ":" + type;
            var now = DateTime.UtcNow;
            DateTime last;
            if (_violationSeen.TryGetValue(key, out last) && (now - last).TotalSeconds < ViolationThrottleSeconds) return;
            _violationSeen[key] = now;

            Moderation(SteamId(player), DisplayName(player), "violation", $"{type} ({amount:F1})", null, null);
        }

        private void OnPlayerReported(BasePlayer reporter, string targetName, string targetId, string subject, string message, string type)
        {
            if (!IsRealPlayer(reporter)) return;

            ulong targetSteamId;
            ulong.TryParse(targetId, out targetSteamId);
            var details = Truncate($"{type}: {subject} - {message}", 4000);
            Moderation(targetSteamId, targetName ?? targetId ?? "unknown", "reported", details, SteamId(reporter), DisplayName(reporter));
        }

        private void Moderation(ulong steamId, string name, string evt, string details, ulong? actorId, string actorName)
        {
            if (!_config.Logging.Moderation) return;
            Count("moderation");
            Write("INSERT INTO mstats_moderation (steam_id, name, event, details, actor_id, actor_name, occurred_at) VALUES (@0, @1, @2, @3, @4, @5, @6)",
                steamId, Truncate(name ?? steamId.ToString(), 128), evt, details, actorId, Truncate(actorName, 128), Stamp(Now));
        }

        #endregion

        #region Hooks: gathering

        // Trees, ore nodes, corpses: final amount after gather-rate modifiers.
        private void OnDispenserGathered(ResourceDispenser dispenser, BasePlayer player, Item item) => Gather(player, item, "dispenser");

        // Finishing bonus for fully harvested trees/nodes. Rust creates a separate item for the bonus
        // (ResourceDispenser.AssignFinishBonus), so it never passes through OnDispenserGathered.
        private void OnDispenserBonusReceived(ResourceDispenser dispenser, BasePlayer player, Item item) => Gather(player, item, "bonus");

        // Ground collectibles picked up with E (hemp, mushrooms, wood/stone/ore piles, pumpkins...).
        // Raised once per item created by CollectibleEntity.DoPickup after the amount is final.
        private void OnCollectiblePickedup(CollectibleEntity collectible, BasePlayer player, Item item) => Gather(player, item, "collectible");

        // Harvesting planted crops.
        private void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer player) => Gather(player, item, "growable");

        private void OnFishCaught(ItemDefinition fish, BaseFishingRod rod, BasePlayer player)
        {
            if (fish == null || !IsRealPlayer(player) || !_config.Logging.Gathering) return;
            Count("gather");
            WriteGather(SteamId(player), Ref(fish), 1, "fishing");
        }

        // Quarry output is attributed to the player who last started the engine; if nobody did since the
        // plugin loaded, to the quarry owner (0 = static/monument quarry).
        private void OnQuarryGather(MiningQuarry quarry, Item item)
        {
            if (!_config.Logging.QuarryAndExcavator || item == null || quarry == null) return;
            Count("quarry");
            WriteGather(OperatorOf(quarry, quarry.OwnerID), Ref(item.info), item.amount, "quarry");
        }

        // Raised after EngineSwitch.StartEngine / StopEngine; IsEngineOn() reflects the new state.
        private void OnQuarryToggled(MiningQuarry quarry, BasePlayer player)
        {
            if (quarry == null || quarry.net == null || !IsRealPlayer(player)) return;
            var on = quarry.IsEngineOn();
            if (on)
            {
                _extractorOperators[quarry.net.ID.Value] = SteamId(player);
            }
            Activity(player, on ? "quarry_started" : "quarry_stopped", quarry.ShortPrefabName);
        }

        // The giant excavator has no owner; output is attributed to the player who last set the arm's
        // resource target, otherwise logged under steam_id 0.
        private void OnExcavatorGather(ExcavatorArm excavator, Item item)
        {
            if (!_config.Logging.QuarryAndExcavator || item == null) return;
            Count("excavator");
            WriteGather(OperatorOf(excavator, 0), Ref(item.info), item.amount, "excavator");
        }

        private void OnExcavatorResourceSet(ExcavatorArm excavator, string resource, BasePlayer player)
        {
            if (excavator == null || excavator.net == null || !IsRealPlayer(player)) return;
            _extractorOperators[excavator.net.ID.Value] = SteamId(player);
            Activity(player, "excavator_target_set", resource);
        }

        private ulong OperatorOf(BaseEntity extractor, ulong fallback)
        {
            ulong steamId;
            if (extractor != null && extractor.net != null && _extractorOperators.TryGetValue(extractor.net.ID.Value, out steamId)) return steamId;
            return fallback;
        }

        // Survey charge yield is spawned on the ground; the actual gain is logged when someone picks it up
        // (source = pickup). Here we only record that the player used a charge and what it revealed.
        private void OnSurveyGather(SurveyCharge survey, Item item)
        {
            if (item == null) return;
            var player = survey != null ? survey.creatorEntity as BasePlayer : null;
            if (!IsRealPlayer(player)) return;
            Activity(player, "survey_yield", item.info != null ? item.info.shortname : "unknown", Math.Max(1, item.amount));
        }

        // Items picked up from the ground (dropped items, survey yield, loot).
        private void OnItemPickup(Item item, BasePlayer player, WorldItem worldItem)
        {
            if (!_config.Logging.WorldItemPickups || item == null || !IsRealPlayer(player)) return;
            Count("pickup");
            WriteGather(SteamId(player), Ref(item.info), item.amount, "pickup");
        }

        private void Gather(BasePlayer player, Item item, string source)
        {
            if (!_config.Logging.Gathering || item == null || !IsRealPlayer(player)) return;
            Count("gather");
            WriteGather(SteamId(player), Ref(item.info), item.amount, source);
        }

        private void WriteGather(ulong steamId, ItemRef item, int amount, string source)
        {
            if (amount <= 0) return;
            Write("INSERT INTO mstats_gather (steam_id, item_shortname, item_name, source, amount, day) VALUES (@0, @1, @2, @3, @4, @5) ON DUPLICATE KEY UPDATE amount = amount + @4, item_name = @2",
                steamId, item.Shortname, item.Name, source, amount, Day());
        }

        #endregion

        #region Hooks: crafting & items

        private void OnItemCraftFinished(ItemCraftTask task, Item item, ItemCrafter crafter)
        {
            if (!_config.Logging.Crafting || item == null) return;
            var player = crafter != null ? crafter.owner : null;
            if (!IsRealPlayer(player)) return;
            Count("craft");
            var def = Ref(item.info);
            Write("INSERT INTO mstats_crafting (steam_id, item_shortname, item_name, amount, day) VALUES (@0, @1, @2, @3, @4) ON DUPLICATE KEY UPDATE amount = amount + @3, item_name = @2",
                SteamId(player), def.Shortname, def.Name, Math.Max(1, item.amount), Day());
        }

        // Raised for every world drop (inventory drop, death drops, container drops).
        private void OnItemDropped(Item item, BaseEntity worldEntity)
        {
            if (item == null) return;
            var player = item.GetOwnerPlayer();
            if (!IsRealPlayer(player) || player.IsDead()) return;
            ItemAction(player, "dropped", item.info, item.amount);
        }

        // Inventory item actions: consume, unwrap, upgrade_item, open, etc. "drop" is covered by OnItemDropped.
        private void OnItemAction(Item item, string action, BasePlayer player)
        {
            if (item == null || string.IsNullOrEmpty(action) || action == "drop") return;
            ItemAction(player, Truncate(action, 24), item.info, 1);
        }

        private void OnHealingItemUse(MedicalTool tool, BasePlayer player)
        {
            var item = tool != null ? tool.GetItem() : null;
            ItemAction(player, "healed", item != null ? item.info : null, 1);
        }

        private void OnPlayerDrink(BasePlayer player, LiquidContainer container)
        {
            if (!IsRealPlayer(player) || !_config.Logging.Items) return;
            Count("item");
            var subject = container != null ? container.ShortPrefabName : "unknown";
            WriteItem(SteamId(player), "drank", new ItemRef { Shortname = Truncate(subject, 64), Name = PrettyPrefab(subject) }, 1);
        }

        private void OnItemRepair(BasePlayer player, Item item)
        {
            if (item == null) return;
            ItemAction(player, "repaired", item.info, 1);
        }

        private void OnItemResearched(ResearchTable table, int scrapCost)
        {
            if (table == null) return;
            var item = table.GetTargetItem();
            ItemAction(table.user, "researched", item != null ? item.info : null, 1);
        }

        private void OnTechTreeNodeUnlocked(Workbench workbench, TechTreeData.NodeInstance node, BasePlayer player)
        {
            ItemAction(player, "unlocked", node != null ? node.itemDef : null, 1);
        }

        // Vending machine purchases. NPC vending machines call the same base method, so this covers both.
        private void OnGiveSoldItem(VendingMachine machine, Item item, BasePlayer buyer)
        {
            if (item == null) return;
            ItemAction(buyer, machine is NPCVendingMachine ? "bought_npc" : "bought", item.info, item.amount);
        }

        private void OnMixingTableFinished(MixingTable table, BasePlayer player, Recipe recipe, int quantity)
        {
            if (recipe == null) return;
            ItemAction(player, "mixed", recipe.ProducedItem, Math.Max(1, recipe.ProducedItemCount * Math.Max(1, quantity)));
        }

        private void ItemAction(BasePlayer player, string action, ItemDefinition definition, int amount)
        {
            if (!_config.Logging.Items || !IsRealPlayer(player)) return;
            Count("item");
            WriteItem(SteamId(player), action, Ref(definition), amount);
        }

        private void WriteItem(ulong steamId, string action, ItemRef item, int amount)
        {
            if (amount <= 0) return;
            Write("INSERT INTO mstats_items (steam_id, action, item_shortname, item_name, amount, day) VALUES (@0, @1, @2, @3, @4, @5) ON DUPLICATE KEY UPDATE amount = amount + @4, item_name = @3",
                steamId, action, item.Shortname, item.Name, amount, Day());
        }

        #endregion

        #region Hooks: building

        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            if (planner == null || go == null) return;
            var player = planner.GetOwnerPlayer();
            if (!IsRealPlayer(player)) return;

            var entity = go.ToBaseEntity();
            var block = entity as BuildingBlock;
            if (block != null)
            {
                if (!_config.Logging.Building) return;
                Count("build");
                WriteBuilding(player, "placed", BlockName(block), block.grade.ToString());
                return;
            }

            if (!_config.Logging.Deployables) return;
            Count("deploy");
            var def = planner.GetOwnerItemDefinition();
            var item = def != null ? Ref(def) : PrefabRef(entity);
            WriteDeployable(player, item);
        }

        // Items placed with a Deployer (locks, code locks, etc.) instead of a Planner. The 2nd argument is
        // an ItemModDeployable or the target entity depending on the code path, hence 'object'.
        private void OnItemDeployed(Deployer deployer, object target, BaseEntity deployed)
        {
            if (!_config.Logging.Deployables || deployer == null) return;
            var player = deployer.GetOwnerPlayer();
            if (!IsRealPlayer(player)) return;
            Count("deploy");
            var held = deployer.GetItem();
            var item = held != null && held.info != null ? Ref(held.info) : PrefabRef(deployed);
            WriteDeployable(player, item);
        }

        private void OnStructureUpgraded(BuildingBlock block, BasePlayer player, BuildingGrade.Enum grade, ulong skin)
        {
            if (!_config.Logging.Building || block == null || !IsRealPlayer(player)) return;
            Count("upgrade");
            WriteBuilding(player, "upgraded", BlockName(block), grade.ToString());
        }

        private void OnStructureDemolish(BaseCombatEntity entity, BasePlayer player, bool immediate)
        {
            if (!_config.Logging.Building || entity == null || !IsRealPlayer(player)) return;
            Count("demolish");
            var block = entity as BuildingBlock;
            WriteBuilding(player, immediate ? "demolished_now" : "demolished", block != null ? BlockName(block) : Truncate(entity.ShortPrefabName, 64), block != null ? block.grade.ToString() : "-");
        }

        private void OnStructureRepair(BaseCombatEntity entity, BasePlayer player)
        {
            if (!_config.Logging.Building || entity == null || !IsRealPlayer(player)) return;
            if (entity.health >= entity.MaxHealth()) return; // hammer hit on an undamaged structure
            Count("repair");
            var block = entity as BuildingBlock;
            WriteBuilding(player, "repaired", block != null ? BlockName(block) : Truncate(entity.ShortPrefabName, 64), block != null ? block.grade.ToString() : "-");
        }

        private void WriteBuilding(BasePlayer player, string action, string block, string grade)
        {
            Write("INSERT INTO mstats_building (steam_id, action, block, grade, amount, day) VALUES (@0, @1, @2, @3, 1, @4) ON DUPLICATE KEY UPDATE amount = amount + 1",
                SteamId(player), action, Truncate(block, 64), Truncate(grade, 16), Day());
        }

        private void WriteDeployable(BasePlayer player, ItemRef item)
        {
            Write("INSERT INTO mstats_deployables (steam_id, item_shortname, item_name, amount, day) VALUES (@0, @1, @2, 1, @3) ON DUPLICATE KEY UPDATE amount = amount + 1, item_name = @2",
                SteamId(player), item.Shortname, item.Name, Day());
        }

        #endregion

        #region Hooks: tool cupboards

        private void OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player) => SetCupboardAccess(privilege, player, SteamId(player), DisplayName(player), true);

        // One player authorizing another (e.g. team authorization).
        private void OnCupboardAssign(BuildingPrivlidge privilege, ulong targetId, BasePlayer player)
        {
            if (targetId <= SteamIdOffset) return;
            SetCupboardAccess(privilege, player, targetId, OwnerName(targetId), true);
        }

        private void OnCupboardDeauthorize(BuildingPrivlidge privilege, BasePlayer player) => SetCupboardAccess(privilege, player, SteamId(player), DisplayName(player), false);

        private void OnCupboardClearList(BuildingPrivlidge privilege, BasePlayer player)
        {
            if (!_config.Logging.Cupboards || privilege == null || privilege.net == null) return;
            Count("cupboard");
            Write("UPDATE mstats_cupboards SET is_authorized = 0, deauthorized_at = @1 WHERE cupboard_id = @0 AND is_authorized = 1", privilege.net.ID.Value, Stamp(Now));
        }

        private void SetCupboardAccess(BuildingPrivlidge privilege, BasePlayer actor, ulong steamId, string name, bool authorized)
        {
            if (!_config.Logging.Cupboards || privilege == null || privilege.net == null || !IsRealPlayer(actor)) return;
            Count("cupboard");
            var pos = privilege.transform.position;
            var stamp = Stamp(Now);
            if (authorized)
            {
                Write("INSERT INTO mstats_cupboards (cupboard_id, steam_id, name, is_authorized, pos_x, pos_y, pos_z, grid, authorized_at, deauthorized_at) VALUES (@0, @1, @2, 1, @3, @4, @5, @6, @7, NULL) ON DUPLICATE KEY UPDATE is_authorized = 1, name = @2, authorized_at = @7, deauthorized_at = NULL",
                    privilege.net.ID.Value, steamId, Truncate(name, 128), R(pos.x), R(pos.y), R(pos.z), Grid(pos), stamp);
            }
            else
            {
                Write("UPDATE mstats_cupboards SET is_authorized = 0, deauthorized_at = @2 WHERE cupboard_id = @0 AND steam_id = @1",
                    privilege.net.ID.Value, steamId, stamp);
            }
        }

        private void OnEntityKill(BaseNetworkable networkable)
        {
            if (_state == DbState.Disabled || networkable == null || networkable.net == null) return;
            var netId = networkable.net.ID.Value;
            _extractorOperators.Remove(netId);

            if (!_config.Logging.Cupboards) return;
            if (networkable is BuildingPrivlidge)
            {
                Write("UPDATE mstats_cupboards SET is_authorized = 0, deauthorized_at = @1 WHERE cupboard_id = @0 AND is_authorized = 1", netId, Stamp(Now));
            }
        }

        #endregion

        #region Hooks: combat

        // One row increment per trigger pull (a shotgun shell with several pellets counts as one shot).
        private void OnWeaponFired(BaseProjectile projectile, BasePlayer player, ItemModProjectile mod, ProtoBuf.ProjectileShoot shoot)
        {
            if (!_config.Logging.WeaponsFired || !IsRealPlayer(player)) return;
            Count("shot");
            var ammo = projectile != null && projectile.primaryMagazine != null ? projectile.primaryMagazine.ammoType : null;
            WriteShot(player, HeldRef(projectile) ?? ActiveRef(player), Ref(ammo), 1);
        }

        private void OnRocketLaunched(BasePlayer player, BaseEntity rocket)
        {
            if (!_config.Logging.WeaponsFired || !IsRealPlayer(player)) return;
            Count("shot");
            var active = player.GetActiveItem();
            var launcher = active != null ? active.GetHeldEntity() as BaseProjectile : null;
            var ammo = launcher != null && launcher.primaryMagazine != null ? launcher.primaryMagazine.ammoType : null;
            WriteShot(player, ActiveRef(player), ammo != null ? Ref(ammo) : PrefabRef(rocket), 1);
        }

        private void OnExplosiveThrown(BasePlayer player, BaseEntity entity, ThrownWeapon weapon) => Thrown(player, entity, weapon);

        private void OnExplosiveDropped(BasePlayer player, BaseEntity entity, ThrownWeapon weapon) => Thrown(player, entity, weapon);

        private void Thrown(BasePlayer player, BaseEntity entity, ThrownWeapon weapon)
        {
            if (!_config.Logging.WeaponsFired || !IsRealPlayer(player)) return;
            Count("shot");
            var item = HeldRef(weapon) ?? ActiveRef(player) ?? PrefabRef(entity);
            WriteShot(player, item, item, 1);
        }

        private void WriteShot(BasePlayer player, ItemRef weapon, ItemRef ammo, int count)
        {
            weapon = weapon ?? ItemRef.Unknown;
            ammo = ammo ?? ItemRef.Unknown;
            Write("INSERT INTO mstats_shots (steam_id, weapon_shortname, weapon_name, ammo_shortname, ammo_name, shots, day) VALUES (@0, @1, @2, @3, @4, @5, @6) ON DUPLICATE KEY UPDATE shots = shots + @5",
                SteamId(player), weapon.Shortname, weapon.Name, ammo.Shortname, ammo.Name, count, Day());
        }

        // Projectile and melee hits. Raised before damage is applied; only hits on combat entities are counted.
        // damage is the raw HitInfo total before the target's armour/protection is applied (Rust scales it afterwards).
        private void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (!_config.Logging.Hits || info == null || !IsRealPlayer(attacker)) return;
            var target = info.HitEntity as BaseCombatEntity; // trees/ore nodes are not combat entities and are skipped
            if (target == null || target == attacker) return;

            Count("hit");
            var weapon = WeaponRef(info, attacker);
            var damage = info.damageTypes != null ? Math.Round(Math.Max(0f, info.damageTypes.Total()), 1) : 0;
            Write("INSERT INTO mstats_hits (steam_id, weapon_shortname, target_type, bodypart, hits, headshots, damage, day) VALUES (@0, @1, @2, @3, 1, @4, @5, @6) ON DUPLICATE KEY UPDATE hits = hits + 1, headshots = headshots + @4, damage = damage + @5",
                SteamId(attacker), weapon.Shortname, TargetType(target), BodyPart(info, target), info.isHeadshot ? 1 : 0, damage, Day());
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || _state == DbState.Disabled) return;

            try
            {
                var attacker = (info != null ? info.InitiatorPlayer : null) ?? entity.lastAttacker as BasePlayer;
                var attackerIsPlayer = IsRealPlayer(attacker) && attacker != entity;
                var victimPlayer = entity as BasePlayer;

                if (victimPlayer != null && IsRealPlayer(victimPlayer))
                {
                    PlayerDied(victimPlayer, info, attacker, attackerIsPlayer);
                    return;
                }

                if (entity is PatrolHelicopter || entity is BradleyAPC)
                {
                    var eventName = entity is PatrolHelicopter ? "patrol_helicopter_destroyed" : "bradley_destroyed";
                    ServerEvent(eventName, entity, entity.transform.position, attackerIsPlayer ? attacker : null, null, attackerIsPlayer ? WeaponRef(info, attacker).Name : null);
                    if (attackerIsPlayer) PveKill(attacker, entity, info, "event");
                    return;
                }

                if (!attackerIsPlayer) return;

                if (victimPlayer != null)
                {
                    PveKill(attacker, entity, info, victimPlayer is HumanNPC ? "scientist" : "npc");
                }
                else if (entity is BaseNpc || entity is Rust.Ai.Gen2.BaseNPC2 || entity is SimpleShark || entity is RidableHorse)
                {
                    PveKill(attacker, entity, info, "animal");
                }
                else if (entity is BuildingBlock)
                {
                    BuildingDestroyed(attacker, entity, info, "block");
                }
                else if (entity is DecayEntity && entity.OwnerID != 0)
                {
                    BuildingDestroyed(attacker, entity, info, "deployable");
                }
                else if (entity is BaseVehicle && entity.OwnerID != 0)
                {
                    BuildingDestroyed(attacker, entity, info, "vehicle");
                }
            }
            catch (Exception ex)
            {
                PrintError($"OnEntityDeath: {ex}");
            }
        }

        private void PlayerDied(BasePlayer victim, HitInfo info, BasePlayer attacker, bool attackerIsPlayer)
        {
            var pos = victim.transform.position;
            var stamp = Stamp(Now);
            var weapon = WeaponRef(info, attackerIsPlayer ? attacker : null);
            var bodypart = BodyPart(info, victim);

            if (attackerIsPlayer && _config.Logging.PvpKills)
            {
                Count("pvp_kill");
                var kpos = attacker.transform.position;
                Write("INSERT INTO mstats_pvp_kills (killer_id, killer_name, victim_id, victim_name, weapon_shortname, weapon_name, bodypart, is_headshot, distance, victim_sleeping, victim_wounded, killer_x, killer_y, killer_z, pos_x, pos_y, pos_z, grid, occurred_at) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12, @13, @14, @15, @16, @17, @18)",
                    SteamId(attacker), DisplayName(attacker), SteamId(victim), DisplayName(victim), weapon.Shortname, weapon.Name, bodypart, info != null && info.isHeadshot ? 1 : 0, Distance(victim, info, attacker),
                    victim.IsSleeping() ? 1 : 0, victim.IsWounded() ? 1 : 0,
                    R(kpos.x), R(kpos.y), R(kpos.z), R(pos.x), R(pos.y), R(pos.z), Grid(pos), stamp);
            }

            if (!_config.Logging.Deaths) return;
            Count("death");

            var killerType = KillerType(victim, info);
            BaseEntity initiator = info != null ? info.Initiator : null;
            if (initiator == null) initiator = victim.lastAttacker;
            var killerPlayer = initiator as BasePlayer;
            ulong? killerId = null;
            string killerName = null;
            if (killerPlayer != null && killerPlayer != victim)
            {
                killerId = SteamId(killerPlayer);
                killerName = DisplayName(killerPlayer);
            }
            else if (initiator != null && initiator != victim)
            {
                killerName = Truncate(initiator.ShortPrefabName, 128);
            }

            Write("INSERT INTO mstats_deaths (steam_id, name, cause, killer_type, killer_id, killer_name, weapon_shortname, weapon_name, pos_x, pos_y, pos_z, grid, occurred_at) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12)",
                SteamId(victim), DisplayName(victim), DeathCause(victim, info), killerType, killerId, killerName,
                killerType == "environment" || killerType == "self" ? null : weapon.Shortname, killerType == "environment" || killerType == "self" ? null : weapon.Name,
                R(pos.x), R(pos.y), R(pos.z), Grid(pos), stamp);
        }

        private void PveKill(BasePlayer attacker, BaseCombatEntity victim, HitInfo info, string type)
        {
            if (!_config.Logging.PveKills) return;
            Count("pve_kill");
            var pos = victim.transform.position;
            var weapon = WeaponRef(info, attacker);
            Write("INSERT INTO mstats_pve_kills (steam_id, victim_type, victim_prefab, weapon_shortname, weapon_name, bodypart, distance, pos_x, pos_y, pos_z, grid, occurred_at) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11)",
                SteamId(attacker), type, Truncate(victim.ShortPrefabName ?? "unknown", 64), weapon.Shortname, weapon.Name, BodyPart(info, victim), Distance(victim, info, attacker),
                R(pos.x), R(pos.y), R(pos.z), Grid(pos), Stamp(Now));
        }

        private void BuildingDestroyed(BasePlayer attacker, BaseCombatEntity entity, HitInfo info, string entityType)
        {
            if (!_config.Logging.BuildingDestroyed) return;
            Count("destroy");
            var pos = entity.transform.position;
            var weapon = WeaponRef(info, attacker);
            var block = entity as BuildingBlock;
            Write("INSERT INTO mstats_building_destroyed (steam_id, owner_id, entity_type, entity, grade, weapon_shortname, weapon_name, pos_x, pos_y, pos_z, grid, occurred_at) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11)",
                SteamId(attacker), entity.OwnerID, entityType, block != null ? BlockName(block) : Truncate(entity.ShortPrefabName, 64), block != null ? block.grade.ToString() : null, weapon.Shortname, weapon.Name,
                R(pos.x), R(pos.y), R(pos.z), Grid(pos), Stamp(Now));
        }

        private static string DeathCause(BaseCombatEntity entity, HitInfo info)
        {
            var type = info != null && info.damageTypes != null && info.damageTypes.Total() > 0 ? info.damageTypes.GetMajorityDamageType() : entity.lastDamage;
            return type.ToString();
        }

        /// <summary>Classifies what killed/wounded a player: player, self, npc, animal, turret, trap, ... or environment.</summary>
        private static string KillerType(BaseCombatEntity victim, HitInfo info)
        {
            BaseEntity initiator = info != null ? info.Initiator : null;
            if (initiator == null) initiator = victim.lastAttacker;

            var damage = info != null && info.damageTypes != null && info.damageTypes.Total() > 0 ? info.damageTypes.GetMajorityDamageType() : victim.lastDamage;
            if (damage == DamageType.Suicide) return "self";

            if (initiator != null)
            {
                if (initiator == victim) return "self";
                var player = initiator as BasePlayer;
                if (player != null) return IsRealPlayer(player) ? "player" : "npc";
                if (initiator is BaseNpc || initiator is Rust.Ai.Gen2.BaseNPC2 || initiator is SimpleShark) return "animal";
                if (initiator is PatrolHelicopter) return "patrol_helicopter";
                if (initiator is BradleyAPC) return "bradley";
                if (initiator is AutoTurret || initiator is GunTrap || initiator is FlameTurret || initiator is SamSite) return "turret";
                if (initiator is BaseTrap) return "trap";
                if (initiator is FireBall) return "fire";
                if (initiator is BaseVehicle) return "vehicle";
                if (initiator is TimedExplosive) return "explosive";
                return "entity";
            }

            switch (damage)
            {
                case DamageType.Fall:
                case DamageType.Drowned:
                case DamageType.Cold:
                case DamageType.ColdExposure:
                case DamageType.Heat:
                case DamageType.Hunger:
                case DamageType.Thirst:
                case DamageType.Radiation:
                case DamageType.RadiationExposure:
                case DamageType.Poison:
                case DamageType.Decay:
                    return "environment";
                case DamageType.Bleeding:
                    return "bleeding";
                default:
                    return "unknown";
            }
        }

        private static string TargetType(BaseCombatEntity target)
        {
            var player = target as BasePlayer;
            if (player != null) return IsRealPlayer(player) ? "player" : "npc";
            if (target is BaseNpc || target is Rust.Ai.Gen2.BaseNPC2 || target is SimpleShark || target is RidableHorse) return "animal";
            if (target is PatrolHelicopter || target is BradleyAPC) return "event";
            if (target is BuildingBlock) return "building";
            if (target is BaseVehicle) return "vehicle";
            if (target is DecayEntity) return "deployable";
            return "other";
        }

        private static string BodyPart(HitInfo info, BaseCombatEntity victim)
        {
            if (info == null) return "unknown";
            var bone = victim != null && victim.skeletonProperties != null ? victim.skeletonProperties.FindBone(info.HitBone) : null;
            if (bone != null) return Truncate(bone.area.ToString().ToLowerInvariant(), 32);
            var name = info.boneName;
            return string.IsNullOrEmpty(name) ? "unknown" : Truncate(name.ToLowerInvariant(), 32);
        }

        private ItemRef WeaponRef(HitInfo info, BasePlayer attacker)
        {
            var item = HeldRef(info != null ? info.Weapon : null);
            if (item != null) return item;
            if (info != null && info.WeaponPrefab != null) return PrefabRef(info.WeaponPrefab);
            return ActiveRef(attacker) ?? ItemRef.Unknown;
        }

        private static double Distance(BaseCombatEntity victim, HitInfo info, BasePlayer attacker)
        {
            try
            {
                if (info != null && info.IsProjectile() && info.ProjectileDistance > 0)
                {
                    return Math.Round(info.ProjectileDistance, 1);
                }
                var from = (info != null ? info.Initiator : null) ?? attacker;
                if (from == null || victim == null) return 0;
                return Math.Round(Vector3.Distance(from.transform.position, victim.transform.position), 1);
            }
            catch
            {
                return 0;
            }
        }

        #endregion

        #region Hooks: chat & commands

        private void OnPlayerChat(BasePlayer player, string message, Chat.ChatChannel channel)
        {
            if (!IsRealPlayer(player) || string.IsNullOrEmpty(message)) return;

            var flagged = _config.AdminLog.Enabled && _config.AdminLog.Keywords.Any(k => !string.IsNullOrWhiteSpace(k) && message.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
            var enabled = channel == Chat.ChatChannel.Team ? _config.Logging.TeamChat : _config.Logging.Chat;
            if (!enabled && !flagged) return;

            Count("chat");
            Write("INSERT INTO mstats_chat (steam_id, name, channel, message, is_admin, is_flagged, occurred_at) VALUES (@0, @1, @2, @3, @4, @5, @6)",
                SteamId(player), DisplayName(player), Truncate(channel.ToString(), 16), message, player.IsAdmin ? 1 : 0, flagged ? 1 : 0, Stamp(Now));
        }

        private void OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            if (!_config.Logging.Commands || !IsRealPlayer(player)) return;
            Count("command");
            var arguments = args != null && args.Length > 0 ? string.Join(" ", args) : null;
            Write("INSERT INTO mstats_commands (steam_id, name, source, command, arguments, occurred_at) VALUES (@0, @1, 'chat', @2, @3, @4)",
                SteamId(player), DisplayName(player), Truncate("/" + command, 128), arguments, Stamp(Now));
        }

        private void OnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg == null || arg.cmd == null) return;

            var command = arg.cmd.FullName;
            if (string.IsNullOrEmpty(command)) return;
            foreach (var prefix in IgnoredCommandPrefixes)
            {
                if (command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            }

            var player = arg.Player();
            if (player != null)
            {
                if (!_config.Logging.Commands || !IsRealPlayer(player)) return;
                Count("command");
                Write("INSERT INTO mstats_commands (steam_id, name, source, command, arguments, occurred_at) VALUES (@0, @1, 'console', @2, @3, @4)",
                    SteamId(player), DisplayName(player), Truncate(command, 128), ArgumentsText(arg), Stamp(Now));
                return;
            }

            if (!_config.Logging.RconCommands) return;
            Count("rcon");
            Write("INSERT INTO mstats_commands (steam_id, name, source, command, arguments, occurred_at) VALUES (NULL, @0, @1, @2, @3, @4)",
                arg.IsRcon ? "RCON" : "Server", arg.IsRcon ? "rcon" : "server", Truncate(command, 128), ArgumentsText(arg), Stamp(Now));
        }

        private static string ArgumentsText(ConsoleSystem.Arg arg)
        {
            try
            {
                var text = arg.FullString.ToString();
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch
            {
                return null;
            }
        }

        // Messages broadcast by the server itself (name == "SERVER"), e.g. the 'say' command or plugin broadcasts.
        private void OnServerMessage(string message, string name, string color, ulong id)
        {
            if (!_config.Logging.ServerMessages || name != "SERVER" || string.IsNullOrEmpty(message)) return;
            Count("server_message");
            Write("INSERT INTO mstats_server_messages (message, occurred_at) VALUES (@0, @1)", message, Stamp(Now));
        }

        #endregion

        #region Hooks: activity (loot, mounting, signs, ...)

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (entity == null || entity is BasePlayer) return; // players are reported through OnLootPlayer
            Activity(player, "looted", entity.ShortPrefabName);
        }

        private void OnLootPlayer(BasePlayer looter, BasePlayer target)
        {
            if (target == null) return;
            Activity(looter, "looted_player", target.IsSleeping() ? "sleeper" : target.IsWounded() ? "wounded" : "player");
        }

        private void OnEntityMounted(BaseMountable mountable, BasePlayer player)
        {
            if (mountable == null) return;
            Activity(player, "mounted", mountable.ShortPrefabName);
        }

        private void OnRidableAnimalClaimed(BaseEntity animal, BasePlayer player)
        {
            Activity(player, "claimed", animal != null ? animal.ShortPrefabName : "animal");
        }

        private void OnSignUpdated(Signage sign, BasePlayer player)
        {
            if (sign == null) return;
            Activity(player, "sign_updated", sign.ShortPrefabName);
        }

        private void OnRecyclerToggle(Recycler recycler, BasePlayer player)
        {
            if (recycler == null || recycler.IsOn()) return; // only count turning it on
            Activity(player, "recycler_started", recycler.ShortPrefabName);
        }

        private void OnOvenToggle(BaseOven oven, BasePlayer player)
        {
            if (oven == null || oven.IsOn()) return; // only count turning it on
            Activity(player, "oven_started", oven.ShortPrefabName);
        }

        private void Activity(BasePlayer player, string action, string subject, int amount = 1)
        {
            if (!_config.Logging.Activity || !IsRealPlayer(player) || amount <= 0) return;
            Count("activity");
            Write("INSERT INTO mstats_activity (steam_id, action, subject, amount, day) VALUES (@0, @1, @2, @3, @4) ON DUPLICATE KEY UPDATE amount = amount + @3",
                SteamId(player), Truncate(action, 32), Truncate(string.IsNullOrEmpty(subject) ? "unknown" : subject, 64), amount, Day());
        }

        #endregion

        #region Hooks: teams & missions

        private void OnTeamCreated(BasePlayer player, RelationshipManager.PlayerTeam team) => Team(team, "created", player, null);

        private void OnTeamAcceptInvite(RelationshipManager.PlayerTeam team, BasePlayer player) => Team(team, "joined", player, null);

        private void OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player) => Team(team, "left", player, null);

        private void OnTeamKick(RelationshipManager.PlayerTeam team, BasePlayer kicker, ulong target)
        {
            if (!_config.Logging.Teams || team == null) return;
            Count("team");
            Write("INSERT INTO mstats_teams (team_id, event, steam_id, name, actor_id, member_count, occurred_at) VALUES (@0, 'kicked', @1, @2, @3, @4, @5)",
                team.teamID, target, OwnerName(target), IsRealPlayer(kicker) ? (ulong?)SteamId(kicker) : null, team.members != null ? team.members.Count : 0, Stamp(Now));
        }

        private void OnTeamMemberPromote(RelationshipManager.PlayerTeam team, ulong newLeader)
        {
            if (!_config.Logging.Teams || team == null) return;
            Count("team");
            Write("INSERT INTO mstats_teams (team_id, event, steam_id, name, actor_id, member_count, occurred_at) VALUES (@0, 'promoted', @1, @2, @3, @4, @5)",
                team.teamID, newLeader, OwnerName(newLeader), team.teamLeader, team.members != null ? team.members.Count : 0, Stamp(Now));
        }

        private void OnTeamDisbanded(RelationshipManager.PlayerTeam team) => Team(team, "disbanded", null, null);

        private void Team(RelationshipManager.PlayerTeam team, string evt, BasePlayer player, ulong? actorId)
        {
            if (!_config.Logging.Teams || team == null) return;
            if (player != null && !IsRealPlayer(player)) return;
            Count("team");
            Write("INSERT INTO mstats_teams (team_id, event, steam_id, name, actor_id, member_count, occurred_at) VALUES (@0, @1, @2, @3, @4, @5, @6)",
                team.teamID, evt, player != null ? (ulong?)SteamId(player) : null, player != null ? DisplayName(player) : null, actorId, team.members != null ? team.members.Count : 0, Stamp(Now));
        }

        private void OnMissionSucceeded(BaseMission.MissionInstance instance, BasePlayer player) => Mission(instance, player, "succeeded", null);

        private void OnMissionFailed(BaseMission.MissionInstance instance, BasePlayer player, BaseMission.MissionFailReason reason, bool save) => Mission(instance, player, "failed", reason.ToString());

        private void Mission(BaseMission.MissionInstance instance, BasePlayer player, string result, string reason)
        {
            if (!_config.Logging.Missions || !IsRealPlayer(player)) return;
            Count("mission");
            var mission = instance != null ? instance.GetMission() : null;
            var name = mission != null && !string.IsNullOrEmpty(mission.shortname) ? mission.shortname : instance != null ? instance.missionID.ToString() : "unknown";
            Write("INSERT INTO mstats_missions (steam_id, mission, result, reason, occurred_at) VALUES (@0, @1, @2, @3, @4)",
                SteamId(player), Truncate(name, 64), result, Truncate(reason, 32), Stamp(Now));
        }

        #endregion

        #region Hooks: server events

        private void OnAirdrop(CargoPlane plane, Vector3 location) => ServerEvent("airdrop", plane, location, null, null, null);

        private void OnCargoPlaneSignaled(CargoPlane plane, SupplySignal signal)
        {
            var player = signal != null ? signal.creatorEntity as BasePlayer : null;
            BaseEntity entity = signal;
            if (entity == null) entity = plane;
            ServerEvent("supply_signal", entity, entity != null ? entity.transform.position : Vector3.zero, IsRealPlayer(player) ? player : null, null, null);
        }

        private void OnSupplyDropLanded(SupplyDrop drop)
        {
            if (drop == null) return;
            ServerEvent("supply_drop_landed", drop, drop.transform.position, null, null, null);
        }

        private void OnEntitySpawned(BaseNetworkable networkable)
        {
            var entity = networkable as BaseEntity;
            if (entity == null || _state == DbState.Disabled || !_config.Logging.ServerEvents) return;

            if (entity is PatrolHelicopter) ServerEvent("patrol_helicopter_spawned", entity, entity.transform.position, null, null, null);
            else if (entity is CargoShip) ServerEvent("cargo_ship_spawned", entity, entity.transform.position, null, null, null);
            else if (entity is BradleyAPC) ServerEvent("bradley_spawned", entity, entity.transform.position, null, null, null);
            else if (entity is CH47HelicopterAIController) ServerEvent("ch47_spawned", entity, entity.transform.position, null, null, null);
            else if (entity is HackableLockedCrate) ServerEvent("locked_crate_spawned", entity, entity.transform.position, null, null, null);
        }

        private void OnCargoShipEgress(CargoShip ship)
        {
            if (ship == null) return;
            ServerEvent("cargo_ship_egress", ship, ship.transform.position, null, null, null);
        }

        private void OnHelicopterDropCrate(CH47HelicopterAIController heli)
        {
            if (heli == null) return;
            ServerEvent("ch47_crate_dropped", heli, heli.transform.position, null, null, null);
        }

        private void OnHelicopterRetire(PatrolHelicopterAI ai)
        {
            if (ai == null) return;
            ServerEvent("patrol_helicopter_retired", ai.helicopterBase, ai.transform.position, null, null, null);
        }

        private void OnCrateHack(HackableLockedCrate crate)
        {
            if (crate == null) return;
            var hacker = crate.originalHackerPlayerId > SteamIdOffset ? BasePlayer.FindByID(crate.originalHackerPlayerId) : null;
            ServerEvent("locked_crate_hack_started", crate, crate.transform.position, hacker, hacker == null && crate.originalHackerPlayerId > SteamIdOffset ? crate.originalHackerPlayerId : (ulong?)null, null);
        }

        private void OnCrateHackEnd(HackableLockedCrate crate)
        {
            if (crate == null) return;
            var hacker = crate.originalHackerPlayerId > SteamIdOffset ? BasePlayer.FindByID(crate.originalHackerPlayerId) : null;
            ServerEvent("locked_crate_hack_finished", crate, crate.transform.position, hacker, hacker == null && crate.originalHackerPlayerId > SteamIdOffset ? crate.originalHackerPlayerId : (ulong?)null, null);
        }

        private void OnCrateLanded(HackableLockedCrate crate)
        {
            if (crate == null) return;
            ServerEvent("locked_crate_landed", crate, crate.transform.position, null, null, null);
        }

        private void OnExcavatorMiningToggled(ExcavatorArm arm)
        {
            if (arm == null) return;
            // The hook is raised at the start of BeginMining/StopMining; read the state on the next tick.
            NextTick(() =>
            {
                if (arm == null || arm.IsDestroyed) return;
                ServerEvent(arm.IsMining() ? "excavator_started" : "excavator_stopped", arm, arm.transform.position, null, null, null);
            });
        }

        private void ServerEvent(string evt, BaseEntity entity, Vector3 position, BasePlayer player, ulong? steamId, string details)
        {
            if (!_config.Logging.ServerEvents) return;
            Count("server_event");

            ulong? id = steamId;
            string name = null;
            if (player != null && IsRealPlayer(player))
            {
                id = SteamId(player);
                name = DisplayName(player);
            }
            else if (id.HasValue)
            {
                name = OwnerName(id.Value);
            }

            var hasPosition = position != Vector3.zero;
            Write("INSERT INTO mstats_server_events (event, entity, entity_id, steam_id, name, pos_x, pos_y, pos_z, grid, details, occurred_at) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10)",
                Truncate(evt, 48), entity != null ? Truncate(entity.ShortPrefabName, 64) : null, entity != null && entity.net != null ? (ulong?)entity.net.ID.Value : null, id, Truncate(name, 128),
                hasPosition ? R(position.x) : (double?)null, hasPosition ? R(position.y) : (double?)null, hasPosition ? R(position.z) : (double?)null, hasPosition ? Grid(position) : null,
                Truncate(details, 255), Stamp(Now));
        }

        #endregion

        #region Console commands

        [ConsoleCommand("mstats.info")]
        private void InfoCommand(ConsoleSystem.Arg arg)
        {
            if (!HasCommandAccess(arg)) return;
            SendReply(arg, BuildDiagnostics());
        }

        [ConsoleCommand("mstats.reload")]
        private void ReloadCommand(ConsoleSystem.Arg arg)
        {
            if (!HasCommandAccess(arg)) return;
            SendReply(arg, "Reloading MStats...");
            NextTick(() => Interface.Oxide.ReloadPlugin(Name));
        }

        [ConsoleCommand("mstats.empty")]
        private void EmptyCommand(ConsoleSystem.Arg arg)
        {
            if (!HasCommandAccess(arg)) return;
            if (!RequireConfirm(arg, "mstats.empty", "permanently DELETE all rows in the MStats statistics tables")) return;
            if (!_schemaReady)
            {
                SendReply(arg, "Database is not ready; try again later.");
                return;
            }
            PrintWarning($"mstats.empty executed by {CommandSource(arg)}");
            TruncateStatistics("mstats.empty", () => SendReply(arg, "All MStats statistics tables emptied."));
        }

        [ConsoleCommand("mstats.drop")]
        private void DropCommand(ConsoleSystem.Arg arg)
        {
            if (!HasCommandAccess(arg)) return;
            if (!RequireConfirm(arg, "mstats.drop", "permanently DELETE all MStats tables including the schema and wipe history")) return;

            _schemaReady = false;
            var steps = new List<Action<Action<bool>>>();
            foreach (var table in Tables.Keys)
            {
                var name = table;
                steps.Add(done => Exec($"DROP TABLE IF EXISTS `{name}`", done));
            }
            RunSteps(steps, 0, ok =>
            {
                SendReply(arg, ok ? "All MStats tables dropped. Reload the plugin (mstats.reload) to recreate the schema." : "Dropping tables failed; see the server log.");
                PrintWarning($"mstats.drop executed by {CommandSource(arg)}: {(ok ? "success" : "FAILED")}");
            });
        }

        private bool HasCommandAccess(ConsoleSystem.Arg arg)
        {
            var player = arg != null ? arg.Player() : null;
            if (player == null) return true; // server console / RCON
            if (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionAdmin)) return true;
            SendReply(arg, "You do not have permission to use this command.");
            return false;
        }

        private bool RequireConfirm(ConsoleSystem.Arg arg, string command, string what)
        {
            if (_state == DbState.Disabled)
            {
                SendReply(arg, "MStats is disabled or has no database connection.");
                return false;
            }
            if (arg.HasArgs(1) && string.Equals(arg.GetString(0, string.Empty), "confirm", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            SendReply(arg, $"This will {what}. Run '{command} confirm' to proceed.");
            return false;
        }

        private static string CommandSource(ConsoleSystem.Arg arg)
        {
            var player = arg != null ? arg.Player() : null;
            if (player != null) return $"{player.displayName} ({player.UserIDString})";
            return arg != null && arg.IsRcon ? "RCON" : "server console";
        }

        /// <summary>
        /// Identifies the modding framework hosting the plugin. Carbon ships an Oxide compatibility layer,
        /// so the same Oxide.Core API is used on both; only the reported name/version and folder layout differ.
        /// </summary>
        private static string FrameworkDescription()
        {
            var carbon = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Carbon");
            if (carbon != null)
            {
                var v = carbon.GetName().Version;
                return $"Carbon {v.Major}.{v.Minor}.{v.Build}";
            }
            var rustExtension = Interface.Oxide.GetAllExtensions().FirstOrDefault(e => e.Name == "Rust");
            return $"Oxide {OxideMod.Version} (Rust extension {(rustExtension != null ? rustExtension.Version.ToString() : "n/a")})";
        }

        private string ConfigFilePath() => RelativeToRoot(Path.Combine(Interface.Oxide.ConfigDirectory, Name + ".json"));

        private static string PendingFilePath() => RelativeToRoot(Path.Combine(Interface.Oxide.DataDirectory, PendingDataFile + ".json"));

        private static string RelativeToRoot(string path)
        {
            var root = Interface.Oxide.RootDirectory;
            if (!string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(root.Length).TrimStart('\\', '/');
            }
            return path.Replace('\\', '/');
        }

        private string BuildDiagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("MStats diagnostic information");
            sb.AppendLine($"  Plugin version   : {Version}");
            sb.AppendLine($"  Schema version   : expected v{DatabaseSchemaVersion}, database {(_schemaVersionInDb < 0 ? "unknown" : "v" + _schemaVersionInDb)}, ready: {_schemaReady}");
            sb.AppendLine($"  Rust protocol    : {Protocol.printable} (network {Protocol.network}, save {Protocol.save})");
            sb.AppendLine($"  Framework        : {FrameworkDescription()}");
            sb.AppendLine($"  Config file      : {ConfigFilePath()}");
            if (_wipe != null)
            {
                sb.AppendLine($"  World            : {_wipe.Level} seed {_wipe.Seed} size {_wipe.WorldSize} wipe id {_wipe.WipeId ?? "n/a"}");
            }
            sb.AppendLine($"  Database         : {_state}{(_state == DbState.Online ? $" since {_connectedSince:yyyy-MM-dd HH:mm:ss} UTC" : string.Empty)} ({_config.Database.Host}:{_config.Database.Port}/{_config.Database.Database})");
            if (_state == DbState.Offline) sb.AppendLine($"  Next reconnect   : {_nextReconnectAt:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"  Statements       : sent {_sentStatements}, failed {_failedStatements}, buffered {_pending.Count}, dropped {_droppedStatements}");
            sb.AppendLine($"  Buffer on disk   : {(Interface.Oxide.DataFileSystem.ExistsDatafile(PendingDataFile) ? PendingFilePath() + " (mirrored on every server save while offline)" : "none")}");
            if (_lastError != null) sb.AppendLine($"  Last error       : {_lastError} at {_lastErrorAt:yyyy-MM-dd HH:mm:ss} UTC");
            if (_schemaError != null) sb.AppendLine($"  Schema error     : {_schemaError}");
            sb.AppendLine($"  Open sessions    : {_sessions.Count}");
            sb.AppendLine($"  Timestamps       : {(_config.Database.UseUtcTimestamps ? "UTC" : "server local time")}");

            var l = _config.Logging;
            var flags = new Dictionary<string, bool>
            {
                ["Connections"] = l.Connections,
                ["Gathering"] = l.Gathering,
                ["QuarryAndExcavator"] = l.QuarryAndExcavator,
                ["WorldItemPickups"] = l.WorldItemPickups,
                ["Crafting"] = l.Crafting,
                ["Items"] = l.Items,
                ["WeaponsFired"] = l.WeaponsFired,
                ["Hits"] = l.Hits,
                ["PvpKills"] = l.PvpKills,
                ["PveKills"] = l.PveKills,
                ["Deaths"] = l.Deaths,
                ["Building"] = l.Building,
                ["BuildingDestroyed"] = l.BuildingDestroyed,
                ["Deployables"] = l.Deployables,
                ["Cupboards"] = l.Cupboards,
                ["Chat"] = l.Chat,
                ["TeamChat"] = l.TeamChat,
                ["Commands"] = l.Commands,
                ["RconCommands"] = l.RconCommands,
                ["ServerMessages"] = l.ServerMessages,
                ["ServerEvents"] = l.ServerEvents,
                ["Teams"] = l.Teams,
                ["Missions"] = l.Missions,
                ["Activity"] = l.Activity,
                ["Moderation"] = l.Moderation,
                ["ServerStatus"] = _config.ServerStatus.Enabled,
                ["AdminLog"] = _config.AdminLog.Enabled
            };
            sb.AppendLine($"  Logging enabled  : {JoinOrDash(flags.Where(f => f.Value).Select(f => f.Key))}");
            sb.AppendLine($"  Logging disabled : {JoinOrDash(flags.Where(f => !f.Value).Select(f => f.Key))}");
            sb.AppendLine($"  Wipe handling    : truncate on map wipe {_config.Wipe.TruncateOnMapWipe}, on forced wipe {_config.Wipe.TruncateOnForcedWipe}");
            sb.AppendLine($"  Events since load: {JoinOrDash(_counters.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))}");
            return sb.ToString().TrimEnd();
        }

        private static string JoinOrDash(IEnumerable<string> values)
        {
            var list = values.ToList();
            return list.Count == 0 ? "-" : string.Join(", ", list);
        }

        #endregion

        #region Helpers

        private static bool IsRealPlayer(BasePlayer player) => player != null && !player.IsNpc && !(player is NPCPlayer) && (ulong)player.userID > SteamIdOffset;

        private static ulong SteamId(BasePlayer player) => (ulong)player.userID;

        private static string DisplayName(BasePlayer player) => Truncate(string.IsNullOrEmpty(player.displayName) ? player.UserIDString : player.displayName, 128);

        private static string Ip(BasePlayer player)
        {
            try
            {
                var connection = player.Connection;
                return connection != null ? Truncate(connection.IPAddressWithoutPort(), 45) : null;
            }
            catch
            {
                return null;
            }
        }

        private string OwnerName(ulong steamId)
        {
            if (steamId == 0) return "Server";
            var online = BasePlayer.FindByID(steamId);
            if (online != null) return DisplayName(online);
            var known = covalence.Players.FindPlayerById(steamId.ToString());
            return known != null && !string.IsNullOrEmpty(known.Name) ? Truncate(known.Name, 128) : steamId.ToString();
        }

        private static ItemRef Ref(ItemDefinition definition)
        {
            if (definition == null) return ItemRef.Unknown;
            var english = definition.displayName != null ? definition.displayName.english : null;
            return new ItemRef
            {
                Shortname = Truncate(string.IsNullOrEmpty(definition.shortname) ? "unknown" : definition.shortname, 64),
                Name = Truncate(string.IsNullOrEmpty(english) ? definition.shortname : english, 128)
            };
        }

        private static ItemRef HeldRef(HeldEntity held)
        {
            var item = held != null ? held.GetItem() : null;
            return item != null && item.info != null ? Ref(item.info) : null;
        }

        private static ItemRef ActiveRef(BasePlayer player)
        {
            var item = player != null ? player.GetActiveItem() : null;
            return item != null && item.info != null ? Ref(item.info) : null;
        }

        private static ItemRef PrefabRef(BaseEntity entity)
        {
            var prefab = entity != null ? entity.ShortPrefabName : null;
            if (string.IsNullOrEmpty(prefab)) return ItemRef.Unknown;
            return new ItemRef { Shortname = Truncate(prefab, 64), Name = Truncate(PrettyPrefab(prefab), 128) };
        }

        private static string BlockName(BuildingBlock block)
        {
            string name = null;
            if (block != null && block.blockDefinition != null && block.blockDefinition.info.name != null)
            {
                name = block.blockDefinition.info.name.english;
            }
            return string.IsNullOrEmpty(name) ? PrettyPrefab(block != null ? block.ShortPrefabName : null) : name;
        }

        private static string PrettyPrefab(string shortPrefabName)
        {
            if (string.IsNullOrEmpty(shortPrefabName)) return "Unknown";
            var name = shortPrefabName.Replace(".entity", string.Empty).Replace(".deployed", string.Empty).Replace(".prefab", string.Empty).Replace('_', ' ').Replace('.', ' ');
            return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name.Substring(1) : name;
        }

        private static string Grid(Vector3 position)
        {
            try
            {
                return Truncate(MapHelper.GridToString(MapHelper.PositionToGrid(position)), 8);
            }
            catch
            {
                return null;
            }
        }

        private static double R(float value) => Math.Round(value, 1);

        private static string Truncate(string value, int max)
        {
            if (value == null) return null;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private DateTime Now => _config.Database.UseUtcTimestamps ? DateTime.UtcNow : DateTime.Now;

        private static string Stamp(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss");

        private string Day() => Now.ToString("yyyy-MM-dd");

        private void Count(string key)
        {
            long n;
            _counters.TryGetValue(key, out n);
            _counters[key] = n + 1;
        }

        #endregion
    }
}
