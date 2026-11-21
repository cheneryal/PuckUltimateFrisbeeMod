using System;
using System.Collections;
using System.Reflection;
using System.Net.Security;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace PuckUltimateFrisbeeMod
{
    public static class UltimateMatchReporter
    {
        public static bool IsMatchServer { get; private set; } = false;

        private static string _matchId;
        private static string _callbackUrl;
        private static string _webhookSecret;
        private static bool _hasReported = false;
        private static PlayerTeam _forfeitingTeam = PlayerTeam.None;
        private static float _forfeitDetectedTime = -999f;

        // Empty-server watchdog state
        private static float _serverStartTime = 0f;
        private static bool _anyPlayerEverJoined = false;
        private static float _emptySinceTime = -1f;
        private static float _nextWatchdogCheckTime = 0f;

        private const float POST_GAME_GRACE_SECONDS = 15f;
        private const float EMPTY_AFTER_JOIN_TIMEOUT = 180f;   // 3 min empty after players joined
        private const float NO_SHOW_TIMEOUT = 480f;            // 8 min if nobody ever joined

        public static void Initialize()
        {
            IsMatchServer = false;
            _hasReported = false;
            _anyPlayerEverJoined = false;
            _forfeitingTeam = PlayerTeam.None;
            _emptySinceTime = -1f;
            _serverStartTime = Time.realtimeSinceStartup;
            _nextWatchdogCheckTime = _serverStartTime + 10f;

            if (!UltimateFrisbeePlugin.IsHeadlessServer)
                return;

            string metaPath = Path.Combine(Directory.GetCurrentDirectory(), "match_meta.json");
            if (!File.Exists(metaPath))
            {
                UltimateFrisbeePlugin.ModLogger.Info("UltimateMatchReporter: No match_meta.json found (public server mode).");
                return;
            }

            try
            {
                string raw = File.ReadAllText(metaPath, Encoding.UTF8);
                _matchId = ExtractJsonString(raw, "match_id");
                _callbackUrl = ExtractJsonString(raw, "callback_url");
                _webhookSecret = ExtractJsonString(raw, "webhook_secret");

                if (!string.IsNullOrEmpty(_matchId) && !string.IsNullOrEmpty(_callbackUrl))
                {
                    IsMatchServer = true;
                    UltimateFrisbeePlugin.ModLogger.Info($"UltimateMatchReporter: Armed for Match #{_matchId} -> {_callbackUrl}");
                }
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"UltimateMatchReporter: Failed to read match_meta.json: {ex}");
            }
        }

        public static void Dispose()
        {
            IsMatchServer = false;
            _hasReported = false;
        }

        public static void RecordForfeitVote(Vote vote)
        {
            if (!IsMatchServer || !NetworkRoles.IsServer || vote == null)
                return;

            if (!vote.Passed || !string.Equals(vote.Name, "forfeit", StringComparison.OrdinalIgnoreCase))
                return;

            PlayerTeam detected = ExtractTeamFromVote(vote);
            if (detected == PlayerTeam.Red || detected == PlayerTeam.Blue)
            {
                _forfeitingTeam = detected;
                _forfeitDetectedTime = Time.realtimeSinceStartup;
                UltimateFrisbeePlugin.ModLogger.Info(
                    $"UltimateMatchReporter: Detected passed forfeit vote by {detected}.");
            }
        }

        private static PlayerTeam ExtractTeamFromVote(Vote vote)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type t = vote.GetType();

            // 1. Check any PlayerTeam or collection of PlayerTeam (e.g. Teams / AllowedTeams)
            foreach (var prop in t.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                object val = null;
                try { val = prop.GetValue(vote); } catch { continue; }
                PlayerTeam found = UnwrapTeamValue(val);
                if (found != PlayerTeam.None) return found;
            }
            foreach (var field in t.GetFields(flags))
            {
                object val = null;
                try { val = field.GetValue(vote); } catch { continue; }
                PlayerTeam found = UnwrapTeamValue(val);
                if (found != PlayerTeam.None) return found;
            }
            return PlayerTeam.None;
        }

        private static PlayerTeam UnwrapTeamValue(object val)
        {
            if (val is PlayerTeam pt && (pt == PlayerTeam.Red || pt == PlayerTeam.Blue))
                return pt;

            if (val is IEnumerable seq && !(val is string))
            {
                PlayerTeam found = PlayerTeam.None;
                int count = 0;
                foreach (var item in seq)
                {
                    if (item is PlayerTeam teamItem && (teamItem == PlayerTeam.Red || teamItem == PlayerTeam.Blue))
                    {
                        found = teamItem;
                        count++;
                    }
                }
                if (count == 1) return found;
            }
            return PlayerTeam.None;
        }

        public static void OnGameOver(int redScore, int blueScore, int period)
        {
            if (!IsMatchServer || !NetworkRoles.IsServer || _hasReported)
                return;

            _hasReported = true;

            bool isForfeit = false;
            int? winnerTeam = null;

            if ((_forfeitingTeam == PlayerTeam.Red || _forfeitingTeam == PlayerTeam.Blue) &&
                (Time.realtimeSinceStartup - _forfeitDetectedTime) < 10f)
            {
                isForfeit = true;
                winnerTeam = (_forfeitingTeam == PlayerTeam.Blue) ? 1 : 2; // Opposing team wins
            }
            else if (redScore > blueScore)
            {
                winnerTeam = 1; // Red
            }
            else if (blueScore > redScore)
            {
                winnerTeam = 2; // Blue
            }

            var playerStatsJson = CollectPlayerStatsJson();
            string winnerJson = winnerTeam.HasValue ? winnerTeam.Value.ToString() : "null";
            string forfeitJson = isForfeit ? "true" : "false";

            string payload =
                "{" +
                $"\"match_id\":\"{EscapeJson(_matchId)}\"," +
                $"\"winner_team\":{winnerJson}," +
                $"\"forfeit\":{forfeitJson}," +
                $"\"red_score\":{redScore}," +
                $"\"blue_score\":{blueScore}," +
                $"\"period\":{period}," +
                $"\"players\":{playerStatsJson}" +
                "}";

            string tag = isForfeit ? $" (Forfeit by {_forfeitingTeam})" : "";
            UltimateFrisbeePlugin.ModLogger.Info(
                $"UltimateMatchReporter: GameOver detected for Match #{_matchId} (Red {redScore} - Blue {blueScore}){tag}. Reporting in {POST_GAME_GRACE_SECONDS:F0}s...");

            UltimateRulesManager.BroadcastAnnouncement(
                $"FINAL: RED {redScore} - {blueScore} BLUE{tag}! Reporting to Discord & closing server in {POST_GAME_GRACE_SECONDS:F0}s...",
                "#2ecc71");

            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(POST_GAME_GRACE_SECONDS));
                PostMatchResult(payload);
            });
        }

        public static void TickEmptyServerWatchdog()
        {
            if (!IsMatchServer || !NetworkRoles.IsServer || _hasReported)
                return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextWatchdogCheckTime)
                return;
            _nextWatchdogCheckTime = now + 10f;

            int playerCount = 0;
            if (MonoBehaviourSingleton<PlayerManager>.Instance != null)
            {
                playerCount = MonoBehaviourSingleton<PlayerManager>.Instance
                    .GetPlayers(false)
                    .Count(p => p != null);
            }

            if (playerCount > 0)
            {
                _anyPlayerEverJoined = true;
                _emptySinceTime = -1f;
                return;
            }

            if (_anyPlayerEverJoined)
            {
                if (_emptySinceTime < 0f)
                {
                    _emptySinceTime = now;
                }
                else if (now - _emptySinceTime >= EMPTY_AFTER_JOIN_TIMEOUT)
                {
                    ReportEmptyCancel("Server empty for 3 minutes after players left.");
                }
            }
            else if (now - _serverStartTime >= NO_SHOW_TIMEOUT)
            {
                ReportEmptyCancel("No players connected within 8 minutes.");
            }
        }

        private static void ReportEmptyCancel(string reason)
        {
            if (_hasReported) return;
            _hasReported = true;
            UltimateFrisbeePlugin.ModLogger.Info($"UltimateMatchReporter: Auto-cancelling Match #{_matchId}: {reason}");

            string payload =
                "{" +
                $"\"match_id\":\"{EscapeJson(_matchId)}\"," +
                "\"winner_team\":null," +
                $"\"reason\":\"{EscapeJson(reason)}\"" +
                "}";

            Task.Run(() => PostMatchResult(payload));
        }

        private static string CollectPlayerStatsJson()
        {
            var sb = new StringBuilder();
            sb.Append("[");
            bool first = true;

            if (MonoBehaviourSingleton<PlayerManager>.Instance != null)
            {
                foreach (Player p in MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(false))
                {
                    if (p == null) continue;
                    PlayerTeam team = UltimateRulesManager.GetTeam(p);
                    if (team != PlayerTeam.Red && team != PlayerTeam.Blue) continue;

                    string name = p.Username != null ? p.Username.Value.ToString() : "Player";
                    int goals = p.Goals != null ? p.Goals.Value : 0;
                    int assists = p.Assists != null ? p.Assists.Value : 0;
                    string teamStr = team == PlayerTeam.Red ? "red" : "blue";

                    if (!first) sb.Append(",");
                    first = false;

                    sb.Append("{")
                      .Append($"\"name\":\"{EscapeJson(name)}\",")
                      .Append($"\"team\":\"{teamStr}\",")
                      .Append($"\"goals\":{goals},")
                      .Append($"\"assists\":{assists}")
                      .Append("}");
                }
            }

            sb.Append("]");
            return sb.ToString();
        }

        private static void PostMatchResult(string jsonPayload)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/curl",
                        Arguments =
                            $"-sS -f -m 10 -X POST " +
                            $"-H \"Content-Type: application/json\" " +
                            $"-H \"X-Match-Secret: {_webhookSecret}\" " +
                            $"--data-binary @- \"{_callbackUrl}\"",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var proc = Process.Start(psi))
                    {
                        using (var writer = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
                        {
                            writer.Write(jsonPayload);
                        }

                        string stdout = proc.StandardOutput.ReadToEnd();
                        string stderr = proc.StandardError.ReadToEnd();
                        proc.WaitForExit(15000);

                        if (proc.ExitCode == 0)
                        {
                            UltimateFrisbeePlugin.ModLogger.Info(
                                $"UltimateMatchReporter: Posted Match #{_matchId} result successfully ({stdout.Trim()}).");
                            return;
                        }

                        UltimateFrisbeePlugin.ModLogger.Error(
                            $"UltimateMatchReporter: Attempt {attempt} curl exited {proc.ExitCode}: {stderr.Trim()}");
                    }
                }
                catch (Exception ex)
                {
                    UltimateFrisbeePlugin.ModLogger.Error(
                        $"UltimateMatchReporter: Attempt {attempt} failed posting Match #{_matchId}: {ex.Message}");
                }

                System.Threading.Thread.Sleep(2000);
            }
        }



        private static string ExtractJsonString(string json, string key)
        {
            var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
        }
    }

    // Hook GameManager.Server_SetGameState for exact GameOver transition
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.Server_SetGameState))]
    public static class GameManager_ReportMatchOver_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(GameManager __instance, GamePhase? phase, int? tick, int? period, int? blueScore, int? redScore, bool? isOvertime)
        {
            if (!UltimateMatchReporter.IsMatchServer || !NetworkRoles.IsServer)
                return;

            if (phase == GamePhase.GameOver && __instance.GameState.Value.Phase != GamePhase.GameOver)
            {
                int finalRed = redScore ?? __instance.GameState.Value.RedScore;
                int finalBlue = blueScore ?? __instance.GameState.Value.BlueScore;
                int finalPeriod = period ?? __instance.GameState.Value.Period;
                UltimateMatchReporter.OnGameOver(finalRed, finalBlue, finalPeriod);
            }
        }
    }

    // Lightweight watchdog tick (runs only on private match servers, once every 10s)
    [HarmonyPatch(typeof(PhysicsManager), "Update")]
    public static class PhysicsManager_MatchWatchdog_Patch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (UltimateMatchReporter.IsMatchServer)
            {
                UltimateMatchReporter.TickEmptyServerWatchdog();
            }
        }
    }
    [HarmonyPatch(typeof(BaseGameMode<BaseGameModeConfig>), "OnVoteRemoved")]
    public static class BaseGameMode_TrackForfeit_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Vote vote)
        {
            if (UltimateMatchReporter.IsMatchServer)
            {
                UltimateMatchReporter.RecordForfeitVote(vote);
            }
        }
    }
}