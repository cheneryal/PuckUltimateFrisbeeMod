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
    public enum UltimateBodyBuild : byte
    {
        AthleticMasculine = 0,
        AthleticFeminine = 1,
        HeavyDadBod = 2,
        StockyPowerhouse = 3,
        LeanSprinter = 4
    }

    public enum UltimateHairStyle : byte
    {
        None = 0,
        ShortFlow = 1,
        Ponytail = 2,
        LongFlow = 3,
        AfroCrown = 4,
        TopBun = 5,
        Mohawk = 6,
        TwinBraids = 7
    }

    public enum UltimateFacialHair : byte
    {
        None = 0,
        Mustache = 1,
        Goatee = 2,
        FullBeard = 3,
        MuttonChops = 4,
        Chinstrap = 5
    }

    public enum UltimateHeadGear : byte
    {
        None = 0,
        BackwardCap = 1,
        ForwardCap = 2,
        SunVisor = 3,
        Headband = 4,
        Beanie = 5,
        BucketHat = 6
    }

    [Serializable]
    public struct PlayerAppearanceData : IEquatable<PlayerAppearanceData>
    {
        public byte BodyBuild;
        public byte HairStyle;
        public byte FacialHair;
        public byte HeadGear;
        public byte SkinToneIndex;
        public byte HairColorIndex;

        public static PlayerAppearanceData Default => new PlayerAppearanceData
        {
            BodyBuild = (byte)UltimateBodyBuild.AthleticMasculine,
            HairStyle = (byte)UltimateHairStyle.ShortFlow,
            FacialHair = (byte)UltimateFacialHair.None,
            HeadGear = (byte)UltimateHeadGear.BackwardCap,
            SkinToneIndex = 0,
            HairColorIndex = 0
        };

        public bool Equals(PlayerAppearanceData other) =>
            BodyBuild == other.BodyBuild &&
            HairStyle == other.HairStyle &&
            FacialHair == other.FacialHair &&
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

        public static readonly string[] BuildNames = { "Masculine", "Feminine", "Heavy / Dad-Bod", "Stocky / Buff", "Lean Sprinter" };
        public static readonly string[] HairNames  = { "Bald / None", "Short Flow", "Ponytail", "Long Flow", "Afro Crown", "Top Bun", "Mohawk", "Twin Braids" };
        public static readonly string[] BeardNames = { "Clean Shaven", "Mustache", "Goatee", "Full Beard", "Mutton Chops", "Chinstrap" };
        public static readonly string[] GearNames  = { "None", "Backward Cap", "Forward Cap", "Sun Visor", "Sweatband", "Knit Beanie", "Bucket Hat" };

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
            writer.WriteValueSafe(d.BodyBuild);
            writer.WriteValueSafe(d.HairStyle);
            writer.WriteValueSafe(d.FacialHair);
            writer.WriteValueSafe(d.HeadGear);
            writer.WriteValueSafe(d.SkinToneIndex);
            writer.WriteValueSafe(d.HairColorIndex);
        }

        private static PlayerAppearanceData ReadAppearance(FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte build);
            reader.ReadValueSafe(out byte hair);
            reader.ReadValueSafe(out byte beard);
            reader.ReadValueSafe(out byte gear);
            reader.ReadValueSafe(out byte skinIdx);
            reader.ReadValueSafe(out byte hairColIdx);
            return new PlayerAppearanceData
            {
                BodyBuild = build,
                HairStyle = hair,
                FacialHair = beard,
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
        private GUIStyle _titleStyle, _subStyle, _secStyle, _rowLabelStyle, _rowDescStyle, _valStyle, _hintOverlayStyle;

        // --- 3D PREVIEW BOOTH OBJECTS ---
        private GameObject _boothRoot;
        private Camera _previewCam;
        private RenderTexture _previewRT;
        private GameObject _previewAvatar;
        private UltimatePlayerMarker _previewMarker;
        private float _previewYaw = 155f; // Angled slightly so you see face + cap brim
        private float _previewZoom = 0f;  // 0 = Full Body, 1 = Head/Upper Torso Closeup
        private bool _isDraggingPreview = false;
        private PlayerTeam _previewTeam = PlayerTeam.Blue;

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
            DestroyPreviewBooth();
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

            if (_isVisible && _previewAvatar != null)
            {
                // Slowly turntable-rotate when not actively dragging
                if (!_isDraggingPreview)
                {
                    _previewYaw = (_previewYaw + Time.unscaledDeltaTime * 14f) % 360f;
                }
                _previewAvatar.transform.localRotation = Quaternion.Euler(0f, _previewYaw, 0f);

                // Smoothly interpolate camera between Full Body (zoom=0) and Head/Torso Closeup (zoom=1)
                if (_previewCam != null)
                {
                    Vector3 fullBodyCamPos = new Vector3(0f, 0.96f, -3.45f);
                    Vector3 closeupCamPos  = new Vector3(0f, 1.58f, -1.35f);
                    _previewCam.transform.localPosition = Vector3.Lerp(fullBodyCamPos, closeupCamPos, _previewZoom);

                    Vector3 lookTarget = _boothRoot.transform.position + new Vector3(0f, Mathf.Lerp(0.94f, 1.55f, _previewZoom), 0f);
                    _previewCam.transform.LookAt(lookTarget, Vector3.up);
                }
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

        // =====================================================================
        // 3D PREVIEW BOOTH CREATION & POSE SETUP
        // =====================================================================
        private void EnsurePreviewBooth()
        {
            if (UltimateAssetLoader.CustomPlayerPrefab == null) return;

            if (_previewRT == null)
            {
                _previewRT = new RenderTexture(500, 600, 24, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = 4,
                    name = "UltimatePreviewRT"
                };
                _previewRT.Create();
            }

            if (_boothRoot == null)
            {
                _boothRoot = new GameObject("__Ultimate3DPreviewBooth__");
                DontDestroyOnLoad(_boothRoot);
                // Place 500m underground so it never overlaps the stadium or players
                _boothRoot.transform.position = new Vector3(0f, -500f, 0f);

                // Studio Camera
                GameObject camObj = new GameObject("PreviewCamera");
                camObj.transform.SetParent(_boothRoot.transform, false);
                _previewCam = camObj.AddComponent<Camera>();
                _previewCam.clearFlags = CameraClearFlags.SolidColor;
                _previewCam.backgroundColor = new Color(0.08f, 0.09f, 0.12f, 1f);
                _previewCam.fieldOfView = 38f;
                _previewCam.nearClipPlane = 0.1f;
                _previewCam.farClipPlane = 15f;
                _previewCam.targetTexture = _previewRT;
                _previewCam.useOcclusionCulling = false;

                // Studio Key Light
                GameObject keyLightObj = new GameObject("PreviewKeyLight");
                keyLightObj.transform.SetParent(_boothRoot.transform, false);
                keyLightObj.transform.localPosition = new Vector3(1.5f, 2.6f, -2.2f);
                keyLightObj.transform.LookAt(_boothRoot.transform.position + Vector3.up * 1.2f);
                Light keyLight = keyLightObj.AddComponent<Light>();
                keyLight.type = LightType.Point;
                keyLight.range = 12f;
                keyLight.intensity = 2.2f;
                keyLight.color = new Color(1.0f, 0.98f, 0.94f);
                keyLight.shadows = LightShadows.None;

                // Studio Rim/Fill Light
                GameObject fillLightObj = new GameObject("PreviewFillLight");
                fillLightObj.transform.SetParent(_boothRoot.transform, false);
                fillLightObj.transform.localPosition = new Vector3(-1.8f, 1.5f, -1.5f);
                Light fillLight = fillLightObj.AddComponent<Light>();
                fillLight.type = LightType.Point;
                fillLight.range = 10f;
                fillLight.intensity = 1.1f;
                fillLight.color = new Color(0.65f, 0.80f, 1.0f);
                fillLight.shadows = LightShadows.None;

                // Instantiate the Modular Athlete Model
                _previewAvatar = Instantiate(UltimateAssetLoader.CustomPlayerPrefab, _boothRoot.transform);
                _previewAvatar.name = "PreviewAthleteMannequin";
                _previewAvatar.transform.localPosition = Vector3.zero;
                _previewAvatar.transform.localRotation = Quaternion.Euler(0f, _previewYaw, 0f);

                UltimateAssetLoader.SmoothMeshNormals(_previewAvatar, 55f);
                UltimateAssetLoader.ApplyRuntimeMaterials(_previewAvatar, 0.15f);

                foreach (var smr in _previewAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    smr.updateWhenOffscreen = true;
                }

                // Relax the T-Pose arms into a natural athletic standing stance if no Animator is playing
                RelaxTPoseArms(_previewAvatar.transform);

                _previewMarker = _previewAvatar.AddComponent<UltimatePlayerMarker>();
            }

            // Detect local player's current team if spawned
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsClient)
            {
                Player localP = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerByClientId(nm.LocalClientId);
                if (localP != null)
                {
                    PlayerTeam t = UltimateRulesManager.GetTeam(localP);
                    if (t == PlayerTeam.Blue || t == PlayerTeam.Red) _previewTeam = t;
                }
            }

            _boothRoot.SetActive(true);
            RefreshPreviewMannequin();
        }

        private static void RelaxTPoseArms(Transform avatarRoot)
        {
            // Disable Animator if it has no controller or is stuck in T-Pose
            Animator anim = avatarRoot.GetComponent<Animator>();
            if (anim != null && anim.runtimeAnimatorController == null)
            {
                anim.enabled = false;
            }

            // Rotate LeftArm and RightArm down from horizontal T-Pose (~68 degrees) for a clean athletic stance
            Transform leftArm = FindBone(avatarRoot, "mixamorig:LeftArm");
            Transform rightArm = FindBone(avatarRoot, "mixamorig:RightArm");
            Transform leftForeArm = FindBone(avatarRoot, "mixamorig:LeftForeArm");
            Transform rightForeArm = FindBone(avatarRoot, "mixamorig:RightForeArm");

            if (leftArm != null) leftArm.localRotation *= Quaternion.Euler(0f, 0f, -66f);
            if (rightArm != null) rightArm.localRotation *= Quaternion.Euler(0f, 0f, 66f);
            if (leftForeArm != null) leftForeArm.localRotation *= Quaternion.Euler(14f, 0f, -12f);
            if (rightForeArm != null) rightForeArm.localRotation *= Quaternion.Euler(14f, 0f, 12f);
        }

        private static Transform FindBone(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                Transform found = FindBone(child, name);
                if (found != null) return found;
            }
            return null;
        }

        public void RefreshPreviewMannequin()
        {
            if (_previewAvatar == null) return;

            PlayerHead fallbackHead = null;
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsClient)
            {
                Player localP = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerByClientId(nm.LocalClientId);
                fallbackHead = localP?.PlayerBody?.PlayerMesh?.PlayerHead;
            }

            // 1. Apply Gender, Hair, Headgear, Skin Color & Hair Color
            UltimateSkinAndTeamSync.ApplyToTarget(_previewAvatar.transform, UltimateAppearanceManager.LocalAppearance, fallbackHead);

            // 2. Re-apply Team Color (since enabling/disabling new headgear meshes needs team tinting)
            if (_previewMarker != null)
            {
                _previewMarker.LastAppliedTeam = null;
                UltimatePlayerRigManager.ApplyTeamColors(_previewMarker, _previewTeam);
            }
        }

        private void DestroyPreviewBooth()
        {
            if (_boothRoot != null)
            {
                Destroy(_boothRoot);
                _boothRoot = null;
            }
            if (_previewRT != null)
            {
                _previewRT.Release();
                Destroy(_previewRT);
                _previewRT = null;
            }
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

            EnsurePreviewBooth();
            _isVisible = true;
        }

        public void Hide()
        {
            if (!_isVisible) return;
            _isVisible = false;
            _isDraggingPreview = false;

            if (_boothRoot != null) _boothRoot.SetActive(false);

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

            _hintOverlayStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
            _hintOverlayStyle.normal.textColor = new Color(0.85f, 0.88f, 0.92f, 0.70f);
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

        private bool DrawStyledButton(Rect r, string text, Color? overrideBg = null)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            Color baseCol = overrideBg ?? ButtonBg;
            DrawRect(r, hover ? Color.Lerp(baseCol, Accent, 0.35f) : baseCol);
            DrawBorder(r, (hover || overrideBg.HasValue) ? Accent : PanelBorder, 1f);

            Color prevTextCol = _valStyle.normal.textColor;
            _valStyle.normal.textColor = (overrideBg.HasValue || hover) ? TextPrimary : Accent;
            bool clicked = GUI.Button(r, text, _valStyle);
            _valStyle.normal.textColor = prevTextCol;

            return clicked;
        }

        private void OnGUI()
        {
            if (!_isVisible || _whiteTex == null) return;
            EnsureStyles();

            // Fullscreen dimmed backdrop
            DrawRect(new Rect(0, 0, Screen.width, Screen.height), BackdropCol);

            float panelW = 940f;
            float panelH = 580f; // Increased to fit 6 rows
            Rect panelRect = new Rect((Screen.width - panelW) * 0.5f, (Screen.height - panelH) * 0.5f, panelW, panelH);

            DrawRect(panelRect, PanelBg);
            DrawBorder(panelRect, PanelBorder, 1.5f);

            float x = panelRect.x + 22f;
            float y = panelRect.y + 18f;
            float leftColW = 610f;
            float rightColW = 272f;
            float rightColX = x + leftColW + 14f;

            // Header
            _titleStyle.normal.textColor = TextPrimary;
            GUI.Label(new Rect(x, y, 145f, 34f), "ULTIMATE", _titleStyle);
            _titleStyle.normal.textColor = Accent;
            GUI.Label(new Rect(x + 140f, y, 200f, 34f), "ATHLETE", _titleStyle);
            y += 34f;

            GUI.Label(new Rect(x, y, panelW - 44f, 20f), $"Character appearance & gear  ·  Synced across server  ·  {ToggleKey} to toggle  ·  Esc to close", _subStyle);
            y += 28f;

            // =================================================================
            // LEFT COLUMN: CUSTOMIZATION CONTROLS
            // =================================================================
            float secH = 420f; // Increased to fit 6 rows
            Rect secRect = new Rect(x, y, leftColW, secH);
            DrawRect(secRect, SectionBg);
            DrawBorder(secRect, PanelBorder, 1f);

            DrawRect(new Rect(x + 14f, y + 14f, 4f, 18f), Accent);
            GUI.Label(new Rect(x + 26f, y + 12f, 300f, 22f), "ATHLETE APPEARANCE", _secStyle);
            float rowY = y + 44f;

            var d = UltimateAppearanceManager.LocalAppearance;

            DrawStepperRow(new Rect(x + 12f, rowY, leftColW - 24f, 54f),
                "Body Build", "Masculine, Feminine, Heavy / Dad-Bod, Stocky, or Lean Sprinter.",
                UltimateAppearanceManager.BuildNames, null, d.BodyBuild,
                next => { d.BodyBuild = (byte)next; ApplyAndSync(d); });
            rowY += 60f;

            DrawStepperRow(new Rect(x + 12f, rowY, leftColW - 24f, 54f),
                "Hairstyle", "8 styles. Crowns automatically tuck under closed-top caps & beanies.",
                UltimateAppearanceManager.HairNames, null, d.HairStyle,
                next => { d.HairStyle = (byte)next; ApplyAndSync(d); });
            rowY += 60f;

            DrawStepperRow(new Rect(x + 12f, rowY, leftColW - 24f, 54f),
                "Facial Hair", "Clean shaven, mustache, goatee, full lumberjack beard, or chops.",
                UltimateAppearanceManager.BeardNames, null, d.FacialHair,
                next => { d.FacialHair = (byte)next; ApplyAndSync(d); });
            rowY += 60f;

            DrawStepperRow(new Rect(x + 12f, rowY, leftColW - 24f, 54f),
                "Headgear", "Caps, visors, sweatbands, beanies, and bucket hats in team colors.",
                UltimateAppearanceManager.GearNames, null, d.HeadGear,
                next => { d.HeadGear = (byte)next; ApplyAndSync(d); });
            rowY += 60f;

            DrawStepperRow(new Rect(x + 12f, rowY, leftColW - 24f, 54f),
                "Skin Tone", "Updates both your 3rd-person athlete and 1st-person catching arm.",
                UltimateAppearanceManager.SkinToneNames, UltimateAppearanceManager.SkinTonePalette, d.SkinToneIndex,
                next => { d.SkinToneIndex = (byte)next; ApplyAndSync(d); });
            rowY += 60f;

            DrawStepperRow(new Rect(x + 12f, rowY, leftColW - 24f, 54f),
                "Hair & Beard Color", "Applies to both hairstyles and facial hair.",
                UltimateAppearanceManager.HairColorNames, UltimateAppearanceManager.HairColorPalette, d.HairColorIndex,
                next => { d.HairColorIndex = (byte)next; ApplyAndSync(d); });

            // =================================================================
            // RIGHT COLUMN: LIVE 3D PREVIEW BOOTH
            // =================================================================
            Rect previewCard = new Rect(rightColX, y, rightColW, secH);
            DrawRect(previewCard, SectionBg);
            DrawBorder(previewCard, PanelBorder, 1f);

            DrawRect(new Rect(rightColX + 14f, y + 14f, 4f, 18f), Accent);
            GUI.Label(new Rect(rightColX + 26f, y + 12f, 140f, 22f), "3D PREVIEW", _secStyle);

            // Team Kit Preview Toggle (BLUE / RED) in top-right of Preview Card
            Rect blueBtn = new Rect(previewCard.xMax - 114f, y + 10f, 48f, 24f);
            Rect redBtn  = new Rect(previewCard.xMax - 60f,  y + 10f, 48f, 24f);
            if (DrawStyledButton(blueBtn, "BLUE", _previewTeam == PlayerTeam.Blue ? new Color(0.16f, 0.45f, 0.82f) : (Color?)null))
            {
                _previewTeam = PlayerTeam.Blue;
                RefreshPreviewMannequin();
            }
            if (DrawStyledButton(redBtn, "RED", _previewTeam == PlayerTeam.Red ? new Color(0.80f, 0.20f, 0.20f) : (Color?)null))
            {
                _previewTeam = PlayerTeam.Red;
                RefreshPreviewMannequin();
            }

            Rect viewportRect = new Rect(rightColX + 10f, y + 44f, rightColW - 20f, secH - 54f);
            DrawRect(viewportRect, new Color(0.08f, 0.09f, 0.12f, 1f));

            if (_previewRT != null)
            {
                GUI.DrawTexture(viewportRect, _previewRT, ScaleMode.ScaleToFit, false);
            }
            DrawBorder(viewportRect, PanelBorder, 1f);

            // Handle Mouse Drag (Rotate) & Scroll Wheel (Zoom) inside the 3D Viewport
            Event ev = Event.current;
            if (ev != null)
            {
                if (ev.type == EventType.MouseDown && ev.button == 0 && viewportRect.Contains(ev.mousePosition))
                {
                    _isDraggingPreview = true;
                    ev.Use();
                }
                else if (ev.type == EventType.MouseUp && ev.button == 0)
                {
                    _isDraggingPreview = false;
                }
                else if (ev.type == EventType.MouseDrag && _isDraggingPreview)
                {
                    _previewYaw = (_previewYaw - ev.delta.x * 1.15f) % 360f;
                    ev.Use();
                }
                else if (ev.type == EventType.ScrollWheel && viewportRect.Contains(ev.mousePosition))
                {
                    _previewZoom = Mathf.Clamp01(_previewZoom - ev.delta.y * 0.08f);
                    ev.Use();
                }
            }

            GUI.Label(new Rect(viewportRect.x, viewportRect.yMax - 22f, viewportRect.width, 20f),
                "Drag to Rotate  ·  Scroll to Zoom", _hintOverlayStyle);

            // =================================================================
            // FOOTER
            // =================================================================
            float footerY = panelRect.yMax - 48f;
            GUI.Label(new Rect(x, footerY + 6f, 380f, 22f), "Changes save automatically & sync live to all players.", _subStyle);

            if (DrawStyledButton(new Rect(panelRect.xMax - 362f, footerY, 108f, 32f), "RANDOMIZE"))
            {
                ApplyAndSync(new PlayerAppearanceData
                {
                    BodyBuild = (byte)UnityEngine.Random.Range(0, UltimateAppearanceManager.BuildNames.Length),
                    HairStyle = (byte)UnityEngine.Random.Range(0, UltimateAppearanceManager.HairNames.Length),
                    FacialHair = (byte)UnityEngine.Random.Range(0, UltimateAppearanceManager.BeardNames.Length),
                    HeadGear = (byte)UnityEngine.Random.Range(0, UltimateAppearanceManager.GearNames.Length),
                    SkinToneIndex = (byte)UnityEngine.Random.Range(1, UltimateAppearanceManager.SkinToneNames.Length),
                    HairColorIndex = (byte)UnityEngine.Random.Range(1, UltimateAppearanceManager.HairColorNames.Length)
                });
            }

            if (DrawStyledButton(new Rect(panelRect.xMax - 246f, footerY, 108f, 32f), "RESET"))
            {
                ApplyAndSync(PlayerAppearanceData.Default);
            }

            if (DrawStyledButton(new Rect(panelRect.xMax - 130f, footerY, 108f, 32f), "CLOSE"))
            {
                Hide();
            }
        }

        private void ApplyAndSync(PlayerAppearanceData newData)
        {
            UltimateAppearanceManager.SetAndBroadcastLocalAppearance(newData);
            RefreshPreviewMannequin();
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

            GUI.Label(new Rect(rowRect.x + 14f, rowRect.y + 6f, 290f, 20f), title, _rowLabelStyle);
            GUI.Label(new Rect(rowRect.x + 14f, rowRect.y + 26f, 310f, 20f), desc, _rowDescStyle);

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