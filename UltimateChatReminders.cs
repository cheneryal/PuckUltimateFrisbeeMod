using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace PuckUltimateFrisbeeMod
{
    internal sealed class UltimateChatReminderRunner : MonoBehaviour
    {
        private void Update()
        {
            UltimateChatReminders.Tick();
        }
    }

    public static class UltimateChatReminders
    {
        private const string LinkPrefix = "ultimatefrisbee:url:";
        private const string DiscordUrl = "https://discord.gg/3WuvPHgW9y";

        // Posts once ~15s after joining a server so players see it (and you can test quickly),
        // then repeats on every 4-minute wall-clock boundary. Set to -1f to disable the join post.
        private const float InitialJoinDelaySeconds = 15f;
        private const float IntervalSeconds = 240f;
        private const int MaxContentBytes = 480;

        private struct ReminderEntry
        {
            public string Tag;
            public string AccentHex; // 6-digit #rrggbb
            public string Text;
            public string Url;
            public string ButtonLabel;
        }

        private static readonly ReminderEntry[] Reminders =
        {
            new ReminderEntry
            {
                Tag = "DISCORD",
                AccentHex = "#a5adff",
                Text = "Join the Pultimate Frisbee community Discord for PUGs & updates!",
                Url = DiscordUrl,
                ButtonLabel = "JOIN DISCORD"
            },
        };

        private static readonly Regex OurLinkRegex = new Regex(
            "<link=\"" + Regex.Escape(LinkPrefix) + "([^\"]+)\"",
            RegexOptions.Compiled);

        private static readonly FieldInfo UiChatMessagesField =
            AccessTools.Field(typeof(UIChat), "uiChatMessages");

        private static GameObject _runnerObject;
        private static UIChat _chat;
        private static long _lastSlot = long.MinValue;
        private static bool _wasInSession = false;
        private static float _nextPollTime = 0f;
        private static float _pendingJoinPostAt = -1f;

        public static void Initialize()
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;

            Dispose();

            _runnerObject = new GameObject("UltimateChatReminderRunner");
            UnityEngine.Object.DontDestroyOnLoad(_runnerObject);
            _runnerObject.AddComponent<UltimateChatReminderRunner>();

            UltimateFrisbeePlugin.ModLogger.Info("UltimateChatReminders: Initialized client runner.");
        }

        public static void Dispose()
        {
            if (_runnerObject != null)
            {
                UnityEngine.Object.Destroy(_runnerObject);
                _runnerObject = null;
            }

            _chat = null;
            _lastSlot = long.MinValue;
            _wasInSession = false;
            _nextPollTime = 0f;
            _pendingJoinPostAt = -1f;
        }

        internal static void Tick()
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;
            if (Reminders.Length == 0) return;

            if (Time.unscaledTime < _nextPollTime) return;
            _nextPollTime = Time.unscaledTime + 0.5f;

            var nm = NetworkManager.Singleton;
            bool inSession = nm != null && nm.IsListening;
            if (!inSession)
            {
                if (_wasInSession)
                {
                    _wasInSession = false;
                    _lastSlot = long.MinValue;
                    _pendingJoinPostAt = -1f;
                }
                return;
            }

            if (ChatManager.Instance == null) return;

            double wallSeconds = Utils.GetTimestamp() / 1000.0;
            long slot = (long)Math.Floor(wallSeconds / IntervalSeconds);

            if (!_wasInSession)
            {
                _wasInSession = true;
                _lastSlot = slot;
                if (InitialJoinDelaySeconds >= 0f)
                {
                    _pendingJoinPostAt = Time.unscaledTime + InitialJoinDelaySeconds;
                }
                UltimateFrisbeePlugin.ModLogger.Info(
                    $"UltimateChatReminders: Session active (slot {slot}). Next join post in {InitialJoinDelaySeconds:0}s.");
                return;
            }

            // One-time post shortly after joining a match
            if (_pendingJoinPostAt >= 0f && Time.unscaledTime >= _pendingJoinPostAt)
            {
                _pendingJoinPostAt = -1f;
                int joinIdx = (int)(((slot % Reminders.Length) + Reminders.Length) % Reminders.Length);
                PostReminder(Reminders[joinIdx]);
                return;
            }

            // Subsequent synchronized wall-clock posts
            if (_lastSlot == long.MinValue)
            {
                _lastSlot = slot;
                return;
            }
            if (slot == _lastSlot) return;

            _lastSlot = slot;

            int idx = (int)(((slot % Reminders.Length) + Reminders.Length) % Reminders.Length);
            PostReminder(Reminders[idx]);
        }

        public static void PostReminder(int index = 0)
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;
            if (index < 0 || index >= Reminders.Length) return;
            PostReminder(Reminders[index]);
        }

        private static void PostReminder(ReminderEntry entry)
        {
            try
            {
                var chat = ChatManager.Instance;
                if (chat == null)
                {
                    UltimateFrisbeePlugin.ModLogger.Warning("UltimateChatReminders: ChatManager.Instance is null.");
                    return;
                }

                string formatted = FormatMessage(entry);
                int byteCount = Encoding.UTF8.GetByteCount(formatted);
                if (byteCount > MaxContentBytes)
                {
                    UltimateFrisbeePlugin.ModLogger.Warning(
                        $"UltimateChatReminders: Reminder too long ({byteCount}/{MaxContentBytes} UTF-8 bytes); skipping.");
                    return;
                }

                var msg = new ChatMessage
                {
                    SteamID = null,
                    Username = null,
                    Team = null,
                    Content = new FixedString512Bytes(formatted),
                    Timestamp = Utils.GetTimestamp(),
                    IsQuickChat = false,
                    IsTeamChat = false,
                    IsSystem = true
                };

                chat.AddChatMessage(msg);
                UltimateFrisbeePlugin.ModLogger.Info($"UltimateChatReminders: Posted [{entry.Tag}] reminder.");
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"UltimateChatReminders: Failed to post reminder: {ex}");
            }
        }

        private static string FormatMessage(ReminderEntry entry)
        {
            string accent = entry.AccentHex;
            string plateColor = accent + "66";
            string button = string.IsNullOrEmpty(entry.ButtonLabel) ? "CLICK HERE" : entry.ButtonLabel;

            return
                $"<size=70%>" +
                $"<b><i><color={accent}>{entry.Tag}</color></i></b> " +
                $"<color={accent}>\u2022</color> " +
                $"{entry.Text}  " +
                $"<link=\"{LinkPrefix}{entry.Url}\">" +
                $"<mark={plateColor} padding=15,15,-6,-10><nobr><b><color={accent}>{button}</color></b></nobr></mark>" +
                $"</link></size>";
        }

        internal static void OnChatRowAdded(UIChat uiChat, ChatMessage chatMessage)
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer || uiChat == null || UiChatMessagesField == null)
                return;

            try
            {
                string content = chatMessage.Content.ToString();
                if (!OurLinkRegex.IsMatch(content)) return;

                var rows = UiChatMessagesField.GetValue(uiChat) as List<UIChatMessage>;
                if (rows == null || rows.Count == 0) return;

                var row = rows[rows.Count - 1];
                var label = row?.VisualElement?.Q<Label>();
                if (label == null) return;

                _chat = uiChat;
                label.UnregisterCallback<UnityEngine.UIElements.Experimental.PointerUpLinkTagEvent>(OnLinkClicked);
                label.RegisterCallback<UnityEngine.UIElements.Experimental.PointerUpLinkTagEvent>(OnLinkClicked);
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Warning($"UltimateChatReminders: Failed to bind chat link: {ex.Message}");
            }
        }

        private static void OnLinkClicked(UnityEngine.UIElements.Experimental.PointerUpLinkTagEvent e)
        {
            try
            {
                string id = e.linkID;
                if (string.IsNullOrEmpty(id) || !id.StartsWith(LinkPrefix, StringComparison.OrdinalIgnoreCase))
                    return;

                string url = id.Substring(LinkPrefix.Length);
                if (!Uri.IsWellFormedUriString(url, UriKind.Absolute)) return;
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    return;

                try { _chat?.StopInput(); } catch { }
                Application.OpenURL(url);
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Warning($"UltimateChatReminders: Link click failed: {ex.Message}");
            }
        }
    }

    [HarmonyPatch]
    public static class UIChat_AddChatMessage_UltimateLinks_Patch
    {
        private static MethodBase _target;

        [HarmonyPrepare]
        public static bool Prepare()
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return false;

            _target = AccessTools.Method(
                typeof(UIChat),
                "AddChatMessage",
                new[] { typeof(ChatMessage), typeof(Units), typeof(bool) });

            if (_target != null) return true;

            var matches = new List<MethodInfo>();
            for (var t = typeof(UIChat); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                               BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == "AddChatMessage") matches.Add(m);
                }
            }

            if (matches.Count == 1)
            {
                _target = matches[0];
                return true;
            }

            UltimateFrisbeePlugin.ModLogger.Warning(
                $"UltimateChatReminders: Could not uniquely resolve UIChat.AddChatMessage (found {matches.Count}); chat links disabled.");
            return false;
        }

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod() => _target;

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        public static void Postfix(UIChat __instance, ChatMessage chatMessage)
        {
            if (!chatMessage.IsSystem) return;
            UltimateChatReminders.OnChatRowAdded(__instance, chatMessage);
        }
    }
}