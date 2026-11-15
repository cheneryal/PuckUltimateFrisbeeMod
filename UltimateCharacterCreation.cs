using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PuckUltimateFrisbeeMod
{
    public enum UltimateGender : byte
    {
        Masculine = 0,
        Feminine = 1
    }

    public enum UltimateHairStyle : byte
    {
        None = 0,
        ShortFlow = 1,
        Ponytail = 2,
        LongFlow = 3,
        AfroCrown = 4,
        TopBun = 5
    }

    public enum UltimateHeadGear : byte
    {
        None = 0,
        BackwardCap = 1,
        ForwardCap = 2,
        SunVisor = 3,
        Headband = 4,
        Beanie = 5
    }

    [Serializable]
    public struct PlayerAppearanceData : IEquatable<PlayerAppearanceData>
    {
        public byte Gender;
        public byte HairStyle;
        public byte HeadGear;
        public byte SkinToneIndex;
        public byte HairColorIndex;

        public static PlayerAppearanceData Default => new PlayerAppearanceData
        {
            Gender = (byte)UltimateGender.Masculine,
            HairStyle = (byte)UltimateHairStyle.ShortFlow,
            HeadGear = (byte)UltimateHeadGear.BackwardCap,
            SkinToneIndex = 0,
            HairColorIndex = 0
        };

        public bool Equals(PlayerAppearanceData other) =>
            Gender == other.Gender &&
            HairStyle == other.HairStyle &&
            HeadGear == other.HeadGear &&
            SkinToneIndex == other.SkinToneIndex &&
            HairColorIndex == other.HairColorIndex;
    }

    public static class UltimateAppearanceManager
    {
        public const string ClientToServerMsg = "ULTIMATE/AppearanceReq";
        public const string ServerToAllMsg    = "ULTIMATE/AppearanceSync";

        private static CustomMessagingManager _registeredCmm;
        private static string _configPath;
        private static float _lastClientCheckTime;
        private static bool _hasSentInitialLocalAppearance;

        public static PlayerAppearanceData LocalAppearance = PlayerAppearanceData.Default;
        public static readonly Dictionary<ulong, PlayerAppearanceData> SyncedAppearances = new Dictionary<ulong, PlayerAppearanceData>();

        public static readonly string[] GenderNames = { "Masculine", "Feminine" };
        public static readonly string[] HairNames   = { "Bald / None", "Short Flow", "Ponytail", "Long Flow", "Afro Crown", "Top Bun" };
        public static readonly string[] GearNames   = { "None", "Backward Cap", "Forward Cap", "Sun Visor", "Sweatband", "Knit Beanie" };

        public static readonly string[] SkinToneNames = { "Game Default", "Fair", "Warm Peach", "Light Olive", "Golden Tan", "Caramel", "Rich Umber", "Deep Espresso" };
        public static readonly Color[] SkinTonePalette =
        {
            new Color(0.92f, 0.74f, 0.60f, 1f),
            new Color(0.97f, 0.84f, 0.76f, 1f),
            new Color(0.92f, 0.74f, 0.60f, 1f),
            new Color(0.87f, 0.69f, 0.53f, 1f),
            new Color(0.78f, 0.57f, 0.41f, 1f),
            new Color(0.64f, 0.43f, 0.29f, 1f),
            new Color(0.46f, 0.29f, 0.19f, 1f),
            new Color(0.28f, 0.18f, 0.13f, 1f),
        };

        public static readonly string[] HairColorNames = { "Game Default", "Espresso", "Jet Black", "Chestnut Brown", "Golden Blonde", "Platinum", "Copper Auburn", "Silver Grey", "Electric Blue" };
        public static readonly Color[] HairColorPalette =
        {
            new Color(0.18f, 0.12f, 0.08f, 1f),
            new Color(0.18f, 0.12f, 0.08f, 1f),
            new Color(0.07f, 0.07f, 0.09f, 1f),
            new Color(0.36f, 0.21f, 0.12f, 1f),
            new Color(0.82f, 0.66f, 0.34f, 1f),
            new Color(0.92f, 0.89f, 0.82f, 1f),
            new Color(0.62f, 0.22f, 0.11f, 1f),
            new Color(0.60f, 0.62f, 0.65f, 1f),
            new Color(0.18f, 0.58f, 0.95f, 1f),
        };

        public static void Initialize(string pluginDirectory)
        {
            _configPath = Path.Combine(pluginDirectory ?? "", "ultimate_appearance.json");
            LoadLocalConfig();
            UltimateCustomizationUI.Initialize();
        }

        public static void Dispose()
        {
            UnregisterNetwork();
            UltimateCustomizationUI.Shutdown();
            SyncedAppearances.Clear();
        }

        public static void LoadLocalConfig()
        {
            try
            {
                if (!string.IsNullOrEmpty(_configPath) && File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    LocalAppearance = JsonUtility.FromJson<PlayerAppearanceData>(json);
                }
            }
            catch
            {
                LocalAppearance = PlayerAppearanceData.Default;
            }
        }

        public static void SaveLocalConfig()
        {
            try
            {
                if (!string.IsNullOrEmpty(_configPath))
                {
                    string json = JsonUtility.ToJson(LocalAppearance, true);
                    File.WriteAllText(_configPath, json);
                }
            }
            catch { }
        }

        public static void EnsureNetworkRegistered()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null) return;
            if (_registeredCmm == nm.CustomMessagingManager) return;

            nm.CustomMessagingManager.UnregisterNamedMessageHandler(ClientToServerMsg);
            nm.CustomMessagingManager.UnregisterNamedMessageHandler(ServerToAllMsg);

            nm.CustomMessagingManager.RegisterNamedMessageHandler(ClientToServerMsg, OnServerReceiveClientAppearance);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(ServerToAllMsg, OnClientReceiveAppearanceSync);

            _registeredCmm = nm.CustomMessagingManager;
            _hasSentInitialLocalAppearance = false;
        }

        public static void UnregisterNetwork()
        {
            if (_registeredCmm != null)
            {
                _registeredCmm.UnregisterNamedMessageHandler(ClientToServerMsg);
                _registeredCmm.UnregisterNamedMessageHandler(ServerToAllMsg);
                _registeredCmm = null;
            }
            _hasSentInitialLocalAppearance = false;
        }

        public static void TickClientSyncCheck()
        {
            EnsureNetworkRegistered();
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient)
            {
                _hasSentInitialLocalAppearance = false;
                return;
            }

            if (!_hasSentInitialLocalAppearance && Time.unscaledTime - _lastClientCheckTime > 1.0f)
            {
                _lastClientCheckTime = Time.unscaledTime;
                _hasSentInitialLocalAppearance = true;
                ClientSendAppearanceToServer(LocalAppearance);
            }
        }

        public static PlayerAppearanceData GetAppearance(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsClient && clientId == nm.LocalClientId)
                return LocalAppearance;

            return SyncedAppearances.TryGetValue(clientId, out var data) ? data : PlayerAppearanceData.Default;
        }

        public static void SetAndBroadcastLocalAppearance(PlayerAppearanceData newData)
        {
            LocalAppearance = newData;
            SaveLocalConfig();

            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsClient)
            {
                SyncedAppearances[nm.LocalClientId] = newData;
                RefreshPlayerVisualsByClientId(nm.LocalClientId);
                ClientSendAppearanceToServer(newData);
            }
        }

        public static void ClientSendAppearanceToServer(PlayerAppearanceData data)
        {
            EnsureNetworkRegistered();
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient || nm.CustomMessagingManager == null) return;

            SyncedAppearances[nm.LocalClientId] = data;

            if (nm.IsServer)
            {
                ServerBroadcastAllAppearances();
                RefreshPlayerVisualsByClientId(nm.LocalClientId);
                return;
            }

            using (var writer = new FastBufferWriter(16, Allocator.Temp))
            {
                WriteAppearance(writer, data);
                nm.CustomMessagingManager.SendNamedMessage(ClientToServerMsg, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
            }
        }

        private static void OnServerReceiveClientAppearance(ulong senderClientId, FastBufferReader reader)
        {
            if (!NetworkRoles.IsServer) return;
            try
            {
                PlayerAppearanceData data = ReadAppearance(reader);
                SyncedAppearances[senderClientId] = data;
                RefreshPlayerVisualsByClientId(senderClientId);
                ServerBroadcastAllAppearances();
            }
            catch { }
        }

        public static void ServerBroadcastAllAppearances()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
            if (nm.ConnectedClientsIds == null || nm.ConnectedClientsIds.Count == 0) return;

            using (var writer = new FastBufferWriter(16 + SyncedAppearances.Count * 16, Allocator.Temp))
            {
                writer.WriteValueSafe((ushort)SyncedAppearances.Count);
                foreach (var kvp in SyncedAppearances)
                {
                    writer.WriteValueSafe(kvp.Key);
                    WriteAppearance(writer, kvp.Value);
                }
                nm.CustomMessagingManager.SendNamedMessageToAll(ServerToAllMsg, writer, NetworkDelivery.Reliable);
            }
        }

        private static void OnClientReceiveAppearanceSync(ulong senderClientId, FastBufferReader reader)
        {
            if (NetworkRoles.IsServer) return;
            try
            {
                reader.ReadValueSafe(out ushort count);
                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out ulong clientId);
                    PlayerAppearanceData data = ReadAppearance(reader);
                    SyncedAppearances[clientId] = data;
                    RefreshPlayerVisualsByClientId(clientId);
                }
            }
            catch { }
        }

        public static void RefreshPlayerVisualsByClientId(ulong clientId)
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;
            Player player = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerByClientId(clientId);
            if (player == null || player.PlayerBody == null) return;

            var syncComp = player.PlayerBody.GetComponentInChildren<UltimateSkinAndTeamSync>(true);
            if (syncComp != null)
            {
                syncComp.ApplyCustomization(force: true);
            }
        }

        private static void WriteAppearance(FastBufferWriter writer, PlayerAppearanceData d)
        {
            writer.WriteValueSafe(d.Gender);
            writer.WriteValueSafe(d.HairStyle);
            writer.WriteValueSafe(d.HeadGear);
            writer.WriteValueSafe(d.SkinToneIndex);
            writer.WriteValueSafe(d.HairColorIndex);
        }

        private static PlayerAppearanceData ReadAppearance(FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte gender);
            reader.ReadValueSafe(out byte hair);
            reader.ReadValueSafe(out byte gear);
            reader.ReadValueSafe(out byte skinIdx);
            reader.ReadValueSafe(out byte hairColIdx);
            return new PlayerAppearanceData
            {
                Gender = gender,
                HairStyle = hair,
                HeadGear = gear,
                SkinToneIndex = skinIdx,
                HairColorIndex = hairColIdx
            };
        }
    }

    // =========================================================================
    // MAPHACK-STYLED CUSTOMIZATION WINDOW (Zero Extra DLL Dependencies!)
    // =========================================================================
    [HarmonyPatch(typeof(UIChat), "StartInput")]
    internal static class BlockChatWhileCustomizationOpen_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix() => !UltimateCustomizationUI.PanelOpen;
    }

    public sealed class UltimateCustomizationUI : MonoBehaviour
    {
        public static UltimateCustomizationUI Instance { get; private set; }
        public static bool PanelOpen => Instance != null && Instance._isVisible;
        public static Key ToggleKey = Key.F4;

        private bool _isVisible;
        private bool _savedCursorState;
        private CursorLockMode _prevLockState = CursorLockMode.None;
        private bool _prevCursorVisible;
        private bool _savedMouseRequired;
        private bool _prevIsMouseRequired;

        private Texture2D _whiteTex;
        private GUIStyle _titleStyle, _subStyle, _secStyle, _rowLabelStyle, _rowDescStyle, _valStyle, _btnStyle;

        // MapHack Dark Slate & Blue Accent Palette
        private static readonly Color BackdropCol = new Color32(0, 0, 0, 145);
        private static readonly Color PanelBg     = new Color32(20, 20, 24, 248);
        private static readonly Color PanelBorder = new Color32(48, 48, 56, 255);
        private static readonly Color SectionBg   = new Color32(26, 26, 32, 255);
        private static readonly Color RowBg       = new Color32(34, 34, 40, 255);
        private static readonly Color ButtonBg    = new Color32(48, 48, 56, 255);
        private static readonly Color Accent      = new Color32(74, 158, 255, 255);
        private static readonly Color TextPrimary = new Color32(240, 240, 245, 255);
        private static readonly Color TextMuted   = new Color32(155, 155, 165, 255);

        public static void Initialize()
        {
            if (Instance != null || UltimateFrisbeePlugin.IsHeadlessServer) return;
            var go = new GameObject("__UltimateCustomizationUI__");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<UltimateCustomizationUI>();
        }

        public static void Shutdown()
        {
            if (Instance != null)
            {
                Destroy(Instance.gameObject);
                Instance = null;
            }
        }

        private void Awake()
        {
            Instance = this;
            _whiteTex = new Texture2D(1, 1);
            _whiteTex.SetPixel(0, 0, Color.white);
            _whiteTex.Apply();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_isVisible) Hide();
        }

        private void Update()
        {
            UltimateAppearanceManager.TickClientSyncCheck();

            var kb = Keyboard.current;
            if (kb == null || IsChatOpen()) return;

            if (kb[ToggleKey].wasPressedThisFrame)
            {
                if (_isVisible) Hide();
                else Show();
            }
            else if (_isVisible && kb[Key.Escape].wasPressedThisFrame)
            {
                Hide();
            }
        }

        private static bool IsChatOpen()
        {
            try
            {
                var chat = MonoBehaviourSingleton<UIManager>.Instance?.Chat;
                return chat != null && chat.IsFocused;
            }
            catch { return false; }
        }

        public void Show()
        {
            if (_isVisible) return;
            _prevLockState = Cursor.lockState;
            _prevCursorVisible = Cursor.visible;
            _savedCursorState = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            try
            {
                var sb = MonoBehaviourSingleton<UIManager>.Instance?.Scoreboard;
                if (sb != null && sb.IsVisible) sb.Hide();
            }
            catch { }

            try
            {
                _prevIsMouseRequired = GlobalStateManager.UIState.IsMouseRequired;
                _savedMouseRequired = true;
                if (!_prevIsMouseRequired)
                    GlobalStateManager.SetUIState(new Dictionary<string, object> { { "isMouseRequired", true } });
            }
            catch { }

            _isVisible = true;
        }

        public void Hide()
        {
            if (!_isVisible) return;
            _isVisible = false;

            if (_savedMouseRequired)
            {
                try { GlobalStateManager.SetUIState(new Dictionary<string, object> { { "isMouseRequired", _prevIsMouseRequired } }); }
                catch { }
                _savedMouseRequired = false;
            }
            if (_savedCursorState)
            {
                Cursor.lockState = _prevLockState;
                Cursor.visible = _prevCursorVisible;
                _savedCursorState = false;
            }
        }

        private void EnsureStyles()
        {
            if (_titleStyle != null) return;

            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _subStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft };
            _subStyle.normal.textColor = TextMuted;

            _secStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _secStyle.normal.textColor = TextPrimary;

            _rowLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            _rowLabelStyle.normal.textColor = TextPrimary;

            _rowDescStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleLeft, wordWrap = true };
            _rowDescStyle.normal.textColor = TextMuted;

            _valStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _valStyle.normal.textColor = Accent;

            _btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _btnStyle.normal.textColor = TextPrimary;
        }

        private void DrawRect(Rect r, Color c)
        {
            Color prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _whiteTex);
            GUI.color = prev;
        }

        private void DrawBorder(Rect r, Color c, float t = 1f)
        {
            DrawRect(new Rect(r.x, r.y, r.width, t), c);
            DrawRect(new Rect(r.x, r.yMax - t, r.width, t), c);
            DrawRect(new Rect(r.x, r.y, t, r.height), c);
            DrawRect(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        private bool DrawStyledButton(Rect r, string text)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            DrawRect(r, hover ? Color.Lerp(ButtonBg, Accent, 0.35f) : ButtonBg);
            DrawBorder(r, hover ? Accent : PanelBorder, 1f);
            return GUI.Button(r, text, _valStyle);
        }

        private void OnGUI()
        {
            if (!_isVisible || _whiteTex == null) return;
            EnsureStyles();

            // Fullscreen dimmed backdrop
            DrawRect(new Rect(0, 0, Screen.width, Screen.height), BackdropCol);

            float panelW = 680f;
            float panelH = 510f;
            Rect panelRect = new Rect((Screen.width - panelW) * 0.5f, (Screen.height - panelH) * 0.5f, panelW, panelH);

            DrawRect(panelRect, PanelBg);
            DrawBorder(panelRect, PanelBorder, 1.5f);

            float x = panelRect.x + 22f;
            float y = panelRect.y + 18f;
            float innerW = panelW - 44f;

            // Header
            _titleStyle.normal.textColor = TextPrimary;
            GUI.Label(new Rect(x, y, 145f, 34f), "ULTIMATE", _titleStyle);
            _titleStyle.normal.textColor = Accent;
            GUI.Label(new Rect(x + 140f, y, 200f, 34f), "ATHLETE", _titleStyle);
            y += 34f;

            GUI.Label(new Rect(x, y, innerW, 20f), $"Character appearance & gear  ·  Synced across server  ·  {ToggleKey} to toggle  ·  Esc to close", _subStyle);
            y += 28f;

            // Section Card
            float secH = 356f;
            Rect secRect = new Rect(x, y, innerW, secH);
            DrawRect(secRect, SectionBg);
            DrawBorder(secRect, PanelBorder, 1f);

            // Section Title Bar
            DrawRect(new Rect(x + 14f, y + 14f, 4f, 18f), Accent);
            GUI.Label(new Rect(x + 26f, y + 12f, 300f, 22f), "ATHLETE APPEARANCE", _secStyle);
            y += 44f;

            var d = UltimateAppearanceManager.LocalAppearance;

            DrawStepperRow(new Rect(x + 12f, y, innerW - 24f, 54f),
                "Body Build / Gender", "Switches between Masculine V-taper and Feminine contoured frame.",
                UltimateAppearanceManager.GenderNames, null, d.Gender,
                next => { d.Gender = (byte)next; UltimateAppearanceManager.SetAndBroadcastLocalAppearance(d); });
            y += 60f;

            DrawStepperRow(new Rect(x + 12f, y, innerW - 24f, 54f),
                "Hairstyle", "Select hair mesh. Tall styles automatically tuck under closed-top caps.",
                UltimateAppearanceManager.HairNames, null, d.HairStyle,
                next => { d.HairStyle = (byte)next; UltimateAppearanceManager.SetAndBroadcastLocalAppearance(d); });
            y += 60f;

            DrawStepperRow(new Rect(x + 12f, y, innerW - 24f, 54f),
                "Headgear", "Caps, visors, sweatbands, and beanies tinted to your team color.",
                UltimateAppearanceManager.GearNames, null, d.HeadGear,
                next => { d.HeadGear = (byte)next; UltimateAppearanceManager.SetAndBroadcastLocalAppearance(d); });
            y += 60f;

            DrawStepperRow(new Rect(x + 12f, y, innerW - 24f, 54f),
                "Skin Tone", "Updates both your 3rd-person athlete and 1st-person catching arm.",
                UltimateAppearanceManager.SkinToneNames, UltimateAppearanceManager.SkinTonePalette, d.SkinToneIndex,
                next => { d.SkinToneIndex = (byte)next; UltimateAppearanceManager.SetAndBroadcastLocalAppearance(d); });
            y += 60f;

            DrawStepperRow(new Rect(x + 12f, y, innerW - 24f, 54f),
                "Hair Color", "Applies to all hairstyles.",
                UltimateAppearanceManager.HairColorNames, UltimateAppearanceManager.HairColorPalette, d.HairColorIndex,
                next => { d.HairColorIndex = (byte)next; UltimateAppearanceManager.SetAndBroadcastLocalAppearance(d); });

            // Footer Buttons
            float footerY = panelRect.yMax - 48f;
            GUI.Label(new Rect(x, footerY + 6f, 320f, 22f), "Changes save automatically & sync live.", _subStyle);

            if (DrawStyledButton(new Rect(panelRect.xMax - 362f, footerY, 108f, 32f), "RANDOMIZE"))
            {
                UltimateAppearanceManager.SetAndBroadcastLocalAppearance(new PlayerAppearanceData
                {
                    Gender = (byte)UnityEngine.Random.Range(0, UltimateAppearanceManager.GenderNames.Length),
                    HairStyle = (byte)UnityEngine.Random.Range(0, UltimateAppearanceManager.HairNames.Length),
                    HeadGear = (byte)UnityEngine.Random.Range(0, UltimateAppearanceManager.GearNames.Length),
                    SkinToneIndex = (byte)UnityEngine.Random.Range(1, UltimateAppearanceManager.SkinToneNames.Length),
                    HairColorIndex = (byte)UnityEngine.Random.Range(1, UltimateAppearanceManager.HairColorNames.Length)
                });
            }

            if (DrawStyledButton(new Rect(panelRect.xMax - 246f, footerY, 108f, 32f), "RESET"))
            {
                UltimateAppearanceManager.SetAndBroadcastLocalAppearance(PlayerAppearanceData.Default);
            }

            if (DrawStyledButton(new Rect(panelRect.xMax - 130f, footerY, 108f, 32f), "CLOSE"))
            {
                Hide();
            }
        }

        private void DrawStepperRow(
            Rect rowRect,
            string title,
            string desc,
            string[] options,
            Color[] palette,
            int currentIdx,
            Action<int> onChanged)
        {
            DrawRect(rowRect, RowBg);

            GUI.Label(new Rect(rowRect.x + 14f, rowRect.y + 6f, 320f, 20f), title, _rowLabelStyle);
            GUI.Label(new Rect(rowRect.x + 14f, rowRect.y + 26f, 340f, 20f), desc, _rowDescStyle);

            int safeIdx = Mathf.Clamp(currentIdx, 0, options.Length - 1);

            float rightEdge = rowRect.xMax - 12f;
            Rect nextBtn = new Rect(rightEdge - 32f, rowRect.y + 11f, 32f, 32f);
            Rect valRect = new Rect(nextBtn.x - 134f, rowRect.y + 11f, 130f, 32f);
            Rect prevBtn = new Rect(valRect.x - 36f, rowRect.y + 11f, 32f, 32f);

            if (palette != null && safeIdx < palette.Length)
            {
                Rect swatchRect = new Rect(prevBtn.x - 26f, rowRect.y + 18f, 18f, 18f);
                DrawRect(swatchRect, palette[safeIdx]);
                DrawBorder(swatchRect, PanelBorder, 1f);
            }

            if (DrawStyledButton(prevBtn, "<"))
            {
                onChanged((safeIdx - 1 + options.Length) % options.Length);
            }

            GUI.Label(valRect, options[safeIdx].ToUpperInvariant(), _valStyle);

            if (DrawStyledButton(nextBtn, ">"))
            {
                onChanged((safeIdx + 1) % options.Length);
            }
        }
    }
}