using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace PuckUltimateFrisbeeMod
{
    public static class NetworkRoles
    {
        public static bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        public static bool IsClient => NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient;
    }

    public class UltimateFrisbeePlugin : IPuckPlugin
    {
        private static bool _isAlreadyEnabled = false;
        private Harmony _harmony;
        public static readonly global::Logger ModLogger = new global::Logger("UltimateFrisbeeMod");
        public static string PluginDirectory;

        public static bool IsHeadlessServer =>
            UnityEngine.SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        public bool OnEnable()
        {
            if (_isAlreadyEnabled)
            {
                ModLogger.Info("UltimateFrisbeeMod is already enabled; skipping duplicate load.");
                return true;
            }

            try
            {
                PluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                UltimateAssetLoader.LoadAssets(PluginDirectory);
                UltimateAudioManager.Initialize(PluginDirectory);

                _harmony = new Harmony("com.puckmod.ultimatefrisbee");
                _harmony.PatchAll(Assembly.GetExecutingAssembly());

                UltimateRulesManager.Initialize();
                UltimateDiveMod.EnsureNetworkRegistered();

                Level existingLevel = UnityEngine.Object.FindFirstObjectByType<Level>();
                if (existingLevel != null)
                {
                    Level_GrassStadiumSetup_Patch.Postfix(existingLevel);
                }

                Level_GrassStadiumSetup_Patch.MoveSpawnPositionsToGoalLines();

                foreach (var sp in UnityEngine.Object.FindObjectsByType<StickPositioner>(FindObjectsSortMode.None))
                {
                    StickAndPositioner_MuteAllStickAudio_Patch.StickPositionerPostSpawnPostfix(sp);
                }

                _isAlreadyEnabled = true;
                ModLogger.Info("Ultimate Frisbee Mod Enabled Successfully!");
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Failed to enable Ultimate Frisbee Mod: {ex}");
                return false;
            }
        }

        public bool OnDisable()
        {
            try
            {
                if (!_isAlreadyEnabled) return true;
                _isAlreadyEnabled = false;
                UltimateRulesManager.Dispose();
                UltimateDiveMod.Dispose();
                UltimateAudioManager.UnregisterNetwork();
                // Do NOT unload the AssetBundle here; unloading destroys runtime shaders on reconnect!
                _harmony?.UnpatchSelf();
                ModLogger.Info("Ultimate Frisbee Mod Disabled!");
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"Failed to disable Ultimate Frisbee Mod: {ex}");
                return false;
            }
        }
    }

    // VISUAL TUNING CONSTANTS
    // =========================================================================
    public static class UltimateVisualTuning
    {
        public static Vector3 ArmRotationOffset = new Vector3(180f, 180f, -0.1f);
        public static Vector3 ArmPositionOffset = new Vector3(0f, 0f, -0.12f);
        public static float ArmThicknessMultiplier = 1.0f;
        public static float ArmLengthMultiplier = 0.58f;

        // Horizontal vs Vertical Reach Tuning:
        // Compresses horizontal stick protrusion while allowing FULL overhead high-catch & Hammer reach!
        public static float ArmHorizontalCompression = 0.68f; // Pulls horizontal poke-check reach closer to body
        public static float ArmMaxHorizontalReach = 1.05f;    // Max horizontal distance from shoulder
        public static float ArmVerticalMultiplier = 1.25f;    // Amplifies vertical reach so you can sky high discs & throw Hammers!

        // Legacy alias so any existing code referencing ArmReachCompression compiles cleanly
        public static float ArmReachCompression
        {
            get => ArmHorizontalCompression;
            set => ArmHorizontalCompression = value;
        }

        public static float ArmScale
        {
            get => ArmThicknessMultiplier;
            set => ArmThicknessMultiplier = value;
        }

        public static Vector3 DiscMeshRotationOffset = new Vector3(-90f, 0f, 0f);
        public static float DiscMeshScale = 2.5f;

        public static float DiscWorldRadius = 0.24f;
        public static float DiscHalfThickness = 0.035f;
        public static float WristRollMultiplier = 1.35f;

        // Hard-coded from your in-game F8 fine-tuning:
        public static Vector3 HeldDiscHandOffset = new Vector3(-0.068f, 0.125f, 0.128f);
        public static Vector3 HeldDiscPitchOffset = new Vector3(-12.9f, 0f, 2.6f);
    }


    // =========================================================================
    // 2. CUSTOM 3D ASSETBUNDLE LOADER
    // =========================================================================
    public static class UltimateAssetLoader
    {
        private static AssetBundle _bundle;
        public static GameObject CustomArmHandPrefab;
        public static GameObject CustomFrisbeePrefab;
        public static GameObject CustomGrassFieldPrefab;
        public static GameObject CustomPlayerPrefab;

        public static void LoadAssets(string folderPath)
        {
            try
            {
                if (string.IsNullOrEmpty(folderPath)) return;
                string bundlePath = Path.Combine(folderPath, "ultimate_assets.bundle");
                if (!File.Exists(bundlePath))
                {
                    UltimateFrisbeePlugin.ModLogger.Info("No ultimate_assets.bundle found at: " + bundlePath);
                    return;
                }

                // If bundle was already loaded in memory, reuse or unload first
                foreach (var loadedBundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (loadedBundle != null && loadedBundle.name.Contains("ultimate"))
                    {
                        _bundle = loadedBundle;
                        break;
                    }
                }

                if (_bundle == null)
                {
                    _bundle = AssetBundle.LoadFromFile(bundlePath);
                }

                if (_bundle == null) return;

                CustomArmHandPrefab = _bundle.LoadAsset<GameObject>("ArmHandPrefab");
                CustomFrisbeePrefab = _bundle.LoadAsset<GameObject>("FrisbeeDiscPrefab");
                CustomGrassFieldPrefab = _bundle.LoadAsset<GameObject>("GrassFieldPrefab");
                CustomPlayerPrefab = _bundle.LoadAsset<GameObject>("UltimatePlayerPrefab")
                  ?? _bundle.LoadAsset<GameObject>("Character")
                  ?? _bundle.LoadAsset<GameObject>("skater");

                UltimateFrisbeePlugin.ModLogger.Info(
                    $"AssetBundle Loaded: Arm={CustomArmHandPrefab != null}, Disc={CustomFrisbeePrefab != null}, " +
                    $"Grass={CustomGrassFieldPrefab != null}, Player={CustomPlayerPrefab != null}");
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"Failed loading ultimate_assets.bundle: {ex}");
            }
        }

        private static Material _cachedTemplateMaterial;

        public static bool NeedsNativeMaterialReplacement(Material mat)
        {
            if (mat == null || mat.shader == null || !mat.shader.isSupported) return true;
            if (mat.shader.name == "Hidden/InternalErrorShader" || mat.shader.name == "Standard") return true;

            // AssetBundle was compiled for Windows DX11/DX12.
            // Any Mac (Metal) or Linux/OpenGL/Vulkan client MUST use a native game material!
            var api = SystemInfo.graphicsDeviceType;
            bool isWindowsDirectX = (api == UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 ||
                                     api == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12);
            return !isWindowsDirectX;
        }

        public static Material GetNativeTemplateMaterial()
        {
            if (_cachedTemplateMaterial != null) return _cachedTemplateMaterial;

            foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (r == null || r.sharedMaterial == null || r.sharedMaterial.shader == null) continue;
                if (r.name.Contains("Ultimate") || r.name.Contains("Custom") || r.name.Contains("GrassTurf")) continue;

                Material candidate = r.sharedMaterial;
                string shName = candidate.shader.name;

                if ((shName.Contains("Universal Render Pipeline/Lit") || shName.Contains("Lit") || shName.Contains("Standard")) &&
                    !shName.Contains("Particles") && !shName.Contains("Sprite") && !shName.Contains("UI"))
                {
                    _cachedTemplateMaterial = new Material(candidate);
                    _cachedTemplateMaterial.name = "Ultimate_NativeTemplateMat";
                    if (_cachedTemplateMaterial.HasProperty("_BaseMap")) _cachedTemplateMaterial.SetTexture("_BaseMap", null);
                    if (_cachedTemplateMaterial.HasProperty("_MainTex")) _cachedTemplateMaterial.SetTexture("_MainTex", null);
                    _cachedTemplateMaterial.mainTexture = null;
                    return _cachedTemplateMaterial;
                }
            }

            Shader fallbackShader = Shader.Find("Universal Render Pipeline/Lit")
                                 ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                                 ?? Shader.Find("Standard");
            if (fallbackShader != null)
            {
                _cachedTemplateMaterial = new Material(fallbackShader);
            }
            return _cachedTemplateMaterial;
        }

        public static Material ConvertBundleMaterialToNative(Material bundleMat, float smoothness, bool isGrass = false)
        {
            if (bundleMat == null) return null;

            // If on Windows DX11/12 and the bundle's shader is already valid, keep the original material untouched!
            if (!NeedsNativeMaterialReplacement(bundleMat))
            {
                if (bundleMat.HasProperty("_Smoothness")) bundleMat.SetFloat("_Smoothness", smoothness);
                if (bundleMat.HasProperty("_Glossiness")) bundleMat.SetFloat("_Glossiness", smoothness);
                if (bundleMat.HasProperty("_Metallic")) bundleMat.SetFloat("_Metallic", 0f);
                return bundleMat;
            }

            Material template = GetNativeTemplateMaterial();
            if (template == null) return bundleMat;

            Color origColor = Color.white;
            if (bundleMat.HasProperty("_BaseColor")) origColor = bundleMat.GetColor("_BaseColor");
            else if (bundleMat.HasProperty("_Color")) origColor = bundleMat.GetColor("_Color");
            else origColor = bundleMat.color;

            Texture origTex = null;
            Vector2 texScale = Vector2.one;
            Vector2 texOffset = Vector2.zero;

            if (bundleMat.HasProperty("_BaseMap") && bundleMat.GetTexture("_BaseMap") != null)
            {
                origTex = bundleMat.GetTexture("_BaseMap");
                texScale = bundleMat.GetTextureScale("_BaseMap");
                texOffset = bundleMat.GetTextureOffset("_BaseMap");
            }
            else if (bundleMat.HasProperty("_MainTex") && bundleMat.GetTexture("_MainTex") != null)
            {
                origTex = bundleMat.GetTexture("_MainTex");
                texScale = bundleMat.GetTextureScale("_MainTex");
                texOffset = bundleMat.GetTextureOffset("_MainTex");
            }
            else if (bundleMat.mainTexture != null)
            {
                origTex = bundleMat.mainTexture;
                texScale = bundleMat.mainTextureScale;
                texOffset = bundleMat.mainTextureOffset;
            }

            // If this is the grass mesh on Mac and it used a custom procedural shader with no texture,
            // give it a natural darker turf green instead of blinding neon green!
            if (isGrass && origTex == null)
            {
                origColor = new Color(0.18f, 0.45f, 0.20f, 1f);
            }

            Material nativeMat = new Material(template)
            {
                name = bundleMat.name
            };

            nativeMat.color = origColor;
            if (nativeMat.HasProperty("_BaseColor")) nativeMat.SetColor("_BaseColor", origColor);
            if (nativeMat.HasProperty("_Color")) nativeMat.SetColor("_Color", origColor);

            if (origTex != null)
            {
                nativeMat.mainTexture = origTex;
                if (nativeMat.HasProperty("_BaseMap"))
                {
                    nativeMat.SetTexture("_BaseMap", origTex);
                    nativeMat.SetTextureScale("_BaseMap", texScale);
                    nativeMat.SetTextureOffset("_BaseMap", texOffset);
                }
                if (nativeMat.HasProperty("_MainTex"))
                {
                    nativeMat.SetTexture("_MainTex", origTex);
                    nativeMat.SetTextureScale("_MainTex", texScale);
                    nativeMat.SetTextureOffset("_MainTex", texOffset);
                }
            }

            if (nativeMat.HasProperty("_Smoothness")) nativeMat.SetFloat("_Smoothness", smoothness);
            if (nativeMat.HasProperty("_Glossiness")) nativeMat.SetFloat("_Glossiness", smoothness);
            if (nativeMat.HasProperty("_Metallic")) nativeMat.SetFloat("_Metallic", 0f);

            return nativeMat;
        }

        public static void ApplyRuntimeMaterials(GameObject instance, float smoothness, bool disableReceiveShadows = false)
        {
            if (instance == null || UltimateFrisbeePlugin.IsHeadlessServer) return;

            foreach (Renderer rend in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (disableReceiveShadows)
                {
                    rend.receiveShadows = false;
                }

                Material[] mats = rend.materials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    mats[i] = ConvertBundleMaterialToNative(mats[i], smoothness, false);
                }
                rend.materials = mats;
            }
        }

        public static void SmoothMeshNormals(GameObject root, float maxWeldAngle = 45f)
        {
            if (root == null) return;
            float cosThreshold = Mathf.Cos(maxWeldAngle * Mathf.Deg2Rad);

            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                Mesh mesh = mf.mesh;
                // First let Unity recalculate clean face/vertex normals
                mesh.RecalculateNormals();

                Vector3[] verts = mesh.vertices;
                Vector3[] normals = mesh.normals;
                if (verts == null || normals == null || verts.Length != normals.Length) continue;

                var posGroups = new Dictionary<Vector3Int, List<int>>();
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3Int key = new Vector3Int(
                        Mathf.RoundToInt(verts[i].x * 10000f),
                        Mathf.RoundToInt(verts[i].y * 10000f),
                        Mathf.RoundToInt(verts[i].z * 10000f));

                    if (!posGroups.TryGetValue(key, out var list))
                    {
                        list = new List<int>(4);
                        posGroups[key] = list;
                    }
                    list.Add(i);
                }

                Vector3[] smoothed = new Vector3[normals.Length];
                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3Int key = new Vector3Int(
                        Mathf.RoundToInt(verts[i].x * 10000f),
                        Mathf.RoundToInt(verts[i].y * 10000f),
                        Mathf.RoundToInt(verts[i].z * 10000f));

                    Vector3 n = normals[i];
                    Vector3 sum = Vector3.zero;
                    foreach (int otherIdx in posGroups[key])
                    {
                        // Only average normals if the angle between faces is < maxWeldAngle
                        if (Vector3.Dot(n, normals[otherIdx]) >= cosThreshold)
                            sum += normals[otherIdx];
                    }
                    smoothed[i] = sum.sqrMagnitude > 0.0001f ? sum.normalized : n;
                }

                mesh.normals = smoothed;
                mesh.RecalculateTangents();
                mf.mesh = mesh;
            }
        }

        public static void UnloadAssets()
        {
            if (_bundle != null)
            {
                _bundle.Unload(false); // Unload(false) so active scene instances don't lose their meshes!
                _bundle = null;
            }
        }
    }

    // =========================================================================
    // 3. NETWORK MESSAGING BRIDGE (Multi-Disc Client Sync for 0-Jitter Lock)
    // =========================================================================
    public static class UltimateNetworkSync
    {
        public const string StateMessageName = "ULTIMATE/State";
        private static CustomMessagingManager _registeredCmm = null;

        public struct DiscSyncEntry
        {
            public ulong HolderClientId;
            public sbyte ThrowStyle;
        }

        // Maps Puck.NetworkObjectId -> Holder & Spin state (supports unlimited Warmup discs!)
        public static readonly Dictionary<ulong, DiscSyncEntry> ClientDiscStates = new Dictionary<ulong, DiscSyncEntry>();

        // Legacy fallback properties for single-disc checks
        public static ulong ClientSyncedHolderId = ulong.MaxValue;
        public static sbyte ClientSyncedThrowStyle = 0;

        public static bool IsPlayerHoldingAnyDisc(ulong clientId)
        {
            if (clientId == ulong.MaxValue) return false;

            // Server-authoritative check against live spawned Pucks first
            if (NetworkRoles.IsServer)
            {
                if (!UltimateRulesManager.IsWarmup)
                {
                    return UltimateRulesManager.Holder != null && UltimateRulesManager.Holder.OwnerClientId == clientId;
                }

                foreach (var kvp in UltimateRulesManager.DiscStates)
                {
                    if (kvp.Key != null && kvp.Key.IsSpawned && kvp.Value.Holder != null && kvp.Value.Holder.OwnerClientId == clientId)
                    {
                        return true;
                    }
                }
                return false;
            }

            foreach (var kvp in ClientDiscStates)
            {
                if (kvp.Value.HolderClientId == clientId) return true;
            }
            return !UltimateRulesManager.IsWarmup && ClientSyncedHolderId == clientId;
        }

        public static void EnsureRegistered()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null) return;
            if (_registeredCmm == nm.CustomMessagingManager) return;

            nm.CustomMessagingManager.UnregisterNamedMessageHandler(StateMessageName);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(StateMessageName, OnStateMessageReceived);
            _registeredCmm = nm.CustomMessagingManager;
        }

        public static void Unregister()
        {
            if (_registeredCmm != null)
            {
                _registeredCmm.UnregisterNamedMessageHandler(StateMessageName);
            }
            _registeredCmm = null;
            ClientSyncedHolderId = ulong.MaxValue;
            ClientDiscStates.Clear();
        }

        public static void ServerBroadcastClearAllHolders()
        {
            ClientSyncedHolderId = ulong.MaxValue;
            ClientSyncedThrowStyle = 0;
            ClientDiscStates.Clear();

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
            if (nm.ConnectedClientsIds == null || nm.ConnectedClientsIds.Count == 0) return;

            // discNetId = ulong.MaxValue signals clients to clear ALL held disc states
            var writer = new FastBufferWriter(24, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(ulong.MaxValue);
                writer.WriteValueSafe(ulong.MaxValue);
                writer.WriteValueSafe((sbyte)0);
                nm.CustomMessagingManager.SendNamedMessageToAll(StateMessageName, writer, NetworkDelivery.Reliable);
            }
            finally
            {
                writer.Dispose();
            }
        }

        public static void ServerBroadcastHolderState(Player holder, sbyte throwStyle, Puck disc = null)
        {
            if (disc == null && holder == null)
            {
                ServerBroadcastClearAllHolders();
                return;
            }

            ulong discNetId = (disc != null && disc.NetworkObject != null) ? disc.NetworkObjectId : 0UL;
            ulong holderId = holder != null ? holder.OwnerClientId : ulong.MaxValue;

            ClientSyncedHolderId = holderId;
            ClientSyncedThrowStyle = throwStyle;
            ClientDiscStates[discNetId] = new DiscSyncEntry { HolderClientId = holderId, ThrowStyle = throwStyle };

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
            if (nm.ConnectedClientsIds == null || nm.ConnectedClientsIds.Count == 0) return;

            var writer = new FastBufferWriter(24, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(discNetId);
                writer.WriteValueSafe(holderId);
                writer.WriteValueSafe(throwStyle);
                nm.CustomMessagingManager.SendNamedMessageToAll(StateMessageName, writer, NetworkDelivery.Reliable);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnStateMessageReceived(ulong senderId, FastBufferReader reader)
        {
            if (NetworkRoles.IsServer) return;
            try
            {
                reader.ReadValueSafe(out ulong discNetId);
                reader.ReadValueSafe(out ulong holderId);
                reader.ReadValueSafe(out sbyte throwStyle);

                if (discNetId == ulong.MaxValue)
                {
                    ClientSyncedHolderId = ulong.MaxValue;
                    ClientSyncedThrowStyle = 0;
                    ClientDiscStates.Clear();
                    return;
                }

                ClientSyncedHolderId = holderId;
                ClientSyncedThrowStyle = throwStyle;
                ClientDiscStates[discNetId] = new DiscSyncEntry { HolderClientId = holderId, ThrowStyle = throwStyle };
            }
            catch { }
        }
    }

    // =========================================================================
    // 4. ULTIMATE FRISBEE RULES, THROW MODULATION & STATE ENGINE
    // =========================================================================
    public enum UltimateThrowType
    {
        Backhand,
        Forehand,
        Hammer
    }

    public static class UltimateRulesManager
    {
        // Regulation Ultimate Field Dimensions (100m long x 37m wide, 18m deep Endzones)
        public static float FieldHalfLength = 50.0f;    // +/- 50m (100m total length)
        public static float EndzoneZThreshold = 32.0f;  // Goal lines at +/- 32m (64m central zone + two 18m endzones)
        public static float FieldHalfWidth = 18.5f;     // +/- 18.5m (37m total width)
        public static float BrickMarkZ = 14.0f;         // Brick Marks are 18m in front of each Goal Line (32m - 18m = +/- 14m)
        public static float MarkerRadius = 4.5f;        // Defender must be within marking range to count Stall!
        public static float DoubleTeamRadius = 3.0f;    // 3m (10ft): Only 1 unguarded defender allowed within 3m of thrower!

        // Momentum Carry-Over ("2-3 Step Grace Period") after catching on the run
        public static float CatchMomentumGraceDuration = 0.65f;
        public static float CatchMomentumDecelRate = 13.5f;
        public static bool IsHolderPivotLocked = false;
        public static Vector3 CatchInitialSpot = Vector3.zero;
        public static bool CaughtInCentralZone = false;

        // Possession & Disc State
        public static Player Holder = null;
        public static Player LastThrower = null;
        public static Player SecondLastThrower = null;
        public static PlayerTeam OffensiveTeam = PlayerTeam.None;
        public static PlayerTeam PullingTeam = PlayerTeam.Blue;
        public static bool IsPullPlay = false;
        public static bool IsCurrentThrowPull = false;
        public static bool IsDiscInFlight = false;
        public static UltimateThrowType ActiveFlightThrowType = UltimateThrowType.Backhand;
        public static bool IsDiscTumblingOnGround = false;
        public static float ActiveFlightSpinSign = 1f; // +1f = CW, -1f = CCW
        public static float FirstGroundTouchTime = 0f;
        public static bool WasTippedInFlight = false;
        public static bool WasDefensiveBlock = false;
        public static Player LastDeflector = null;
        public static float LastDeflectionTime = 0f;
        public static GamePhase LastObservedPhase = GamePhase.None;
        public static PlayerTeam LastTurnoverTeam = PlayerTeam.None;

        // Out-of-Bounds Crossing Tracking
        public static Vector3 LastInBoundsFlightPos = Vector3.zero;
        public static bool WentOutOfBoundsInFlight = false;

        // Timers, Stall Count & Double-Team Tracking
        public static float CatchTime = 0f;
        public static float ReleaseTime = 0f;
        public static float StallCount = 0f;
        public static float StallDuration = 10.0f;
        public static int LastStallAnnounced = 0;
        public static Player ActiveMarker = null;
        public static float LastDoubleTeamWarningTime = 0f;

        // Per-Disc state so multiple discs in Warmup can fly, tumble, and be held independently!
        public class DiscFlightState
        {
            public Player Holder = null;
            public Player LastThrower = null;
            public Player SecondLastThrower = null;
            public bool IsDiscInFlight = false;
            public bool IsDiscTumblingOnGround = false;
            public UltimateThrowType ActiveFlightThrowType = UltimateThrowType.Backhand;
            public float ActiveFlightSpinSign = 1f;
            public float FirstGroundTouchTime = 0f;
            public bool WasTippedInFlight = false;
            public bool WasDefensiveBlock = false;
            public Player LastDeflector = null;
            public float LastDeflectionTime = 0f;
            public float CatchTime = 0f;
            public float ReleaseTime = 0f;
            public Vector3 LastInBoundsFlightPos = Vector3.zero;
            public bool WentOutOfBoundsInFlight = false;
            public bool IsHolderPivotLocked = false;
            public Vector3 CatchInitialSpot = Vector3.zero;
            public bool CaughtInCentralZone = false;
        }

        public static readonly Dictionary<Puck, DiscFlightState> DiscStates = new Dictionary<Puck, DiscFlightState>();

        public static DiscFlightState GetDiscState(Puck disc)
        {
            if (disc == null) return new DiscFlightState();
            if (!DiscStates.TryGetValue(disc, out var state))
            {
                state = new DiscFlightState();
                DiscStates[disc] = state;
            }
            return state;
        }

        // Sprint Stamina Tuning:
        // 1.0f = vanilla drain speed, 0.45f = 55% slower stamina drain (players can sprint >2x longer!)
        public static float SprintStaminaDrainMultiplier = 0.45f;

        /// <summary>
        /// Computes a natural human arm reach from the hockey stick:
        /// - Compresses HORIZONTAL protrusion so the arm stays close to the torso.
        /// - Preserves & boosts VERTICAL reach when aiming up so players can sky high discs and throw Hammers!
        /// </summary>
        public static Vector3 GetHandWorldPosition(Stick stick, out Vector3 shoulderAnchor, out Vector3 shaftDir)
        {
            if (stick == null)
            {
                shoulderAnchor = Vector3.zero;
                shaftDir = Vector3.forward;
                return Vector3.zero;
            }

            Player ownerPlayer = stick.Player;
            PlayerBody body = stick.PlayerBody != null ? stick.PlayerBody : (ownerPlayer != null ? ownerPlayer.PlayerBody : null);
            bool isLeft = ownerPlayer != null && ownerPlayer.Handedness != null && ownerPlayer.Handedness.Value == PlayerHandedness.Left;

            Vector3 rawBladeTip = stick.BladeHandlePosition;
            Vector3 rawShaftStart = stick.ShaftHandlePosition;

            if (body != null)
            {
                // Anchor shoulder higher on the upper torso (y = 0.52m local)
                Vector3 shoulderWorld = body.transform.TransformPoint(new Vector3(isLeft ? -0.28f : 0.28f, 0.52f, 0.06f));
                shoulderAnchor = Vector3.Lerp(rawShaftStart, shoulderWorld, 0.65f);
            }
            else
            {
                shoulderAnchor = rawShaftStart;
            }

            Vector3 rawDelta = rawBladeTip - shoulderAnchor;

            // 1. Compress ONLY the horizontal (X/Z) extension so the arm doesn't poke out 1.6m horizontally
            Vector3 horizDelta = new Vector3(rawDelta.x, 0f, rawDelta.z);
            float horizDist = horizDelta.magnitude;
            if (horizDist > 0.001f)
            {
                float compressedHoriz = Mathf.Min(
                    horizDist * UltimateVisualTuning.ArmHorizontalCompression,
                    UltimateVisualTuning.ArmMaxHorizontalReach
                );
                horizDelta = (horizDelta / horizDist) * compressedHoriz;
            }

            // 2. Amplify vertical reach so raising the stick reaches high above the head
            float vertDelta = rawDelta.y * UltimateVisualTuning.ArmVerticalMultiplier;

            // 3. When aiming upward (Hammer / High Catch), the stick shaft also pitches up:
            //    Use the shaft's upward angle to lift the hand into an overhead position!
            Vector3 rawShaftVec = rawBladeTip - rawShaftStart;
            if (rawShaftVec.sqrMagnitude > 0.01f)
            {
                Vector3 rawShaftDir = rawShaftVec.normalized;
                if (rawShaftDir.y > 0.05f)
                {
                    // Add extra overhead extension when aiming up
                    vertDelta += rawShaftDir.y * 0.45f * UltimateVisualTuning.ArmVerticalMultiplier;
                }
            }

            //Also check PlayerInput pitch if available (negative X = looking up in Puck)
            if (ownerPlayer != null && ownerPlayer.PlayerInput != null)
            {
                float aimPitchX = NetworkRoles.IsServer
                    ? ownerPlayer.PlayerInput.StickRaycastOriginAngleInput.ServerValue.x
                    : ownerPlayer.PlayerInput.StickRaycastOriginAngleInput.ClientValue.x;

                if (aimPitchX < 0f)
                {
                    float overheadFactor = Mathf.Clamp01(-aimPitchX / 30f); // 0..1 as player looks up
                    vertDelta += overheadFactor * 0.55f * UltimateVisualTuning.ArmVerticalMultiplier;
                    // Pull hand slightly closer horizontally when reaching straight overhead (like a real Hammer/sky reach)
                    horizDelta *= Mathf.Lerp(1.0f, 0.78f, overheadFactor);
                }
            }

            Vector3 finalDelta = new Vector3(horizDelta.x, vertDelta, horizDelta.z);
            if (finalDelta.sqrMagnitude < 0.005f)
            {
                shaftDir = body != null ? body.transform.forward : stick.transform.forward;
                return shoulderAnchor + shaftDir * 0.55f;
            }

            shaftDir = finalDelta.normalized;
            return shoulderAnchor + finalDelta;
        }

        public static Vector3 GetHandWorldPosition(Stick stick)
        {
            return GetHandWorldPosition(stick, out _, out _);
        }

        public static Vector3 LastDiscFramePos = Vector3.zero;
        public static PlayerTeam GetTeam(Player player)
        {
            if (player == null) return PlayerTeam.None;
            return player.Team;
        }
        public static bool AreOpponents(Player a, Player b)
        {
            PlayerTeam teamA = GetTeam(a);
            PlayerTeam teamB = GetTeam(b);
            return (teamA == PlayerTeam.Blue && teamB == PlayerTeam.Red) ||
                   (teamA == PlayerTeam.Red && teamB == PlayerTeam.Blue);
        }

        // Modulated Wind-up & Throw Tracking per Player
        public static Dictionary<ulong, ThrowWindupTracker> WindupTrackers = new Dictionary<ulong, ThrowWindupTracker>();

        public static object ActiveGameModeInstance = null;
        private static MethodInfo _scoreGoalMethod;

        public class ThrowWindupTracker
        {
            public bool IsCharging = false;
            public bool RequireFreshShiftPress = false; // Prevents sprinting into a catch from charging/throwing!
            public float ChargeStartTime = 0f;
            public float StaminaUsed = 0f;
            public int LastUpdatedTick = -1;

            // --- MOUSE SWING & ACCELERATION TRACKING ---
            public Vector3 PrevLocalHandPos = Vector3.zero;
            public Vector3 PrevLocalHandVel = Vector3.zero;
            public Vector2 PrevStickAngle = Vector2.zero;
            public float PrevAngularSpeed = 0f;

            public float PeakForwardSwingSpeed = 0f;
            public float PeakForwardSwingAccel = 0f;
            public float PeakAngularWhipSpeed = 0f;
            public float SmoothedSwingPowerBonus = 0f;

            public const float FullChargeSeconds = 2.20f;

            public void OnCatchOrPickup(Player holder)
            {
                Reset(holder?.PlayerBody);
                // If the player is currently holding Shift (sprinting) when they catch/pickup,
                // ignore Shift until they release it first!
                bool holdingShiftOnCatch = holder?.PlayerInput != null && holder.PlayerInput.SprintInput.ServerValue;
                RequireFreshShiftPress = holdingShiftOnCatch;
            }

            public void CancelCharge(PlayerBody body)
            {
                Reset(body);
                // Require the player to release Shift before they can start a new charge
                RequireFreshShiftPress = true;
            }

            public void BeginCharge(PlayerBody body, Stick stick, Vector2 stickAngle)
            {
                IsCharging = true;
                ChargeStartTime = Time.time;
                StaminaUsed = 0f;
                LastUpdatedTick = Time.frameCount;

                if (body != null && stick != null)
                {
                    PrevLocalHandPos = body.transform.InverseTransformPoint(GetHandWorldPosition(stick));
                }
                PrevLocalHandVel = Vector3.zero;
                PrevStickAngle = stickAngle;
                PrevAngularSpeed = 0f;

                PeakForwardSwingSpeed = 0f;
                PeakForwardSwingAccel = 0f;
                PeakAngularWhipSpeed = 0f;
                SmoothedSwingPowerBonus = 0f;

                if (body != null)
                {
                    body.IsSprinting.Value = false;
                    if (body.Stamina != null) body.Stamina.Value = 1.0f;
                }
            }

            public void UpdateCharge(PlayerBody body, Stick stick, Vector2 currentStickAngle, float dt)
            {
                dt = Mathf.Max(0.001f, dt);
                float elapsed = Time.time - ChargeStartTime;
                StaminaUsed = Mathf.Clamp01(elapsed / FullChargeSeconds);

                if (body != null && stick != null)
                {
                    Vector3 currentLocalHand = body.transform.InverseTransformPoint(GetHandWorldPosition(stick));
                    Vector3 handVel = (currentLocalHand - PrevLocalHandPos) / dt;
                    Vector3 handAccel = (handVel - PrevLocalHandVel) / dt;

                    float forwardSpeed = Mathf.Max(0f, handVel.z) + Mathf.Abs(handVel.x) * 0.65f;
                    float forwardAccel = Mathf.Max(0f, handAccel.z) + Mathf.Abs(handAccel.x) * 0.5f;

                    Vector2 angleDelta = currentStickAngle - PrevStickAngle;
                    float angularSpeed = angleDelta.magnitude / dt;
                    float angularAccel = Mathf.Max(0f, (angularSpeed - PrevAngularSpeed) / dt);

                    float decay = Mathf.Exp(-dt * 4.5f);
                    PeakForwardSwingSpeed = Mathf.Max(forwardSpeed, PeakForwardSwingSpeed * decay);
                    PeakForwardSwingAccel = Mathf.Max(forwardAccel, PeakForwardSwingAccel * decay);
                    PeakAngularWhipSpeed = Mathf.Max(angularSpeed, PeakAngularWhipSpeed * decay);

                    float speedNorm = Mathf.Clamp01(PeakForwardSwingSpeed / 1.6f);
                    float accelNorm = Mathf.Clamp01(PeakForwardSwingAccel / 20.0f);
                    float angularNorm = Mathf.Clamp01((PeakAngularWhipSpeed / 220.0f) + (angularAccel / 1800.0f) * 0.5f);

                    SmoothedSwingPowerBonus = Mathf.Clamp01(
                        Mathf.Max(speedNorm * 0.55f + accelNorm * 0.45f, angularNorm * 0.85f)
                    );

                    PrevLocalHandPos = currentLocalHand;
                    PrevLocalHandVel = handVel;
                    PrevStickAngle = currentStickAngle;
                    PrevAngularSpeed = angularSpeed;
                }

                if (body != null)
                {
                    body.IsSprinting.Value = false;
                    if (body.Stamina != null)
                    {
                        float visualPower = GetCombinedThrowPower();
                        body.Stamina.Value = Mathf.Clamp01(1.0f - visualPower);
                    }
                }
            }

            public float GetCombinedThrowPower()
            {
                float baseCharge = StaminaUsed * 0.65f;
                float swingContribution = SmoothedSwingPowerBonus * 0.45f;
                return Mathf.Clamp01(baseCharge + swingContribution);
            }

            public void Reset(PlayerBody body = null)
            {
                IsCharging = false;
                StaminaUsed = 0f;
                PeakForwardSwingSpeed = 0f;
                PeakForwardSwingAccel = 0f;
                PeakAngularWhipSpeed = 0f;
                SmoothedSwingPowerBonus = 0f;
                if (body != null && body.Stamina != null)
                {
                    body.Stamina.Value = 1.0f;
                }
            }
        }

        public static bool IsWarmup =>
            NetworkBehaviourSingleton<GameManager>.Instance == null ||
            NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.Play;

        public static void Initialize()
        {
            ResetPlayState();
        }

        public static void Dispose()
        {
            ResetPlayState();
            UltimateNetworkSync.Unregister();
            WindupTrackers.Clear();
        }

        public static void ResetPlayState()
        {
            if (Holder != null && Holder.PlayerBody != null && NetworkRoles.IsServer)
            {
                Holder.PlayerBody.Server_Unfreeze();
            }
            Holder = null;
            IsHolderPivotLocked = false;
            LastThrower = null;
            SecondLastThrower = null;
            IsDiscInFlight = false;
            IsDiscTumblingOnGround = false;
            IsCurrentThrowPull = false;
            StallCount = 0f;
            LastStallAnnounced = 0;
            ActiveMarker = null;
            LastDoubleTeamWarningTime = 0f;
            WasTippedInFlight = false;
            WasDefensiveBlock = false;
            LastDeflector = null;
            LastDeflectionTime = 0f;
            LastTurnoverTeam = PlayerTeam.None;
            WentOutOfBoundsInFlight = false;
            UltimateNetworkSync.ServerBroadcastHolderState(null, 0);
            foreach (var kvp in DiscStates)
            {
                if (kvp.Value.Holder != null && kvp.Value.Holder.PlayerBody != null && NetworkRoles.IsServer)
                {
                    kvp.Value.Holder.PlayerBody.Server_Unfreeze();
                }
            }
            DiscStates.Clear();
        }

        public static bool IsPositionOutOfBounds(Vector3 pos)
        {
            return Mathf.Abs(pos.x) >= FieldHalfWidth || Mathf.Abs(pos.z) >= FieldHalfLength;
        }

        public static UltimateThrowType DetermineThrowType(Player holder, out float spinSign)
        {
            spinSign = 1f;
            if (holder == null || holder.PlayerBody == null || holder.Stick == null)
                return UltimateThrowType.Backhand;

            bool isLeftHanded = holder.Handedness != null && holder.Handedness.Value == PlayerHandedness.Left;

            Vector3 localHand = holder.PlayerBody.transform.InverseTransformPoint(GetHandWorldPosition(holder.Stick));
            float wristRoll = holder.Stick.BladeAngle;

            bool isForehandSide;
            if (!isLeftHanded)
            {
                isForehandSide = (localHand.x >= 0.05f) || (localHand.x > -0.05f && wristRoll < -6f);
                spinSign = isForehandSide ? -1f : 1f;
            }
            else
            {
                isForehandSide = (localHand.x <= -0.05f) || (localHand.x < 0.05f && wristRoll > 6f);
                spinSign = isForehandSide ? 1f : -1f;
            }

            if (holder.PlayerInput != null)
            {
                Vector2 stickAngle = holder.PlayerInput.StickRaycastOriginAngleInput.ServerValue;
                if (stickAngle.x < -12f)
                {
                    return UltimateThrowType.Hammer;
                }
            }

            return isForehandSide ? UltimateThrowType.Forehand : UltimateThrowType.Backhand;
        }

        public static bool IsInAttackingEndzone(Player player)
        {
            if (player == null || player.PlayerBody == null) return false;
            Vector3 pos = player.PlayerBody.transform.position;

            if (IsPositionOutOfBounds(pos))
                return false;

            PlayerTeam? endzoneTeam = GetEndzoneTeamAtPosition(pos);
            if (endzoneTeam == null) return false;

            PlayerTeam playerTeam = GetTeam(player);
            return endzoneTeam.Value != playerTeam && (endzoneTeam.Value == PlayerTeam.Blue || endzoneTeam.Value == PlayerTeam.Red);
        }

        public static bool IsInDefendingEndzone(Player player)
        {
            if (player == null || player.PlayerBody == null) return false;
            Vector3 pos = player.PlayerBody.transform.position;

            if (IsPositionOutOfBounds(pos))
                return false;

            PlayerTeam? endzoneTeam = GetEndzoneTeamAtPosition(pos);
            return endzoneTeam != null && endzoneTeam.Value == GetTeam(player);
        }

        public static PlayerTeam? GetEndzoneTeamAtPosition(Vector3 pos)
        {
            if (pos.z >= EndzoneZThreshold) return PlayerTeam.Blue;
            if (pos.z <= -EndzoneZThreshold) return PlayerTeam.Red;
            return null;
        }

        public static void BroadcastAnnouncement(string message, string color = "#ffe97f")
        {
            if (NetworkRoles.IsServer && NetworkBehaviourSingleton<ChatManager>.Instance != null)
            {
                NetworkBehaviourSingleton<ChatManager>.Instance.Server_BroadcastChatMessage($"[ULTIMATE] {message}", color);
            }
        }

        public static void TriggerGoal(PlayerTeam scoringTeam, Player receiver, Player thrower, Player secondAssist, Puck disc)
        {
            if (ActiveGameModeInstance == null) return;
            if (_scoreGoalMethod == null)
            {
                _scoreGoalMethod = ActiveGameModeInstance.GetType().GetMethod("ScoreGoal", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }

            PullingTeam = scoringTeam;
            ResetPlayState();
            _scoreGoalMethod?.Invoke(ActiveGameModeInstance, new object[] { scoringTeam, receiver, thrower, secondAssist, disc });
        }
    }

    // =========================================================================
    // 5. RUNNING / TURF MOVEMENT PATCHES
    // =========================================================================
    [HarmonyPatch(typeof(Movement), "Awake")]
    public static class Movement_TurfPhysics_Patch
    {
        private static readonly FieldInfo _fAccel = AccessTools.Field(typeof(Movement), "forwardsAcceleration");
        private static readonly FieldInfo _fSprintAccel = AccessTools.Field(typeof(Movement), "forwardsSprintAcceleration");
        private static readonly FieldInfo _bAccel = AccessTools.Field(typeof(Movement), "backwardsAcceleration");
        private static readonly FieldInfo _bSprintAccel = AccessTools.Field(typeof(Movement), "backwardsSprintAcceleration");
        private static readonly FieldInfo _brakeAccel = AccessTools.Field(typeof(Movement), "brakeAcceleration");
        private static readonly FieldInfo _drag = AccessTools.Field(typeof(Movement), "drag");
        private static readonly FieldInfo _overspeedDrag = AccessTools.Field(typeof(Movement), "overspeedDrag");
        private static readonly FieldInfo _turnAccel = AccessTools.Field(typeof(Movement), "turnAcceleration");
        private static readonly FieldInfo _turnBrake = AccessTools.Field(typeof(Movement), "turnBrakeAcceleration");
        private static readonly FieldInfo _turnMax = AccessTools.Field(typeof(Movement), "turnMaxSpeed");

        [HarmonyPostfix]
        public static void Postfix(Movement __instance)
        {
            _fAccel?.SetValue(__instance, 14.0f);
            _fSprintAccel?.SetValue(__instance, 20.0f);
            _bAccel?.SetValue(__instance, 10.0f);
            _bSprintAccel?.SetValue(__instance, 14.0f);
            _brakeAccel?.SetValue(__instance, 28.0f);
            _drag?.SetValue(__instance, 2.5f);
            _overspeedDrag?.SetValue(__instance, 3.0f);
            _turnAccel?.SetValue(__instance, 5.5f);
            _turnBrake?.SetValue(__instance, 10.0f);
            _turnMax?.SetValue(__instance, 3.2f);
        }
    }

    [HarmonyPatch(typeof(Skate), "Awake")]
    public static class Skate_TurfTraction_Patch
    {
        private static readonly FieldInfo _traction = AccessTools.Field(typeof(Skate), "traction");

        [HarmonyPostfix]
        public static void Postfix(Skate __instance)
        {
            _traction?.SetValue(__instance, 2.5f);
        }
    }

    // =========================================================================
    // 6. FULL-SHAFT 3D ARM & HAND CONTROLLER (Stick)
    // =========================================================================
    public class CustomArmController : MonoBehaviour
    {
        public Stick Stick;
        public Transform CustomArmInstance;
        public Transform ProceduralArmCylinder;
        public Transform ProceduralHandPalm;

        // Measured automatically from your CustomArmHandPrefab's Renderer bounds!
        public float NativeModelLength = 1.0f;
        public int LongestLocalAxis = 2; // 0 = X, 1 = Y, 2 = Z
        public Vector3 NativeModelCenterOffset = Vector3.zero;

        public void CalibrateCustomArmBounds()
        {
            if (CustomArmInstance == null) return;

            Quaternion savedRot = CustomArmInstance.rotation;
            Vector3 savedPos = CustomArmInstance.position;
            Vector3 savedScale = CustomArmInstance.localScale;

            CustomArmInstance.position = Vector3.zero;
            CustomArmInstance.rotation = Quaternion.identity;
            CustomArmInstance.localScale = Vector3.one;

            Bounds combined = new Bounds(Vector3.zero, Vector3.zero);
            bool hasBounds = false;
            foreach (var r in CustomArmInstance.GetComponentsInChildren<Renderer>(true))
            {
                if (!hasBounds) { combined = r.bounds; hasBounds = true; }
                else combined.Encapsulate(r.bounds);
            }

            if (hasBounds)
            {
                Vector3 size = combined.size;
                if (size.x >= size.y && size.x >= size.z) { LongestLocalAxis = 0; NativeModelLength = Mathf.Max(0.1f, size.x); }
                else if (size.y >= size.x && size.y >= size.z) { LongestLocalAxis = 1; NativeModelLength = Mathf.Max(0.1f, size.y); }
                else { LongestLocalAxis = 2; NativeModelLength = Mathf.Max(0.1f, size.z); }

                NativeModelCenterOffset = combined.center;
            }

            CustomArmInstance.position = savedPos;
            CustomArmInstance.rotation = savedRot;
            CustomArmInstance.localScale = savedScale;
        }

        private PlayerTeam _appliedBandTeam = PlayerTeam.None;

        public void UpdateWristbandTeamColor(PlayerTeam team)
        {
            if (CustomArmInstance == null || team == _appliedBandTeam) return;
            if (team != PlayerTeam.Blue && team != PlayerTeam.Red) return;

            _appliedBandTeam = team;

            // Vibrant Blue or Red wristband color matching the team
            Color bandColor = (team == PlayerTeam.Blue)
                ? new Color(0.18f, 0.52f, 0.92f, 1f)   // Team Blue
                : new Color(0.90f, 0.22f, 0.20f, 1f);  // Team Red

            foreach (Renderer rend in CustomArmInstance.GetComponentsInChildren<Renderer>(true))
            {
                bool isBandObject = rend.gameObject.name.IndexOf("Band", StringComparison.OrdinalIgnoreCase) >= 0;
                Material[] mats = rend.materials;
                bool modified = false;

                for (int i = 0; i < mats.Length; i++)
                {
                    Material mat = mats[i];
                    if (mat == null) continue;

                    // Match either the material name ("Stylized_Band_Mat") or the "Band" child mesh
                    if (isBandObject || mat.name.IndexOf("Stylized_Band_Mat", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", bandColor);
                        if (mat.HasProperty("_Color")) mat.SetColor("_Color", bandColor);
                        mat.color = bandColor;
                        modified = true;
                    }
                }

                if (modified)
                {
                    rend.materials = mats;
                }
            }
        }

        private void LateUpdate()
        {
            if (Stick == null) return;

            if (Stick.StickMesh != null)
            {
                foreach (var r in Stick.StickMesh.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.enabled) r.enabled = false;
                }
            }

            Player ownerPlayer = Stick.Player;
            if (ownerPlayer != null)
            {
                UpdateWristbandTeamColor(UltimateRulesManager.GetTeam(ownerPlayer));
            }

            // If the full-body Ultimate Athlete rig is active on this player,
            // ONLY show this floating 1st-person arm for the LocalPlayer!
            // (Remote players use their rigged body's IK arm instead so they don't have 3 arms!)
            bool hasFullBodyRig = UltimateAssetLoader.CustomPlayerPrefab != null &&
                                  ownerPlayer != null &&
                                  ownerPlayer.PlayerBody != null &&
                                  ownerPlayer.PlayerBody.GetComponentInChildren<UltimatePlayerMarker>(true) != null;

            bool showFirstPersonArm = !hasFullBodyRig ||
                UltimatePlayerIKBridge.IsLocalPlayerInFirstPerson(ownerPlayer, ownerPlayer.PlayerBody);

            if (CustomArmInstance != null && CustomArmInstance.gameObject.activeSelf != showFirstPersonArm)
            {
                CustomArmInstance.gameObject.SetActive(showFirstPersonArm);
            }
            if (!showFirstPersonArm) return;

            bool isLeft = ownerPlayer != null && ownerPlayer.Handedness != null && ownerPlayer.Handedness.Value == PlayerHandedness.Left;

            Vector3 bladeTip = UltimateRulesManager.GetHandWorldPosition(Stick, out Vector3 shaftStart, out Vector3 shaftDir);
            float armSpanDistance = Mathf.Clamp(Vector3.Distance(bladeTip, shaftStart), 0.45f, 1.35f);

            Quaternion lookRot = Quaternion.LookRotation(shaftDir, Vector3.up);
            float wristRoll = Stick.BladeAngle * UltimateVisualTuning.WristRollMultiplier;
            Quaternion aimRot = lookRot * Quaternion.Euler(0f, 0f, wristRoll);

            if (CustomArmInstance != null)
            {
                Quaternion finalRot = aimRot * Quaternion.Euler(UltimateVisualTuning.ArmRotationOffset);

                // Scale the arm length dynamically with the actual shoulder-to-hand span so it never looks like a floating stub!
                float targetWorldLen = armSpanDistance * UltimateVisualTuning.ArmLengthMultiplier * 1.45f;
                float lengthScale = targetWorldLen / Mathf.Max(0.1f, NativeModelLength);
                float widthScale = (1.15f / Mathf.Max(0.1f, NativeModelLength)) * 0.55f * UltimateVisualTuning.ArmThicknessMultiplier;

                Vector3 scaleVec = new Vector3(widthScale, widthScale, widthScale);
                if (LongestLocalAxis == 0) scaleVec.x = lengthScale;
                else if (LongestLocalAxis == 1) scaleVec.y = lengthScale;
                else scaleVec.z = lengthScale;

                if (isLeft) scaleVec.x = -scaleVec.x;

                CustomArmInstance.localScale = scaleVec;
                CustomArmInstance.rotation = finalRot;

                Vector3 worldCenterOffset = finalRot * Vector3.Scale(NativeModelCenterOffset, scaleVec);

                // Position the arm so the hand is at bladeTip and the forearm stretches back to the shoulder
                Vector3 desiredCenterPos = bladeTip - (shaftDir * (targetWorldLen * 0.35f)) + (aimRot * UltimateVisualTuning.ArmPositionOffset);
                CustomArmInstance.position = desiredCenterPos - worldCenterOffset;
            }
            else if (ProceduralArmCylinder != null && ProceduralHandPalm != null)
            {
                ProceduralArmCylinder.position = Vector3.Lerp(shaftStart, bladeTip, 0.5f);
                ProceduralArmCylinder.rotation = aimRot * Quaternion.Euler(90f, 0f, 0f);
                ProceduralArmCylinder.localScale = new Vector3(0.11f, armSpanDistance * 0.45f, 0.11f);

                ProceduralHandPalm.position = bladeTip;
                ProceduralHandPalm.rotation = aimRot;
                ProceduralHandPalm.localScale = new Vector3(0.22f, 0.08f, 0.26f);
            }
        }
    }

    [HarmonyPatch(typeof(Stick), "OnNetworkPostSpawn")]
    public static class Stick_ArmAndHandVisuals_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Stick __instance)
        {
            try
            {
                if (__instance.StickMesh != null)
                {
                    foreach (var renderer in __instance.StickMesh.GetComponentsInChildren<Renderer>(true))
                    {
                        renderer.enabled = false;
                    }
                }

                foreach (var col in __instance.GetComponentsInChildren<Collider>(true))
                {
                    col.enabled = false;
                }

                if (__instance.transform.Find("UltimateArmRig") == null)
                {
                    GameObject rigRoot = new GameObject("UltimateArmRig");
                    rigRoot.transform.SetParent(__instance.transform, false);
                    rigRoot.transform.localPosition = Vector3.zero;
                    rigRoot.transform.localRotation = Quaternion.identity;
                    rigRoot.transform.localScale = Vector3.one;

                    var controller = rigRoot.AddComponent<CustomArmController>();
                    controller.Stick = __instance;

                    if (UltimateAssetLoader.CustomArmHandPrefab != null)
                    {
                        GameObject customArm = UnityEngine.Object.Instantiate(UltimateAssetLoader.CustomArmHandPrefab, rigRoot.transform);
                        customArm.name = "CustomArmInstance";
                        foreach (var col in customArm.GetComponentsInChildren<Collider>(true))
                            UnityEngine.Object.Destroy(col);

                        UltimateAssetLoader.SmoothMeshNormals(customArm);
                        UltimateAssetLoader.ApplyRuntimeMaterials(customArm, 0.15f, true);

                        controller.CustomArmInstance = customArm.transform;
                        controller.CalibrateCustomArmBounds();
                    }
                    else
                    {
                        GameObject armObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        armObj.name = "UltimateForearm";
                        UnityEngine.Object.Destroy(armObj.GetComponent<Collider>());
                        armObj.transform.SetParent(rigRoot.transform, true);

                        GameObject handObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        handObj.name = "UltimateHandPalm";
                        UnityEngine.Object.Destroy(handObj.GetComponent<Collider>());
                        handObj.transform.SetParent(rigRoot.transform, false);

                        Shader std = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
                        var armMat = new Material(std) { color = new Color(0.2f, 0.22f, 0.25f) };
                        var handMat = new Material(std) { color = new Color(0.95f, 0.8f, 0.68f) };

                        armObj.GetComponent<MeshRenderer>().material = armMat;
                        handObj.GetComponent<MeshRenderer>().material = handMat;

                        controller.ProceduralArmCylinder = armObj.transform;
                        controller.ProceduralHandPalm = handObj.transform;
                    }
                }
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"Error setting up Arm/Hand visuals: {ex}");
            }
        }
    }

    // =========================================================================
    // 7. ZERO-JITTER HELD DISC CLIENT LOCK & SELF-HEALING DISC VISUALS (Puck)
    // =========================================================================
    public class ClientDiscFollower : MonoBehaviour
    {
        public Puck Disc;
        public Transform CustomDiscVisual;
        public PuckElevationIndicator ElevationIndicator;
        private float _visualSpinAngle = 0f;
        private Vector3 _lastFrameWorldPos = Vector3.zero;
        private float _smoothedWorldSpeed = 0f;
        public static float GetMinimumCenterHeightForRotation(Quaternion rot)
        {
            Vector3 discNormal = rot * Vector3.up;
            float cosTilt = Mathf.Clamp(Mathf.Abs(discNormal.y), 0f, 1f);
            float sinTilt = Mathf.Sqrt(Mathf.Max(0f, 1f - cosTilt * cosTilt));
            return (UltimateVisualTuning.DiscWorldRadius * sinTilt) + (UltimateVisualTuning.DiscHalfThickness * cosTilt) + 0.01f;
        }
        public void EnsureCustomDiscVisual()
        {
            if (Disc == null) return;

            // If CustomFrisbeePrefab is loaded, ensure it is instantiated on this Puck and vanilla puck mesh is hidden!
            if (UltimateAssetLoader.CustomFrisbeePrefab != null)
            {
                if (CustomDiscVisual == null)
                {
                    Transform existing = Disc.transform.Find("CustomFrisbeeMesh");
                    if (existing != null)
                    {
                        CustomDiscVisual = existing;
                    }
                    else
                    {
                        var customDisc = UnityEngine.Object.Instantiate(UltimateAssetLoader.CustomFrisbeePrefab, Disc.transform);
                        customDisc.name = "CustomFrisbeeMesh";
                        foreach (var col in customDisc.GetComponentsInChildren<Collider>(true))
                            UnityEngine.Object.Destroy(col);
                        UltimateAssetLoader.SmoothMeshNormals(customDisc);
                        UltimateAssetLoader.ApplyRuntimeMaterials(customDisc, 0.65f);
                        CustomDiscVisual = customDisc.transform;
                    }
                }

                // Keep vanilla green/black puck mesh hidden while keeping our CustomFrisbeeMesh visible!
                foreach (var r in Disc.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (CustomDiscVisual != null && (r.transform == CustomDiscVisual || r.transform.IsChildOf(CustomDiscVisual)))
                    {
                        r.enabled = true;
                        continue;
                    }
                    if (r.GetComponentInParent<PuckElevationIndicator>() != null)
                        continue;

                    if (r.enabled) r.enabled = false;
                }

                Disc.transform.localScale = Vector3.one;
                CustomDiscVisual.localPosition = Vector3.zero;
                CustomDiscVisual.localRotation = Quaternion.Euler(0f, _visualSpinAngle, 0f) * Quaternion.Euler(UltimateVisualTuning.DiscMeshRotationOffset);
                CustomDiscVisual.localScale = Vector3.one * UltimateVisualTuning.DiscMeshScale;
            }
            else
            {
                // Fallback if no custom prefab: flatten vanilla puck into a disc shape
                Disc.transform.localScale = new Vector3(2.2f, 0.35f, 2.2f);
            }
            
        }

        public static void ComputeHandGripTransform(Player holder, sbyte throwStyle, out Vector3 discPos, out Quaternion discRot)
        {
            Stick stick = holder.Stick;
            bool isLeft = holder.Handedness != null && holder.Handedness.Value == PlayerHandedness.Left;

            Vector3 bladeTip = UltimateRulesManager.GetHandWorldPosition(stick, out _, out Vector3 shaftDir);

            Quaternion lookRot = Quaternion.LookRotation(shaftDir, Vector3.up);
            float wristRoll = stick.BladeAngle * UltimateVisualTuning.WristRollMultiplier;
            Quaternion handGripRot = lookRot * Quaternion.Euler(0f, 0f, wristRoll);

            Vector3 localOffset = UltimateVisualTuning.HeldDiscHandOffset;
            if (isLeft) localOffset.x = -localOffset.x;

            discPos = bladeTip + (handGripRot * localOffset);
            discRot = handGripRot * Quaternion.Euler(UltimateVisualTuning.HeldDiscPitchOffset);

            float minY = GetMinimumCenterHeightForRotation(discRot);
            if (discPos.y < minY) discPos.y = minY;
        }

        private static bool _showTunerGUI = false;

        private void Update()
        {
            if (UnityEngine.InputSystem.Keyboard.current != null &&
                UnityEngine.InputSystem.Keyboard.current.f8Key.wasPressedThisFrame)
            {
                _showTunerGUI = !_showTunerGUI;
            }

            if (!_showTunerGUI || UnityEngine.InputSystem.Keyboard.current == null) return;

            var kb = UnityEngine.InputSystem.Keyboard.current;
            float step = 0.01f * Time.deltaTime * 8f;
            float rotStep = 25f * Time.deltaTime;

            // Live keyboard nudging while F8 Tuner is open:
            // Arrow Up/Down = Z (Forward/Back along fingers)
            // PageUp/PageDown (or I/K) = Y (Up/Down above fingers)
            // Arrow Left/Right = X (Left/Right toward thumb)
            // [ and ] = Pitch angle of disc in hand
            if (kb.upArrowKey.isPressed) UltimateVisualTuning.HeldDiscHandOffset.z += step;
            if (kb.downArrowKey.isPressed) UltimateVisualTuning.HeldDiscHandOffset.z -= step;
            if (kb.rightArrowKey.isPressed) UltimateVisualTuning.HeldDiscHandOffset.x += step;
            if (kb.leftArrowKey.isPressed) UltimateVisualTuning.HeldDiscHandOffset.x -= step;
            if (kb.pageUpKey.isPressed || kb.iKey.isPressed) UltimateVisualTuning.HeldDiscHandOffset.y += step;
            if (kb.pageDownKey.isPressed || kb.kKey.isPressed) UltimateVisualTuning.HeldDiscHandOffset.y -= step;
            if (kb.leftBracketKey.isPressed) UltimateVisualTuning.HeldDiscPitchOffset.x -= rotStep;
            if (kb.rightBracketKey.isPressed) UltimateVisualTuning.HeldDiscPitchOffset.x += rotStep;
        }

        private void OnGUI()
        {
            if (!_showTunerGUI) return;

            GUI.Box(new Rect(20, 120, 430, 390), "ULTIMATE VISUAL TUNER (F8 to Hide)");
            int y = 148;

            GUI.Label(new Rect(35, y, 390, 22), "Keys: Arrows = X/Z | I/K (PgUp/Dn) = Y Height | [ / ] = Pitch");
            y += 26;

            Vector3 off = UltimateVisualTuning.HeldDiscHandOffset;
            GUI.Label(new Rect(35, y, 165, 20), $"Disc Offset X: {off.x:F3}");
            off.x = GUI.HorizontalSlider(new Rect(205, y + 4, 215, 20), off.x, -0.35f, 0.35f);
            y += 24;

            GUI.Label(new Rect(35, y, 165, 20), $"Disc Offset Y: {off.y:F3}");
            off.y = GUI.HorizontalSlider(new Rect(205, y + 4, 215, 20), off.y, -0.25f, 0.35f);
            y += 24;

            GUI.Label(new Rect(35, y, 165, 20), $"Disc Offset Z: {off.z:F3}");
            off.z = GUI.HorizontalSlider(new Rect(205, y + 4, 215, 20), off.z, -0.10f, 0.60f);
            UltimateVisualTuning.HeldDiscHandOffset = off;
            y += 24;

            Vector3 pitch = UltimateVisualTuning.HeldDiscPitchOffset;
            GUI.Label(new Rect(35, y, 165, 20), $"Disc Pitch X: {pitch.x:F1}°");
            pitch.x = GUI.HorizontalSlider(new Rect(205, y + 4, 215, 20), pitch.x, -60f, 45f);
            y += 24;

            GUI.Label(new Rect(35, y, 165, 20), $"Disc Roll Z: {pitch.z:F1}°");
            pitch.z = GUI.HorizontalSlider(new Rect(205, y + 4, 215, 20), pitch.z, -45f, 45f);
            UltimateVisualTuning.HeldDiscPitchOffset = pitch;
            y += 24;

            GUI.Label(new Rect(35, y, 165, 20), $"Horiz Reach: {UltimateVisualTuning.ArmHorizontalCompression:F2}");
            UltimateVisualTuning.ArmHorizontalCompression = GUI.HorizontalSlider(new Rect(205, y + 4, 215, 20), UltimateVisualTuning.ArmHorizontalCompression, 0.35f, 1.0f);
            y += 24;

            GUI.Label(new Rect(35, y, 165, 20), $"Max Horiz (m): {UltimateVisualTuning.ArmMaxHorizontalReach:F2}");
            UltimateVisualTuning.ArmMaxHorizontalReach = GUI.HorizontalSlider(new Rect(205, y + 4, 215, 20), UltimateVisualTuning.ArmMaxHorizontalReach, 0.50f, 1.60f);
            y += 24;

            GUI.Label(new Rect(35, y, 165, 20), $"Vert/High Reach: {UltimateVisualTuning.ArmVerticalMultiplier:F2}");
            UltimateVisualTuning.ArmVerticalMultiplier = GUI.HorizontalSlider(new Rect(205, y + 4, 215, 20), UltimateVisualTuning.ArmVerticalMultiplier, 0.80f, 2.00f);
            y += 24;

            GUI.Label(new Rect(35, y, 165, 20), $"Arm Length: {UltimateVisualTuning.ArmLengthMultiplier:F2}");
            UltimateVisualTuning.ArmLengthMultiplier = GUI.HorizontalSlider(new Rect(205, y + 4, 215, 20), UltimateVisualTuning.ArmLengthMultiplier, 0.30f, 1.20f);
            y += 24;

            GUI.Label(new Rect(35, y, 165, 20), $"Arm Thickness: {UltimateVisualTuning.ArmThicknessMultiplier:F2}");
            UltimateVisualTuning.ArmThicknessMultiplier = GUI.HorizontalSlider(new Rect(205, y + 4, 215, 20), UltimateVisualTuning.ArmThicknessMultiplier, 0.5f, 2.5f);
            y += 28;

            string codeSnippet = $"HReach={UltimateVisualTuning.ArmHorizontalCompression:F2}f | MaxH={UltimateVisualTuning.ArmMaxHorizontalReach:F2}f | VMult={UltimateVisualTuning.ArmVerticalMultiplier:F2}f | ArmLen={UltimateVisualTuning.ArmLengthMultiplier:F2}f";
            GUI.TextField(new Rect(35, y, 395, 22), codeSnippet);
        }

        private void LateUpdate()
        {
            if (Disc == null) return;

            ulong discNetId = (Disc.NetworkObject != null) ? Disc.NetworkObjectId : 0UL;
            ulong holderId = ulong.MaxValue;
            sbyte syncedStyle = UltimateNetworkSync.ClientSyncedThrowStyle;

            if (UltimateNetworkSync.ClientDiscStates.TryGetValue(discNetId, out var syncEntry))
            {
                holderId = syncEntry.HolderClientId;
                syncedStyle = syncEntry.ThrowStyle;
            }
            else if (!UltimateRulesManager.IsWarmup)
            {
                holderId = UltimateNetworkSync.ClientSyncedHolderId;
            }

            bool isHeld = (holderId != ulong.MaxValue && MonoBehaviourSingleton<PlayerManager>.Instance != null);

            float dt = Mathf.Max(0.0001f, Time.deltaTime);
            float instantSpeed = Vector3.Distance(Disc.transform.position, _lastFrameWorldPos) / dt;
            _lastFrameWorldPos = Disc.transform.position;
            _smoothedWorldSpeed = Mathf.Lerp(_smoothedWorldSpeed, instantSpeed, Mathf.Clamp01(dt * 12f));

            float minY = GetMinimumCenterHeightForRotation(Disc.transform.rotation);
            bool isAirborne = !isHeld && (Disc.transform.position.y > minY + 0.03f);

            if (!isHeld && (isAirborne || _smoothedWorldSpeed > 0.25f))
            {
                sbyte spinSign = syncedStyle == 0 ? (sbyte)1 : syncedStyle;
                float speedFactor = isAirborne
                    ? Mathf.Clamp01(Mathf.Max(0.45f, _smoothedWorldSpeed / 15f))
                    : Mathf.Clamp01(_smoothedWorldSpeed / 10f);

                float spinDegPerSec = Mathf.Lerp(540f, 1600f, speedFactor) * spinSign;
                _visualSpinAngle = (_visualSpinAngle + spinDegPerSec * Time.deltaTime) % 360f;
            }
            else if (isHeld)
            {
                _visualSpinAngle = 0f;
                _smoothedWorldSpeed = 0f;
            }

            EnsureCustomDiscVisual();

            if (!isHeld)
            {
                if (Disc.transform.position.y < minY)
                {
                    Vector3 p = Disc.transform.position;
                    p.y = minY;
                    Disc.transform.position = p;
                }
                return;
            }

            Player holder = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(holderId);
            if (holder == null || !holder.IsCharacterSpawned || holder.Stick == null || holder.PlayerBody == null)
                return;

            if (ElevationIndicator != null && ElevationIndicator.IsVisible)
            {
                ElevationIndicator.IsVisible = false;
            }

            ComputeHandGripTransform(holder, syncedStyle, out Vector3 gripPos, out Quaternion discRot);

            Disc.transform.SetPositionAndRotation(gripPos, discRot);
            if (NetworkRoles.IsServer && Disc.Rigidbody != null)
            {
                Disc.Rigidbody.position = gripPos;
                Disc.Rigidbody.rotation = discRot;
            }
        }
    }

    [HarmonyPatch(typeof(Puck), "OnNetworkPostSpawn")]
    public static class Puck_DiscSetup_Patch
    {
        private static readonly FieldInfo _indicatorField = AccessTools.Field(typeof(Puck), "verticalityIndicator");
        private static readonly FieldInfo _maxDistField = AccessTools.Field(typeof(PuckElevationIndicator), "maximumDistance");

        [HarmonyPostfix]
        public static void Postfix(Puck __instance)
        {
            UltimateNetworkSync.EnsureRegistered();

            if (__instance.StickCollider != null)
            {
                __instance.StickCollider.enabled = false;
            }

            if (__instance.IceCollider != null && MonoBehaviourSingleton<PlayerManager>.Instance != null)
            {
                foreach (var p in MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(true))
                {
                    if (p.PlayerBody != null)
                    {
                        foreach (var bodyCol in p.PlayerBody.GetComponentsInChildren<Collider>(true))
                        {
                            Physics.IgnoreCollision(__instance.IceCollider, bodyCol, true);
                        }
                    }
                }
            }

            var indicator = _indicatorField?.GetValue(__instance) as PuckElevationIndicator;
            if (indicator != null)
            {
                _maxDistField?.SetValue(indicator, 45f);
            }

            var follower = __instance.GetComponent<ClientDiscFollower>();
            if (follower == null)
            {
                follower = __instance.gameObject.AddComponent<ClientDiscFollower>();
            }
            follower.Disc = __instance;
            follower.ElevationIndicator = indicator;
            follower.EnsureCustomDiscVisual();
        }
    }

    [HarmonyPatch(typeof(Puck), "OnNetworkDespawn")]
    public static class Puck_OnNetworkDespawn_Cleanup_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Puck __instance)
        {
            if (__instance == null) return;

            ulong discNetId = (__instance.NetworkObject != null) ? __instance.NetworkObjectId : 0UL;
            UltimateNetworkSync.ClientDiscStates.Remove(discNetId);

            if (UltimateRulesManager.DiscStates.TryGetValue(__instance, out var ds))
            {
                if (NetworkRoles.IsServer && ds.Holder != null && ds.Holder.PlayerBody != null)
                {
                    ds.Holder.PlayerBody.Server_Unfreeze();
                }
                UltimateRulesManager.DiscStates.Remove(__instance);
            }

            if (NetworkRoles.IsServer && UltimateRulesManager.Holder != null)
            {
                if (UltimateRulesManager.Holder.PlayerBody != null)
                {
                    UltimateRulesManager.Holder.PlayerBody.Server_Unfreeze();
                }
                UltimateRulesManager.Holder = null;
            }
        }
    }

    // =========================================================================
     // 8. SERVER DISC PHYSICS, STAMINA-POWERED THROWS & RULES (Puck.FixedUpdate)
    // =========================================================================
    [HarmonyPatch(typeof(Puck), "FixedUpdate")]
    public static class Puck_UltimatePhysicsAndCatch_Patch
    {
        private static void LoadDiscState(UltimateRulesManager.DiscFlightState ds)
        {
            if (ds == null) return;
            UltimateRulesManager.Holder = ds.Holder;
            UltimateRulesManager.LastThrower = ds.LastThrower;
            UltimateRulesManager.SecondLastThrower = ds.SecondLastThrower;
            UltimateRulesManager.IsDiscInFlight = ds.IsDiscInFlight;
            UltimateRulesManager.IsDiscTumblingOnGround = ds.IsDiscTumblingOnGround;
            UltimateRulesManager.ActiveFlightThrowType = ds.ActiveFlightThrowType;
            UltimateRulesManager.ActiveFlightSpinSign = ds.ActiveFlightSpinSign;
            UltimateRulesManager.FirstGroundTouchTime = ds.FirstGroundTouchTime;
            UltimateRulesManager.WasTippedInFlight = ds.WasTippedInFlight;
            UltimateRulesManager.WasDefensiveBlock = ds.WasDefensiveBlock;
            UltimateRulesManager.LastDeflector = ds.LastDeflector;
            UltimateRulesManager.LastDeflectionTime = ds.LastDeflectionTime;
            UltimateRulesManager.CatchTime = ds.CatchTime;
            UltimateRulesManager.ReleaseTime = ds.ReleaseTime;
            UltimateRulesManager.LastInBoundsFlightPos = ds.LastInBoundsFlightPos;
            UltimateRulesManager.WentOutOfBoundsInFlight = ds.WentOutOfBoundsInFlight;
            UltimateRulesManager.IsHolderPivotLocked = ds.IsHolderPivotLocked;
            UltimateRulesManager.CatchInitialSpot = ds.CatchInitialSpot;
            UltimateRulesManager.CaughtInCentralZone = ds.CaughtInCentralZone;
        }

        private static void SaveDiscState(UltimateRulesManager.DiscFlightState ds)
        {
            if (ds == null) return;
            ds.Holder = UltimateRulesManager.Holder;
            ds.LastThrower = UltimateRulesManager.LastThrower;
            ds.SecondLastThrower = UltimateRulesManager.SecondLastThrower;
            ds.IsDiscInFlight = UltimateRulesManager.IsDiscInFlight;
            ds.IsDiscTumblingOnGround = UltimateRulesManager.IsDiscTumblingOnGround;
            ds.ActiveFlightThrowType = UltimateRulesManager.ActiveFlightThrowType;
            ds.ActiveFlightSpinSign = UltimateRulesManager.ActiveFlightSpinSign;
            ds.FirstGroundTouchTime = UltimateRulesManager.FirstGroundTouchTime;
            ds.WasTippedInFlight = UltimateRulesManager.WasTippedInFlight;
            ds.WasDefensiveBlock = UltimateRulesManager.WasDefensiveBlock;
            ds.LastDeflector = UltimateRulesManager.LastDeflector;
            ds.LastDeflectionTime = UltimateRulesManager.LastDeflectionTime;
            ds.CatchTime = UltimateRulesManager.CatchTime;
            ds.ReleaseTime = UltimateRulesManager.ReleaseTime;
            ds.LastInBoundsFlightPos = UltimateRulesManager.LastInBoundsFlightPos;
            ds.WentOutOfBoundsInFlight = UltimateRulesManager.WentOutOfBoundsInFlight;
            ds.IsHolderPivotLocked = UltimateRulesManager.IsHolderPivotLocked;
            ds.CatchInitialSpot = UltimateRulesManager.CatchInitialSpot;
            ds.CaughtInCentralZone = UltimateRulesManager.CaughtInCentralZone;
        }

        [HarmonyPostfix]
        public static void Postfix(Puck __instance)
        {
            if (!NetworkRoles.IsServer) return;
            if (__instance.IsReplay != null && __instance.IsReplay.Value) return;

            Rigidbody rb = __instance.Rigidbody;
            if (rb == null) return;

            bool isWarmup = UltimateRulesManager.IsWarmup;
            var ds = UltimateRulesManager.GetDiscState(__instance);

            if (isWarmup)
            {
                LoadDiscState(ds);
            }

            try
            {
                if (NetworkBehaviourSingleton<GameManager>.Instance != null)
                {
                    GamePhase currentPhase = NetworkBehaviourSingleton<GameManager>.Instance.Phase;
                    if (currentPhase != UltimateRulesManager.LastObservedPhase)
                    {
                        GamePhase prevPhase = UltimateRulesManager.LastObservedPhase;
                        UltimateRulesManager.LastObservedPhase = currentPhase;
                        OnGamePhaseChanged(__instance, prevPhase, currentPhase);
                    }

                    if (currentPhase == GamePhase.FaceOff)
                    {
                        EnforceGoalLineLineup(__instance);
                        return;
                    }
                }

                if (!isWarmup && UltimateRulesManager.IsPullPlay)
                {
                    EnforcePrePullGoalLineOffsides();
                }

                // -----------------------------------------------------------------
                // A. IF DISC IS CURRENTLY HELD BY A PLAYER
                // -----------------------------------------------------------------
                if (UltimateRulesManager.Holder != null)
                {
                    Player holder = UltimateRulesManager.Holder;
                    if (!holder.IsCharacterSpawned || holder.Stick == null || holder.PlayerBody == null || holder.PlayerInput == null)
                    {
                        UltimateRulesManager.Holder = null;
                        UltimateRulesManager.IsHolderPivotLocked = false;
                        UltimateNetworkSync.ServerBroadcastHolderState(null, 0, __instance);
                        rb.isKinematic = false;
                        __instance.Server_Unfreeze();
                        return;
                    }

                    float holdTime = Time.time - UltimateRulesManager.CatchTime;
                    Rigidbody bodyRb = holder.PlayerBody.Rigidbody;

                    // --- MOMENTUM CARRY-OVER GRACE PERIOD (2-3 Step Deceleration Stop) ---
                    if (!UltimateRulesManager.IsHolderPivotLocked)
                    {
                        Vector3 bodyVel = bodyRb.linearVelocity;
                        Vector3 horizVel = new Vector3(bodyVel.x, 0f, bodyVel.z);
                        float horizSpeed = horizVel.magnitude;

                        if (holdTime < UltimateRulesManager.CatchMomentumGraceDuration && horizSpeed > 0.45f)
                        {
                            // Smoothly decelerate the runner's momentum instead of jarring instant freeze
                            Vector3 newHorizVel = Vector3.MoveTowards(
                                horizVel,
                                Vector3.zero,
                                UltimateRulesManager.CatchMomentumDecelRate * Time.fixedDeltaTime
                            );
                            bodyRb.linearVelocity = new Vector3(newHorizVel.x, bodyVel.y, newHorizVel.z);

                            // Keep player upright while skidding/stopping
                            Vector3 angVel = bodyRb.angularVelocity;
                            bodyRb.angularVelocity = new Vector3(0f, angVel.y, 0f);
                        }
                        else
                        {
                            // Establish Pivot Foot!
                            UltimateRulesManager.IsHolderPivotLocked = true;
                            EstablishPivotAfterMomentumStop(holder, isWarmup);
                        }
                    }
                    else
                    {
                        holder.PlayerBody.Server_Freeze((RigidbodyConstraints)10);
                        Vector3 bodyVel = bodyRb.linearVelocity;
                        bodyRb.linearVelocity = new Vector3(0f, bodyVel.y, 0f);
                    }

                    rb.isKinematic = true;

                    UltimateThrowType throwType = UltimateRulesManager.DetermineThrowType(holder, out float spinSign);
                    sbyte spinByte = (sbyte)(spinSign >= 0f ? 1 : -1);

                    ulong discNetId = (__instance.NetworkObject != null) ? __instance.NetworkObjectId : 0UL;
                    if (!UltimateNetworkSync.ClientDiscStates.TryGetValue(discNetId, out var syncState) ||
                        syncState.HolderClientId != holder.OwnerClientId ||
                        syncState.ThrowStyle != spinByte)
                    {
                        UltimateNetworkSync.ServerBroadcastHolderState(holder, spinByte, __instance);
                    }

                    ClientDiscFollower.ComputeHandGripTransform(holder, spinByte, out Vector3 gripPos, out Quaternion discRot);

                    rb.position = gripPos;
                    rb.rotation = discRot;
                    __instance.transform.SetPositionAndRotation(gripPos, discRot);

                    Vector3 handPos = UltimateRulesManager.GetHandWorldPosition(holder.Stick);
                    Vector3 aimForward = (handPos - holder.PlayerBody.transform.position);
                    aimForward.y = 0f;
                    if (aimForward.sqrMagnitude < 0.001f) aimForward = holder.PlayerBody.transform.forward;
                    aimForward.Normalize();

                    float hyzerAngle = holder.Stick.BladeAngle * UltimateVisualTuning.WristRollMultiplier;

                    ulong clientId = holder.OwnerClientId;
                    if (!UltimateRulesManager.WindupTrackers.TryGetValue(clientId, out var tracker))
                    {
                        tracker = new UltimateRulesManager.ThrowWindupTracker();
                        UltimateRulesManager.WindupTrackers[clientId] = tracker;
                    }

                    bool isHoldingShift = holder.PlayerInput.SprintInput.ServerValue;
                    Vector2 currentStickAngle = holder.PlayerInput.StickRaycastOriginAngleInput.ServerValue;

                    if (holder.PlayerBody.IsSprinting != null)
                    {
                        holder.PlayerBody.IsSprinting.Value = false;
                    }

                    // 1. If the player was already holding Shift when they caught the disc (or just cancelled a throw),
                    //    wait until they let go of Shift before allowing a new throw wind-up!
                    if (tracker.RequireFreshShiftPress)
                    {
                        if (holder.PlayerBody.Stamina != null)
                            holder.PlayerBody.Stamina.Value = 1.0f;

                        if (!isHoldingShift)
                        {
                            tracker.RequireFreshShiftPress = false;
                        }
                    }
                    else if (!tracker.IsCharging)
                    {
                        if (holder.PlayerBody.Stamina != null)
                            holder.PlayerBody.Stamina.Value = 1.0f;

                        if (isHoldingShift && holdTime > 0.12f)
                        {
                            tracker.BeginCharge(holder.PlayerBody, holder.Stick, currentStickAngle);
                        }
                    }
                    else
                    {
                        if (isHoldingShift)
                        {
                            tracker.UpdateCharge(holder.PlayerBody, holder.Stick, currentStickAngle, Time.fixedDeltaTime);
                        }
                        else
                        {
                            ExecuteStaminaThrow(__instance, holder, tracker, currentStickAngle, aimForward, throwType, hyzerAngle, spinSign);
                            return;
                        }
                    }

                    // --- REAL ULTIMATE MARKER-BASED 10-SECOND STALL COUNT & DOUBLE-TEAM CHECK ---
                    if (!isWarmup && !UltimateRulesManager.IsPullPlay)
                    {
                        UpdateMarkerAndStallCount(__instance, holder, tracker, aimForward, spinByte, rb);
                    }
                    return;
                }

                // -----------------------------------------------------------------
                // B. TRACK IN-BOUNDS FLIGHT CROSSING POINT FOR ACCURATE OOB SPOTS
                // -----------------------------------------------------------------
                Vector3 discPos = __instance.transform.position;
                bool isOutOfBounds = !isWarmup && UltimateRulesManager.IsPositionOutOfBounds(discPos);

                if (UltimateRulesManager.IsDiscInFlight)
                {
                    if (!isOutOfBounds)
                    {
                        UltimateRulesManager.LastInBoundsFlightPos = discPos;
                    }
                    else if (!UltimateRulesManager.WentOutOfBoundsInFlight)
                    {
                        UltimateRulesManager.WentOutOfBoundsInFlight = true;
                    }
                }

                // -----------------------------------------------------------------
                // C. SWEPT HAND CATCH (LEVEL vs BLADED), ANTI-JUGGLE & GROUND PICKUP
                // -----------------------------------------------------------------
                float timeSinceRelease = Time.time - UltimateRulesManager.ReleaseTime;
                bool isAirborne = UltimateRulesManager.IsDiscInFlight && !UltimateRulesManager.IsDiscTumblingOnGround;

                Vector3 vel = rb.linearVelocity;
                float discSpeed = vel.magnitude;
                Vector3 sweptStartPos = (timeSinceRelease > Time.fixedDeltaTime * 1.5f)
                    ? discPos - vel * Time.fixedDeltaTime
                    : discPos;

                if (MonoBehaviourSingleton<PlayerManager>.Instance != null)
                {
                    var allPlayers = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(false);

                    Vector3 discUp = __instance.transform.up;
                    float discFlatness = Mathf.Clamp01(Mathf.Abs(discUp.y));
                    float bladeFactor = 1f - discFlatness;

                    foreach (Player player in allPlayers)
                    {
                        if (!player.IsCharacterSpawned || player.Stick == null || player.PlayerBody == null)
                            continue;

                        if (isWarmup && UltimateNetworkSync.IsPlayerHoldingAnyDisc(player.OwnerClientId))
                            continue;

                        PlayerTeam playerTeam = UltimateRulesManager.GetTeam(player);

                        // Prevent thrower from immediately re-catching their own release OR catching a point-blank defensive hand-block!
                        if (player == UltimateRulesManager.LastThrower)
                        {
                            if (timeSinceRelease < 0.35f && !UltimateRulesManager.WasTippedInFlight)
                                continue;
                            if (UltimateRulesManager.WasDefensiveBlock)
                                continue;
                        }

                        // Use the compressed hand position for all catch & tip checks!
                        Vector3 handPos = UltimateRulesManager.GetHandWorldPosition(player.Stick, out _, out Vector3 shaftDir);
                        Vector3 bodyBasePos = player.PlayerBody.transform.position;
                        bool isDiving = UltimateDiveMod.IsPlayerDiving(player);

                        Vector3 closestDiscPt = ClosestPointOnSegment(sweptStartPos, discPos, handPos);
                        float sweptHandDist = Vector3.Distance(closestDiscPt, handPos);

                        Vector3 chestCatchPt = bodyBasePos + Vector3.up * 1.05f + player.PlayerBody.transform.forward * 0.25f;
                        float chestDist = Vector3.Distance(closestDiscPt, chestCatchPt);

                        // 1. GROUND PICKUP
                        if (!isAirborne)
                        {
                            float horizFootDist = Vector2.Distance(
                                new Vector2(discPos.x, discPos.z),
                                new Vector2(bodyBasePos.x, bodyBasePos.z));

                            if (sweptHandDist <= 1.25f || horizFootDist <= 1.25f)
                            {
                                bool canPickup = false;
                                if (isWarmup)
                                {
                                    canPickup = true;
                                }
                                else if (UltimateRulesManager.IsPullPlay)
                                {
                                    canPickup = (playerTeam == UltimateRulesManager.PullingTeam);
                                }
                                else
                                {
                                    bool isOnTurnoverTeam = (UltimateRulesManager.LastTurnoverTeam != PlayerTeam.None && playerTeam == UltimateRulesManager.LastTurnoverTeam);
                                    bool isThrowerAfterOwnTurnover = (UltimateRulesManager.IsDiscTumblingOnGround && player == UltimateRulesManager.LastThrower);

                                    if (!isOnTurnoverTeam && !isThrowerAfterOwnTurnover &&
                                        (UltimateRulesManager.OffensiveTeam == PlayerTeam.None || playerTeam == UltimateRulesManager.OffensiveTeam))
                                    {
         	                            canPickup = true;
                                    }
                                }

                                if (canPickup)
                                {
                                    HandlePlayerCatch(__instance, player);
                                    return;
                                }
                            }
                            continue;
                        }

                        // 2. AIRBORNE INTERACTIONS
                        if (!isWarmup && UltimateRulesManager.IsCurrentThrowPull && !UltimateRulesManager.WasTippedInFlight && playerTeam == UltimateRulesManager.PullingTeam)
                            continue;

                        bool canAttemptAirPlay = isWarmup ||
                                                 (player != UltimateRulesManager.LastThrower) ||
                                                 (UltimateRulesManager.WasTippedInFlight && !UltimateRulesManager.WasDefensiveBlock);

                        if (!canAttemptAirPlay)
                            continue;

                        // Point-blank defender mark check: Within 0.22s of release and within 3.2m of thrower,
                        // a defender's hand block swats the disc straight down instead of catching it cleanly!
                        bool isPointBlankMarkBlock = !isWarmup &&
                                                     UltimateRulesManager.LastThrower != null &&
                                                     UltimateRulesManager.AreOpponents(player, UltimateRulesManager.LastThrower) &&
                                                     timeSinceRelease < 0.22f &&
                                                     Vector3.Distance(bodyBasePos, UltimateRulesManager.LastThrower.PlayerBody.transform.position) < 3.2f;

                        Quaternion handAimRot = Quaternion.LookRotation(shaftDir, Vector3.up) *
                                                Quaternion.Euler(0f, 0f, player.Stick.BladeAngle * UltimateVisualTuning.WristRollMultiplier);
                        Vector3 handNormal = handAimRot * Vector3.up;
                        float handPlaneMatch = Mathf.Abs(Vector3.Dot(handNormal, discUp));

                        float baseCatchRadius = Mathf.Lerp(0.35f, 0.60f, Mathf.Pow(discFlatness, 0.85f));
                        float wristMatchBonus = bladeFactor * handPlaneMatch * 0.14f;
                        float speedPenalty = Mathf.Lerp(0f, 0.07f, Mathf.Clamp01((discSpeed - 12f) / 10f)) * bladeFactor;
                        float cleanCatchRadius = baseCatchRadius + wristMatchBonus - speedPenalty;
                        if (isDiving)
                        {
                            cleanCatchRadius += 0.28f;
                        }

                        bool isRecoveringOwnBobble = UltimateRulesManager.WasTippedInFlight &&
                                                     UltimateRulesManager.LastDeflector == player &&
                                                     (Time.time - UltimateRulesManager.LastDeflectionTime > 0.10f);
                        if (isRecoveringOwnBobble)
                        {
                            cleanCatchRadius = Mathf.Max(cleanCatchRadius, 0.64f);
                        }

                        bool caughtByHand = !isPointBlankMarkBlock && (sweptHandDist <= cleanCatchRadius);
                        bool caughtInBreadbasket = !isPointBlankMarkBlock && (discFlatness > 0.72f && chestDist <= 0.42f && sweptHandDist <= 0.80f);

                        // A. CLEAN HAND CATCH
                        if (caughtByHand || caughtInBreadbasket)
                        {
                            HandlePlayerCatch(__instance, player);
                            return;
                        }

                        if (player == UltimateRulesManager.LastDeflector && (Time.time - UltimateRulesManager.LastDeflectionTime < 0.45f))
                            continue;

                        // B. BLADED RIM GRAZE / DEFENSIVE HAND BLOCK
                        float outerTipRadius = Mathf.Lerp(0.64f, 0.72f, bladeFactor) + (isDiving ? 0.25f : 0f);
                        if (sweptHandDist <= outerTipRadius)
                        {
                            ApplyAirborneDeflection(__instance, rb, player, handPos, isHandTip: true, bladeFactor: bladeFactor);
                            break;
                        }

                        // C. BODY BLOCK / CHEST & SHOULDER BOUNCE
                        float horizBodyDist = Vector2.Distance(
                            new Vector2(discPos.x, discPos.z),
                            new Vector2(bodyBasePos.x, bodyBasePos.z));
                        float relativeHeight = discPos.y - bodyBasePos.y;

                        if (horizBodyDist <= 0.45f && relativeHeight >= 0.05f && relativeHeight <= 1.70f)
                        {
                            Vector3 bodyCenter = bodyBasePos + Vector3.up * Mathf.Clamp(relativeHeight, 0.3f, 1.3f);
                            ApplyAirborneDeflection(__instance, rb, player, bodyCenter, isHandTip: false, bladeFactor: bladeFactor);
                            break;
                        }
                    }
                }

                // -----------------------------------------------------------------
                // D. RIM GROUND COLLISION, TUMBLING, OOB PULLS & TURNOVER CHECK
                // -----------------------------------------------------------------
                float minCenterY = ClientDiscFollower.GetMinimumCenterHeightForRotation(__instance.transform.rotation);
                bool rimTouchingGround = (discPos.y <= minCenterY + 0.015f) || (__instance.IsGrounded && timeSinceRelease > 0.15f);

                if (rimTouchingGround)
                {
                    ResolveRimGroundContactAndTumble(__instance, rb, minCenterY);
                }

                if (UltimateRulesManager.IsDiscInFlight)
                {
                    if (!isWarmup && isOutOfBounds && rimTouchingGround)
                    {
                        UltimateRulesManager.IsDiscInFlight = false;
                        UltimateRulesManager.IsDiscTumblingOnGround = false;

                        Vector3 crossingSpot = UltimateRulesManager.WentOutOfBoundsInFlight
                            ? UltimateRulesManager.LastInBoundsFlightPos
                            : discPos;

                        bool wasPull = UltimateRulesManager.IsCurrentThrowPull;
                        bool wasDroppedPull = wasPull && UltimateRulesManager.WasTippedInFlight &&
                                              UltimateRulesManager.LastDeflector != null &&
                                              UltimateRulesManager.GetTeam(UltimateRulesManager.LastDeflector) != UltimateRulesManager.PullingTeam;

                        AnnounceTurnover(isOutOfBounds: true, outOfBoundsCrossingSpot: crossingSpot);

                        Vector3 legalSpot = (wasPull && !wasDroppedPull)
                            ? GetOutOfBoundsPullSpot(crossingSpot, UltimateRulesManager.OffensiveTeam)
                            : GetLegalFieldSpotAfterTurnover(crossingSpot);

                        UltimateRulesManager.IsCurrentThrowPull = false;
                        FlattenAndFreezeDiscOnGround(__instance, rb, legalSpot);
                        return;
                    }

                    if (rimTouchingGround && timeSinceRelease > 0.08f && !UltimateRulesManager.IsDiscTumblingOnGround)
                    {
                        UltimateRulesManager.IsDiscTumblingOnGround = true;
                        UltimateRulesManager.FirstGroundTouchTime = Time.time;

                        if (!isWarmup)
                        {
                            bool wasPull = UltimateRulesManager.IsCurrentThrowPull;
                            AnnounceTurnover(isOutOfBounds: false, outOfBoundsCrossingSpot: discPos);

                            if (!wasPull && Mathf.Abs(discPos.z) > UltimateRulesManager.EndzoneZThreshold)
                            {
                                UltimateRulesManager.IsDiscInFlight = false;
                                UltimateRulesManager.IsDiscTumblingOnGround = false;
                                FlattenAndFreezeDiscOnGround(__instance, rb, GetLegalFieldSpotAfterTurnover(discPos));
                                return;
                            }
                        }
                    }
                }

                if (UltimateRulesManager.IsDiscTumblingOnGround)
                {
                    if (!isWarmup && isOutOfBounds)
                    {
                        UltimateRulesManager.IsDiscInFlight = false;
                        UltimateRulesManager.IsDiscTumblingOnGround = false;

                        bool wasPull = UltimateRulesManager.IsCurrentThrowPull;
                        UltimateRulesManager.IsCurrentThrowPull = false;

                        if (wasPull)
                        {
                            Vector3 pullSpot = GetOutOfBoundsPullSpot(discPos, UltimateRulesManager.OffensiveTeam);
                            UltimateRulesManager.BroadcastAnnouncement($"PULL ROLLED OUT OF BOUNDS! Centered at (X=0, Z={pullSpot.z:F0}m) for {UltimateRulesManager.OffensiveTeam}!", "#ffe97f");
                            FlattenAndFreezeDiscOnGround(__instance, rb, pullSpot);
                        }
                        else
                        {
                            FlattenAndFreezeDiscOnGround(__instance, rb, GetLegalFieldSpotAfterTurnover(discPos));
                        }
                        return;
                    }

                    float speed = rb.linearVelocity.magnitude;
                    float tumbleTime = Time.time - UltimateRulesManager.FirstGroundTouchTime;
                    if ((speed < 0.35f && rimTouchingGround) || tumbleTime > 4.0f)
                    {
                        UltimateRulesManager.IsDiscInFlight = false;
                        UltimateRulesManager.IsDiscTumblingOnGround = false;
                        UltimateRulesManager.IsCurrentThrowPull = false;

                        Vector3 finalPos = __instance.transform.position;
                        Vector3 restSpot = isWarmup
                            ? new Vector3(finalPos.x, UltimateVisualTuning.DiscHalfThickness + 0.01f, finalPos.z)
                            : GetLegalFieldSpotAfterTurnover(finalPos);

                        FlattenAndFreezeDiscOnGround(__instance, rb, restSpot);
                        return;
                    }
                }
                else if (!UltimateRulesManager.IsDiscInFlight && rimTouchingGround && rb.linearVelocity.sqrMagnitude < 0.05f)
                {
                    bool waitingForPullPickup = UltimateRulesManager.IsPullPlay && UltimateRulesManager.Holder == null;
                    Vector3 restSpot = (isWarmup || waitingForPullPickup)
                        ? new Vector3(discPos.x, UltimateVisualTuning.DiscHalfThickness + 0.01f, discPos.z)
                        : GetLegalFieldSpotAfterTurnover(discPos);

                    FlattenAndFreezeDiscOnGround(__instance, rb, restSpot);
                    return;
                }

                // -----------------------------------------------------------------
                // E. FRISBEE AERODYNAMICS IN FLIGHT
                // -----------------------------------------------------------------
                if (UltimateRulesManager.IsDiscInFlight && !UltimateRulesManager.IsDiscTumblingOnGround)
                {
                    ApplyFrisbeeAerodynamics(__instance, rb);
                }
            }
            finally
            {
                SaveDiscState(ds);
            }
        }

        private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 point)
        {
            Vector3 ab = b - a;
            float sqrLen = ab.sqrMagnitude;
            if (sqrLen < 0.00001f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / sqrLen);
            return a + ab * t;
        }

        private static void UpdateMarkerAndStallCount(
            Puck disc,
            Player holder,
            UltimateRulesManager.ThrowWindupTracker tracker,
            Vector3 aimForward,
            sbyte spinByte,
            Rigidbody rb)
        {
            if (MonoBehaviourSingleton<PlayerManager>.Instance == null) return;

            PlayerTeam holderTeam = UltimateRulesManager.GetTeam(holder);
            if (holderTeam != PlayerTeam.Blue && holderTeam != PlayerTeam.Red) return;

            var allPlayers = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(false);

            Player closestDefender = null;
            float closestDist = float.MaxValue;
            Vector3 holderPos = holder.PlayerBody.transform.position;

            var defendersInDoubleTeamZone = new List<Player>();

            foreach (var p in allPlayers)
            {
                if (p == null || p == holder || !p.IsCharacterSpawned || p.PlayerBody == null)
                    continue;

                if (!UltimateRulesManager.AreOpponents(holder, p))
                    continue;

                float d = Vector3.Distance(holderPos, p.PlayerBody.transform.position);
                if (d <= UltimateRulesManager.MarkerRadius && d < closestDist)
                {
                    closestDist = d;
                    closestDefender = p;
                }

                // Check if this defender is inside the 3.0m (10ft) Marking Zone
                if (d <= UltimateRulesManager.DoubleTeamRadius)
                {
                    defendersInDoubleTeamZone.Add(p);
                }
            }

            // --- DOUBLE-TEAM DETECTION & ENFORCEMENT ---
            // A second defender is only legally allowed inside 3.0m if they are actively guarding another offensive player within 3.5m
            int illegalDoubleTeamers = 0;
            if (defendersInDoubleTeamZone.Count > 1 && closestDefender != null)
            {
                foreach (var def in defendersInDoubleTeamZone)
                {
                    if (def == closestDefender) continue;

                    bool isGuardingAnotherAttacker = false;
                    Vector3 defPos = def.PlayerBody.transform.position;

                    foreach (var other in allPlayers)
                    {
                        if (other == null || other == holder || !other.IsCharacterSpawned || other.PlayerBody == null)
                            continue;
                        if (UltimateRulesManager.GetTeam(other) == holderTeam)
                        {
                            if (Vector3.Distance(defPos, other.PlayerBody.transform.position) <= 3.5f)
                            {
                                isGuardingAnotherAttacker = true;
                                break;
                            }
                        }
                    }

                    if (!isGuardingAnotherAttacker)
                    {
                        illegalDoubleTeamers++;

                        // Gently push the illegal second marker back to the 3.0m perimeter so they can't smother the thrower
                        Vector3 pushDir = (defPos - holderPos);
                        pushDir.y = 0f;
                        if (pushDir.sqrMagnitude < 0.001f) pushDir = -def.PlayerBody.transform.forward;
                        pushDir.Normalize();

                        Vector3 legalPerimeterPos = holderPos + pushDir * (UltimateRulesManager.DoubleTeamRadius + 0.08f);
                        legalPerimeterPos.y = defPos.y;
                        def.PlayerBody.transform.position = legalPerimeterPos;
                        if (def.PlayerBody.Rigidbody != null)
                        {
                            def.PlayerBody.Rigidbody.position = legalPerimeterPos;
                            Vector3 v = def.PlayerBody.Rigidbody.linearVelocity;
                            float inwardSpeed = Vector3.Dot(v, -pushDir);
                            if (inwardSpeed > 0f)
                            {
                                def.PlayerBody.Rigidbody.linearVelocity = v + pushDir * inwardSpeed;
                            }
                        }
                    }
                }
            }

            if (illegalDoubleTeamers > 0)
            {
                // Rewind stall count while double-teaming and warn players
                UltimateRulesManager.StallCount = Mathf.Max(0f, UltimateRulesManager.StallCount - Time.fixedDeltaTime * 1.5f);
                UltimateRulesManager.LastStallAnnounced = Mathf.Min(UltimateRulesManager.LastStallAnnounced, Mathf.FloorToInt(UltimateRulesManager.StallCount));

                if (Time.time - UltimateRulesManager.LastDoubleTeamWarningTime > 2.5f)
                {
                    UltimateRulesManager.LastDoubleTeamWarningTime = Time.time;
                    UltimateRulesManager.BroadcastAnnouncement("DOUBLE TEAM! Only 1 marker allowed within 3m - Stall paused & rewound!", "#f1c40f");
                }
                return;
            }

            if (closestDefender != null)
            {
                UltimateRulesManager.ActiveMarker = closestDefender;
                UltimateRulesManager.StallCount += Time.fixedDeltaTime;
            }
            else
            {
                UltimateRulesManager.ActiveMarker = null;
                return;
            }

            int stallSec = Mathf.FloorToInt(UltimateRulesManager.StallCount);
            if (stallSec >= 1 && stallSec < 10 && stallSec > UltimateRulesManager.LastStallAnnounced)
            {
                if (stallSec == 1 || stallSec == 5 || stallSec >= 7)
                {
                    UltimateRulesManager.LastStallAnnounced = stallSec;
                    string markerInfo = $" (Marked by #{closestDefender.Number.Value})";
                    UltimateRulesManager.BroadcastAnnouncement($"STALL {stallSec}...{markerInfo}", "#e67e22");
                }
            }
            else if (UltimateRulesManager.StallCount >= UltimateRulesManager.StallDuration)
            {
                PlayerTeam? opp = Utils.GetOpposingTeam(holderTeam);
                UltimateRulesManager.BroadcastAnnouncement($"STALL 10! TURNOVER -> {opp} Disc!", "#e74c3c");
                tracker.Reset(holder.PlayerBody);
                holder.PlayerBody.Server_Unfreeze();
                UltimateRulesManager.LastTurnoverTeam = holderTeam;
                UltimateRulesManager.OffensiveTeam = opp ?? PlayerTeam.None;
                UltimateRulesManager.Holder = null;
                UltimateRulesManager.IsHolderPivotLocked = false;
                UltimateRulesManager.LastThrower = holder;
                UltimateRulesManager.ReleaseTime = Time.time;
                UltimateRulesManager.StallCount = 0f;
                UltimateRulesManager.LastStallAnnounced = 0;
                UltimateNetworkSync.ServerBroadcastHolderState(null, spinByte, disc);

                Vector3 dropPos = GetLegalFieldSpotAfterTurnover(holderPos + aimForward * 0.8f);
                FlattenAndFreezeDiscOnGround(disc, rb, dropPos);
            }
        }

        private static void EstablishPivotAfterMomentumStop(Player holder, bool isWarmup)
        {
            if (holder == null || holder.PlayerBody == null) return;

            if (!isWarmup)
            {
                Vector3 pos = holder.PlayerBody.transform.position;
                float maxX = UltimateRulesManager.FieldHalfWidth - 0.35f;
                float goalLineZ = UltimateRulesManager.EndzoneZThreshold;

                bool slidOutOfBounds = UltimateRulesManager.IsPositionOutOfBounds(pos);
                bool slidIntoEndzone = UltimateRulesManager.CaughtInCentralZone && Mathf.Abs(pos.z) > goalLineZ;

                // If momentum carried an in-bounds receiver across the sideline or goal-line,
                // walk them back to the spot where they crossed / caught it to set their pivot!
                if (slidOutOfBounds || slidIntoEndzone || Mathf.Abs(pos.z) > goalLineZ)
                {
                    float pivotX = Mathf.Clamp(pos.x, -maxX, maxX);
                    float pivotZ = Mathf.Clamp(pos.z, -goalLineZ, goalLineZ);
                    Vector3 legalPivot = new Vector3(pivotX, pos.y, pivotZ);
                    holder.PlayerBody.Server_Teleport(legalPivot, holder.PlayerBody.transform.rotation);
                }
            }

            holder.PlayerBody.Server_Freeze((RigidbodyConstraints)10);
            if (holder.PlayerBody.Rigidbody != null)
            {
                Vector3 v = holder.PlayerBody.Rigidbody.linearVelocity;
                holder.PlayerBody.Rigidbody.linearVelocity = new Vector3(0f, v.y, 0f);
            }
        }

        private static void EnforcePrePullGoalLineOffsides()
        {
            if (MonoBehaviourSingleton<PlayerManager>.Instance == null) return;

            foreach (var player in MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(false))
            {
                if (!player.IsCharacterSpawned || player.PlayerBody == null) continue;
                if (player == UltimateRulesManager.Holder) continue;

                Vector3 pos = player.PlayerBody.transform.position;
                float goalLineZ = UltimateRulesManager.EndzoneZThreshold;
                PlayerTeam pTeam = UltimateRulesManager.GetTeam(player);

                if (pTeam == PlayerTeam.Blue && pos.z < goalLineZ)
                {
                    pos.z = goalLineZ;
                    player.PlayerBody.transform.position = pos;
                    if (player.PlayerBody.Rigidbody != null)
                    {
                        player.PlayerBody.Rigidbody.position = pos;
                        Vector3 v = player.PlayerBody.Rigidbody.linearVelocity;
                        if (v.z < 0f) player.PlayerBody.Rigidbody.linearVelocity = new Vector3(v.x, v.y, 0f);
                    }
                }
                else if (pTeam == PlayerTeam.Red && pos.z > -goalLineZ)
                {
                    pos.z = -goalLineZ;
                    player.PlayerBody.transform.position = pos;
                    if (player.PlayerBody.Rigidbody != null)
                    {
                        player.PlayerBody.Rigidbody.position = pos;
                        Vector3 v = player.PlayerBody.Rigidbody.linearVelocity;
                        if (v.z > 0f) player.PlayerBody.Rigidbody.linearVelocity = new Vector3(v.x, v.y, 0f);
                    }
                }
            }
        }

        private static void OnGamePhaseChanged(Puck disc, GamePhase prevPhase, GamePhase newPhase)
        {
            Level_GrassStadiumSetup_Patch.MoveSpawnPositionsToGoalLines();

            if (newPhase == GamePhase.Warmup)
            {
                UltimateRulesManager.ResetPlayState();
                if (MonoBehaviourSingleton<PlayerManager>.Instance != null)
                {
                    foreach (var p in MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(false))
                    {
                        if (p != null && p.IsCharacterSpawned && p.PlayerBody != null)
                            p.PlayerBody.Server_Unfreeze();
                    }
                }
                if (disc != null && disc.Rigidbody != null)
                {
                    disc.Rigidbody.isKinematic = false;
                    disc.Server_Unfreeze();
                }
                return;
            }

            if (newPhase == GamePhase.FaceOff)
            {
                UltimateRulesManager.ResetPlayState();
                SetupPullDiscAndTeam(disc, isPlayStart: false);
                EnforceGoalLineLineup(disc);
                UltimateRulesManager.BroadcastAnnouncement($"Line up on the Goal Lines! {UltimateRulesManager.PullingTeam} will Pull!", "#ffe97f");
            }
            else if (newPhase == GamePhase.Play)
            {
                SetupPullDiscAndTeam(disc, isPlayStart: true);

                if (MonoBehaviourSingleton<PlayerManager>.Instance != null)
                {
                    foreach (var p in MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(false))
                    {
                        if (p.IsCharacterSpawned && p.PlayerBody != null)
                            p.PlayerBody.Server_Unfreeze();
                    }
                }

                UltimateRulesManager.BroadcastAnnouncement($"PULL! {UltimateRulesManager.PullingTeam}: Pick up the disc behind your line and Pull!", "#2ecc71");
            }
        }

        private static void EnforceGoalLineLineup(Puck disc)
        {
            if (MonoBehaviourSingleton<PlayerManager>.Instance == null) return;

            var players = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(false);
            int blueIdx = 0, redIdx = 0;

            foreach (var player in players)
            {
                if (!player.IsCharacterSpawned || player.PlayerBody == null) continue;

                bool isBlue = (UltimateRulesManager.GetTeam(player) == PlayerTeam.Blue);
                float teamZ = isBlue ? UltimateRulesManager.EndzoneZThreshold : -UltimateRulesManager.EndzoneZThreshold;
                int idx = isBlue ? blueIdx++ : redIdx++;
                float xOffset = (idx - 1.5f) * 4.0f;

                Vector3 lineupPos = new Vector3(xOffset, 0.25f, teamZ);
                Quaternion lookCenter = Quaternion.LookRotation(new Vector3(0f, 0f, -Mathf.Sign(teamZ)), Vector3.up);

                if (Mathf.Abs(player.PlayerBody.transform.position.z - teamZ) > 1.5f)
                {
                    player.PlayerBody.Server_Teleport(lineupPos, lookCenter);
                }
                player.PlayerBody.Server_Freeze((RigidbodyConstraints)10);
            }

            float pullDiscZ = (UltimateRulesManager.PullingTeam == PlayerTeam.Blue)
                ? (UltimateRulesManager.EndzoneZThreshold + 4.0f)
                : -(UltimateRulesManager.EndzoneZThreshold + 4.0f);

            FlattenAndFreezeDiscOnGround(disc, disc.Rigidbody, new Vector3(0f, UltimateVisualTuning.DiscHalfThickness + 0.01f, pullDiscZ));
        }

        private static void AnnounceTurnover(bool isOutOfBounds, Vector3 outOfBoundsCrossingSpot)
        {
            if (UltimateRulesManager.IsPullPlay || UltimateRulesManager.IsCurrentThrowPull)
            {
                UltimateRulesManager.IsPullPlay = false;

                PlayerTeam deflectorTeam = UltimateRulesManager.GetTeam(UltimateRulesManager.LastDeflector);
                if (UltimateRulesManager.WasTippedInFlight &&
                    UltimateRulesManager.LastDeflector != null &&
                    deflectorTeam != UltimateRulesManager.PullingTeam)
                {
                    UltimateRulesManager.LastTurnoverTeam = deflectorTeam;
                    UltimateRulesManager.OffensiveTeam = UltimateRulesManager.PullingTeam;
                    UltimateRulesManager.BroadcastAnnouncement($"DROPPED PULL by {UltimateRulesManager.LastDeflector.Username.Value}! TURNOVER -> {UltimateRulesManager.OffensiveTeam} Disc!", "#e74c3c");
                    return;
                }

                if (UltimateRulesManager.LastThrower != null)
                {
                    PlayerTeam throwerTeam = UltimateRulesManager.GetTeam(UltimateRulesManager.LastThrower);
                    UltimateRulesManager.LastTurnoverTeam = throwerTeam;
                    UltimateRulesManager.OffensiveTeam = Utils.GetOpposingTeam(throwerTeam) ?? PlayerTeam.None;
                }

                if (isOutOfBounds)
                {
                    Vector3 pullCenterSpot = GetOutOfBoundsPullSpot(outOfBoundsCrossingSpot, UltimateRulesManager.OffensiveTeam);
                    UltimateRulesManager.BroadcastAnnouncement($"PULL OUT OF BOUNDS! Centered at (X=0, Z={pullCenterSpot.z:F0}m) for {UltimateRulesManager.OffensiveTeam}!", "#ffe97f");
                }
                return;
            }

            if (UltimateRulesManager.LastThrower != null)
            {
                PlayerTeam throwerTeam = UltimateRulesManager.GetTeam(UltimateRulesManager.LastThrower);
                UltimateRulesManager.LastTurnoverTeam = throwerTeam;
                PlayerTeam? newOffense = Utils.GetOpposingTeam(throwerTeam);
                UltimateRulesManager.OffensiveTeam = newOffense ?? PlayerTeam.None;
                string reason = isOutOfBounds ? "OUT OF BOUNDS!" : "TURNOVER!";
                UltimateRulesManager.BroadcastAnnouncement($"{reason} {UltimateRulesManager.OffensiveTeam} Disc!", "#e74c3c");
            }
        }

        private static Vector3 GetOutOfBoundsPullSpot(Vector3 crossingSpot, PlayerTeam receivingTeam)
        {
            float goalLineZ = UltimateRulesManager.EndzoneZThreshold;
            float brickZ = (receivingTeam == PlayerTeam.Blue) ? UltimateRulesManager.BrickMarkZ : -UltimateRulesManager.BrickMarkZ;

            if (receivingTeam == PlayerTeam.Blue && crossingSpot.z >= goalLineZ)
            {
                return new Vector3(0f, UltimateVisualTuning.DiscHalfThickness + 0.01f, brickZ);
            }
            if (receivingTeam == PlayerTeam.Red && crossingSpot.z <= -goalLineZ)
            {
                return new Vector3(0f, UltimateVisualTuning.DiscHalfThickness + 0.01f, brickZ);
            }

            float centeredZ = Mathf.Clamp(crossingSpot.z, -goalLineZ, goalLineZ);
            return new Vector3(0f, UltimateVisualTuning.DiscHalfThickness + 0.01f, centeredZ);
        }

        private static void ResolveRimGroundContactAndTumble(Puck disc, Rigidbody rb, float minCenterY)
        {
            Vector3 pos = disc.transform.position;
            Quaternion rot = disc.transform.rotation;

            if (pos.y < minCenterY)
            {
                pos.y = minCenterY;
                disc.transform.position = pos;
                rb.position = pos;
            }

            Vector3 vel = rb.linearVelocity;
            Vector3 horizVel = new Vector3(vel.x, 0f, vel.z);
            float horizSpeed = horizVel.magnitude;

            Vector3 discUp = rot * Vector3.up;
            Vector3 downInDiscPlane = Vector3.ProjectOnPlane(Vector3.down, discUp);
            Vector3 lowestRimOffset = (downInDiscPlane.sqrMagnitude > 0.0001f)
                ? downInDiscPlane.normalized * UltimateVisualTuning.DiscWorldRadius
                : Vector3.zero;

            float rimTiltAngle = Vector3.Angle(discUp, Vector3.up);
            if (rimTiltAngle > 90f) rimTiltAngle = 180f - rimTiltAngle;

            if (vel.y < -0.1f)
            {
                float restitution = Mathf.Lerp(0.18f, 0.42f, Mathf.Clamp01(rimTiltAngle / 55f));
                vel.y = Mathf.Max(0.15f, -vel.y * restitution);

                if (horizSpeed > 1.8f && rimTiltAngle > 8f)
                {
                    Vector3 rimContactTorque = Vector3.Cross(lowestRimOffset, Vector3.up * (horizSpeed * 0.65f));
                    Vector3 tumbleAxis = Vector3.Cross(Vector3.up, horizVel.normalized);
                    Vector3 forwardTumbleTorque = tumbleAxis * (horizSpeed * Mathf.Sin(rimTiltAngle * Mathf.Deg2Rad) * 1.35f);

                    rb.AddTorque(rimContactTorque + forwardTumbleTorque, ForceMode.VelocityChange);
                    vel.x *= 0.78f;
                    vel.z *= 0.78f;
                }
                else
                {
                    vel.x *= 0.86f;
                    vel.z *= 0.86f;
                }
                if (horizSpeed > 2.0f && Mathf.Abs(vel.y) > 0.35f)
                {
                    float hitVol = Mathf.Clamp01(horizSpeed / 14f) * 0.65f;
                    UltimateAudioManager.ServerBroadcastSound(UltimateSoundType.GroundHit, pos, hitVol, UnityEngine.Random.Range(0.9f, 1.1f));
                }
                rb.linearVelocity = vel;
            }
            else
            {
                float turfFriction = (rimTiltAngle > 25f) ? 2.2f : 4.2f;
                rb.linearVelocity = new Vector3(
                    Mathf.MoveTowards(vel.x, 0f, turfFriction * Time.fixedDeltaTime),
                    Mathf.Max(0f, vel.y),
                    Mathf.MoveTowards(vel.z, 0f, turfFriction * Time.fixedDeltaTime)
                );

                if (horizSpeed < 3.5f && rimTiltAngle > 2f)
                {
                    Vector3 targetUp = (discUp.y >= 0f) ? Vector3.up : Vector3.down;
                    Vector3 flattenTorque = Vector3.Cross(discUp, targetUp) * (12.0f * Time.fixedDeltaTime);
                    rb.angularVelocity = (rb.angularVelocity + flattenTorque) * (1f - 3.5f * Time.fixedDeltaTime);
                }
            }
        }

        public static void EnsureValidPullingTeam()
        {
            if (MonoBehaviourSingleton<PlayerManager>.Instance == null) return;

            if (UltimateRulesManager.PullingTeam != PlayerTeam.Blue && UltimateRulesManager.PullingTeam != PlayerTeam.Red)
            {
                UltimateRulesManager.PullingTeam = PlayerTeam.Blue;
            }

            var teamPlayers = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(false)
                .Where(p => p != null && (UltimateRulesManager.GetTeam(p) == PlayerTeam.Blue || UltimateRulesManager.GetTeam(p) == PlayerTeam.Red))
                .ToList();

            if (teamPlayers.Count == 0) return;

            bool pullingTeamHasPlayers = teamPlayers.Any(p => UltimateRulesManager.GetTeam(p) == UltimateRulesManager.PullingTeam);
            if (!pullingTeamHasPlayers)
            {
                UltimateRulesManager.PullingTeam = UltimateRulesManager.GetTeam(teamPlayers[0]);
            }
        }

        public static void SetupPullDiscAndTeam(Puck disc, bool isPlayStart)
        {
            if (disc == null || disc.Rigidbody == null) return;

            EnsureValidPullingTeam();

            UltimateRulesManager.IsPullPlay = true;
            UltimateRulesManager.IsCurrentThrowPull = false;
            UltimateRulesManager.OffensiveTeam = UltimateRulesManager.PullingTeam;
            UltimateRulesManager.LastTurnoverTeam = PlayerTeam.None;
            UltimateRulesManager.LastThrower = null;
            UltimateRulesManager.SecondLastThrower = null;
            UltimateRulesManager.Holder = null;
            UltimateRulesManager.IsHolderPivotLocked = false;
            UltimateRulesManager.IsDiscInFlight = false;
            UltimateRulesManager.IsDiscTumblingOnGround = false;
            UltimateRulesManager.WentOutOfBoundsInFlight = false;
            UltimateNetworkSync.ServerBroadcastHolderState(null, 1, disc);

            float pullDiscZ = (UltimateRulesManager.PullingTeam == PlayerTeam.Blue)
                ? (UltimateRulesManager.EndzoneZThreshold + 4.0f)
                : -(UltimateRulesManager.EndzoneZThreshold + 4.0f);

            FlattenAndFreezeDiscOnGround(disc, disc.Rigidbody, new Vector3(0f, UltimateVisualTuning.DiscHalfThickness + 0.01f, pullDiscZ));
        }

        private static Vector3 GetLegalFieldSpotAfterTurnover(Vector3 rawSpot)
        {
            float maxX = UltimateRulesManager.FieldHalfWidth - 0.5f;
            float goalLineZ = UltimateRulesManager.EndzoneZThreshold;

            float legalX = Mathf.Clamp(rawSpot.x, -maxX, maxX);
            float legalZ = Mathf.Clamp(rawSpot.z, -goalLineZ, goalLineZ);

            return new Vector3(legalX, UltimateVisualTuning.DiscHalfThickness + 0.01f, legalZ);
        }

        private static void FlattenAndFreezeDiscOnGround(Puck disc, Rigidbody rb, Vector3 groundSpot)
        {
            groundSpot.y = Mathf.Max(groundSpot.y, UltimateVisualTuning.DiscHalfThickness + 0.01f);
            Quaternion flatRot = Quaternion.Euler(0f, disc.transform.eulerAngles.y, 0f);
            rb.isKinematic = false;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.position = groundSpot;
            rb.rotation = flatRot;
            disc.transform.SetPositionAndRotation(groundSpot, flatRot);
            disc.Server_Freeze(RigidbodyConstraints.FreezeAll);
        }

        private static void ExecuteStaminaThrow(
            Puck disc,
            Player thrower,
            UltimateRulesManager.ThrowWindupTracker tracker,
            Vector2 releaseStickAngle,
            Vector3 aimForward,
            UltimateThrowType throwType,
            float hyzerAngle,
            float spinSign)
        {
            Rigidbody rb = disc.Rigidbody;
            thrower.PlayerBody.Server_Unfreeze();
            rb.isKinematic = false;
            disc.Server_Unfreeze();

            float swingBonus = Mathf.Clamp01(tracker.SmoothedSwingPowerBonus);
            float combinedPower = tracker.GetCombinedThrowPower();
            tracker.Reset(thrower.PlayerBody);

            float powerCurve = Mathf.Pow(combinedPower, 1.15f);
            float launchSpeed = Mathf.Lerp(6.5f, 24.0f, powerCurve);

            float launchPitchDeg = Mathf.Clamp((22f - releaseStickAngle.x) * 0.55f, -6f, 32f);
            if (throwType == UltimateThrowType.Hammer)
            {
                launchPitchDeg = Mathf.Max(launchPitchDeg, 20f);
            }

            Quaternion aimYawRot = Quaternion.LookRotation(aimForward, Vector3.up);
            Vector3 launchDir = (aimYawRot * Quaternion.Euler(-launchPitchDeg, 0f, 0f)) * Vector3.forward;
            launchDir.Normalize();

            float discHorizonPitchDeg = Mathf.Clamp(launchPitchDeg * 0.45f + 2.0f, -4f, 16f);
            float initialRollDeg = (throwType == UltimateThrowType.Hammer)
                ? 150f * spinSign
                : Mathf.Clamp(hyzerAngle, -75f, 75f);

            Quaternion launchRot = aimYawRot * Quaternion.Euler(-discHorizonPitchDeg, 0f, initialRollDeg);

            Vector3 launchPos = disc.transform.position;
            float minSafeY = ClientDiscFollower.GetMinimumCenterHeightForRotation(launchRot) + 0.15f;
            if (launchPos.y < minSafeY) launchPos.y = minSafeY;

            disc.transform.SetPositionAndRotation(launchPos, launchRot);
            rb.position = launchPos;
            rb.rotation = launchRot;

            rb.linearVelocity = launchDir * launchSpeed;
            rb.angularVelocity = Vector3.zero;

            UltimateRulesManager.ActiveFlightThrowType = throwType;
            UltimateRulesManager.ActiveFlightSpinSign = spinSign;
            UltimateRulesManager.SecondLastThrower = UltimateRulesManager.LastThrower;
            UltimateRulesManager.LastThrower = thrower;
            UltimateRulesManager.Holder = null;
            UltimateRulesManager.IsHolderPivotLocked = false;
            UltimateRulesManager.IsDiscInFlight = true;
            UltimateRulesManager.IsDiscTumblingOnGround = false;
            UltimateRulesManager.WasTippedInFlight = false;
            UltimateRulesManager.WasDefensiveBlock = false;
            UltimateRulesManager.LastDeflector = null;
            UltimateRulesManager.WentOutOfBoundsInFlight = false;
            UltimateRulesManager.LastInBoundsFlightPos = launchPos;
            UltimateRulesManager.ReleaseTime = Time.time;
            UltimateRulesManager.StallCount = 0f;
            UltimateRulesManager.LastStallAnnounced = 0;

            sbyte spinByte = (sbyte)(spinSign >= 0f ? 1 : -1);
            UltimateNetworkSync.ServerBroadcastHolderState(null, spinByte, disc);

            float throwVol = Mathf.Lerp(0.55f, 1.0f, combinedPower);
            float throwPitch = Mathf.Lerp(0.85f, 1.28f, combinedPower);
            UltimateAudioManager.ServerBroadcastSound(UltimateSoundType.Throw, launchPos, throwVol, throwPitch);

            if (UltimateRulesManager.IsPullPlay)
            {
                PlayerTeam throwerTeam = UltimateRulesManager.GetTeam(thrower);
                UltimateRulesManager.IsCurrentThrowPull = true;
                UltimateRulesManager.IsPullPlay = false;
                UltimateRulesManager.OffensiveTeam = Utils.GetOpposingTeam(throwerTeam) ?? PlayerTeam.None;
                UltimateRulesManager.LastTurnoverTeam = throwerTeam;
                UltimateRulesManager.BroadcastAnnouncement($"{thrower.Username.Value} pulls ({throwType}, {Mathf.RoundToInt(combinedPower * 100f)}% power, {Mathf.RoundToInt(swingBonus * 100f)}% whip)!", "#ffe97f");
            }
            else
            {
                UltimateRulesManager.IsCurrentThrowPull = false;
            }
        }

        private static void ApplyAirborneDeflection(Puck disc, Rigidbody rb, Player deflector, Vector3 contactCenter, bool isHandTip, float bladeFactor = 0.5f)
        {
            bool isDefensivePlay = UltimateRulesManager.LastThrower != null &&
                                   UltimateRulesManager.AreOpponents(deflector, UltimateRulesManager.LastThrower);

            UltimateRulesManager.WasTippedInFlight = true;
            UltimateRulesManager.WasDefensiveBlock = isDefensivePlay;
            UltimateRulesManager.LastDeflector = deflector;
            UltimateRulesManager.LastDeflectionTime = Time.time;

            Vector3 discPos = disc.transform.position;
            Vector3 incomingVel = rb.linearVelocity;
            float incomingSpeed = incomingVel.magnitude;

            Vector3 bounceNormal = (discPos - contactCenter);
            if (bounceNormal.sqrMagnitude < 0.001f)
                bounceNormal = -incomingVel.normalized;
            bounceNormal.Normalize();

            Vector3 playerVel = (deflector.PlayerBody != null && deflector.PlayerBody.Rigidbody != null)
                ? deflector.PlayerBody.Rigidbody.linearVelocity
                : Vector3.zero;

            Vector3 newVel;
            if (isDefensivePlay)
            {
                // DEFENSIVE HAND / BODY BLOCK: Kill forward momentum and slam the disc downward to the turf
                // so it never pops back into the thrower's hands for a free stall reset!
                Vector3 lateralScatter = new Vector3(bounceNormal.x, 0f, bounceNormal.z);
                if (lateralScatter.sqrMagnitude < 0.001f) lateralScatter = deflector.PlayerBody.transform.forward;
                lateralScatter.Normalize();

                // If blocking a point-blank throw, push away from the thrower
                if (UltimateRulesManager.LastThrower != null && UltimateRulesManager.LastThrower.PlayerBody != null)
                {
                    Vector3 awayFromThrower = (discPos - UltimateRulesManager.LastThrower.PlayerBody.transform.position);
                    awayFromThrower.y = 0f;
                    if (awayFromThrower.sqrMagnitude > 0.01f)
                    {
                        lateralScatter = (lateralScatter + awayFromThrower.normalized * 1.2f).normalized;
                    }
                }

                float deadenedHorizSpeed = Mathf.Clamp(incomingSpeed * 0.18f, 1.0f, 3.0f);
                float downwardPlummetSpeed = isHandTip ? -5.5f : -4.5f;

                newVel = lateralScatter * deadenedHorizSpeed
                         + new Vector3(playerVel.x, 0f, playerVel.z) * 0.20f
                         + Vector3.up * downwardPlummetSpeed;
            }
            else if (isHandTip)
            {
                // Offensive teammate bobble / tip
                Vector3 carryDir = incomingVel.sqrMagnitude > 0.1f ? incomingVel.normalized : bounceNormal;
                float horizCarry = Mathf.Clamp(incomingSpeed * Mathf.Lerp(0.22f, 0.40f, bladeFactor), 1.2f, 4.2f);
                newVel = new Vector3(carryDir.x, 0f, carryDir.z).normalized * horizCarry
                         + bounceNormal * (1.2f * bladeFactor)
                         + playerVel * 0.45f
                         + Vector3.up * Mathf.Lerp(2.2f, 3.2f, bladeFactor);
            }
            else
            {
                Vector3 horizNormal = new Vector3(bounceNormal.x, 0f, bounceNormal.z);
                if (horizNormal.sqrMagnitude < 0.001f) horizNormal = deflector.PlayerBody.transform.forward;
                horizNormal.Normalize();

                float bounceSpeed = Mathf.Clamp(incomingSpeed * 0.28f, 1.5f, 4.0f);
                newVel = (horizNormal * bounceSpeed) + (playerVel * 0.35f) + (Vector3.up * 1.2f);
            }

            disc.transform.position = discPos + bounceNormal * 0.10f;
            rb.position = disc.transform.position;
            rb.linearVelocity = newVel;

            // Steeply tilt the disc on a defensive block so aerodynamics lose lift and it tumbles onto the grass
            Quaternion tipTilt = isDefensivePlay
                ? Quaternion.Euler(UnityEngine.Random.Range(40f, 68f), disc.transform.eulerAngles.y, UnityEngine.Random.Range(-55f, 55f))
                : Quaternion.Slerp(disc.transform.rotation, Quaternion.Euler(0f, disc.transform.eulerAngles.y, 0f), 0.55f);
            rb.MoveRotation(tipTilt);

            float blockPitch = isHandTip ? UnityEngine.Random.Range(1.02f, 1.15f) : UnityEngine.Random.Range(0.82f, 0.94f);
            UltimateAudioManager.ServerBroadcastSound(UltimateSoundType.BlockTip, discPos, 0.95f, blockPitch);

            if (!UltimateRulesManager.IsWarmup)
            {
                string playType = isHandTip
                    ? (isDefensivePlay ? "HAND BLOCK" : "BOBBLED")
                    : (isDefensivePlay ? "BODY BLOCK" : "BODY DEFLECTION");
                UltimateRulesManager.BroadcastAnnouncement($"{playType} by #{deflector.Number.Value} {deflector.Username.Value}!", "#f39c12");
            }
        }

        private static void HandlePlayerCatch(Puck disc, Player catcher)
        {
            bool wasInFlight = UltimateRulesManager.IsDiscInFlight && !UltimateRulesManager.IsDiscTumblingOnGround;
            bool wasTipped = UltimateRulesManager.WasTippedInFlight;
            bool wasPull = UltimateRulesManager.IsCurrentThrowPull;
            Player previousThrower = UltimateRulesManager.LastThrower;
            Vector3 catcherPos = catcher.PlayerBody.transform.position;
            PlayerTeam catcherTeam = UltimateRulesManager.GetTeam(catcher);
            PlayerTeam prevThrowerTeam = UltimateRulesManager.GetTeam(previousThrower);

            if (!UltimateRulesManager.IsWarmup && wasInFlight && UltimateRulesManager.IsPositionOutOfBounds(catcherPos))
            {
                UltimateRulesManager.IsDiscInFlight = false;
                UltimateRulesManager.IsDiscTumblingOnGround = false;
                UltimateRulesManager.IsCurrentThrowPull = false;

                Vector3 crossingSpot = UltimateRulesManager.WentOutOfBoundsInFlight
                    ? UltimateRulesManager.LastInBoundsFlightPos
                    : catcherPos;

                if (wasPull)
                {
                    UltimateRulesManager.OffensiveTeam = catcherTeam;
                    UltimateRulesManager.LastTurnoverTeam = UltimateRulesManager.PullingTeam;
                    Vector3 pullSpot = GetOutOfBoundsPullSpot(crossingSpot, catcherTeam);
                    UltimateRulesManager.BroadcastAnnouncement($"PULL CAUGHT OUT OF BOUNDS! Centered at (X=0, Z={pullSpot.z:F0}m)!", "#ffe97f");
                    FlattenAndFreezeDiscOnGround(disc, disc.Rigidbody, pullSpot);
                }
                else
                {
                    UltimateRulesManager.LastTurnoverTeam = previousThrower != null ? prevThrowerTeam : catcherTeam;
                    UltimateRulesManager.OffensiveTeam = Utils.GetOpposingTeam(UltimateRulesManager.LastTurnoverTeam) ?? PlayerTeam.None;
                    UltimateRulesManager.BroadcastAnnouncement($"CAUGHT OUT OF BOUNDS by #{catcher.Number.Value} {catcher.Username.Value}! {UltimateRulesManager.OffensiveTeam} Disc!", "#e74c3c");
                    FlattenAndFreezeDiscOnGround(disc, disc.Rigidbody, GetLegalFieldSpotAfterTurnover(crossingSpot));
                }
                return;
            }

            UltimateRulesManager.Holder = catcher;
            UltimateRulesManager.CatchTime = Time.time;
            UltimateRulesManager.CatchInitialSpot = catcherPos;
            UltimateRulesManager.CaughtInCentralZone = Mathf.Abs(catcherPos.z) < UltimateRulesManager.EndzoneZThreshold;
            UltimateRulesManager.StallCount = 0f;
            UltimateRulesManager.LastStallAnnounced = 0;
            UltimateRulesManager.ActiveMarker = null;
            UltimateRulesManager.IsDiscInFlight = false;
            UltimateRulesManager.IsDiscTumblingOnGround = false;
            UltimateRulesManager.WasTippedInFlight = false;
            UltimateRulesManager.WasDefensiveBlock = false;
            UltimateRulesManager.LastDeflector = null;
            UltimateRulesManager.LastTurnoverTeam = PlayerTeam.None;

            if (!UltimateRulesManager.WindupTrackers.TryGetValue(catcher.OwnerClientId, out var tracker))
            {
                tracker = new UltimateRulesManager.ThrowWindupTracker();
                UltimateRulesManager.WindupTrackers[catcher.OwnerClientId] = tracker;
            }
            // Automatically ignores Shift if the player was sprinting into the catch/pickup!
            tracker.OnCatchOrPickup(catcher);

            // Determine whether the catcher has running momentum to carry over
            Rigidbody catcherRb = catcher.PlayerBody.Rigidbody;
            Vector3 currentVel = catcherRb != null ? catcherRb.linearVelocity : Vector3.zero;
            float horizSpeed = new Vector2(currentVel.x, currentVel.z).magnitude;

            if (horizSpeed > 0.65f)
            {
                // Allow a smooth 2-3 step deceleration slide before locking pivot!
                UltimateRulesManager.IsHolderPivotLocked = false;
                if (catcherRb != null)
                {
                    catcherRb.linearVelocity = new Vector3(currentVel.x * 0.85f, currentVel.y, currentVel.z * 0.85f);
                }
            }
            else
            {
                UltimateRulesManager.IsHolderPivotLocked = true;
                catcher.PlayerBody.Server_Freeze((RigidbodyConstraints)10);
                if (catcherRb != null) catcherRb.linearVelocity = Vector3.zero;
            }

            disc.Rigidbody.isKinematic = true;
            disc.Server_Freeze(RigidbodyConstraints.FreezeAll);
            UltimateNetworkSync.ServerBroadcastHolderState(catcher, 1, disc);

            Vector3 catchSoundPos = UltimateRulesManager.GetHandWorldPosition(catcher.Stick);
            if (wasInFlight)
            {
                UltimateAudioManager.ServerBroadcastSound(UltimateSoundType.Catch, catchSoundPos, 0.95f, UnityEngine.Random.Range(0.94f, 1.06f));
            }
            else
            {
                UltimateAudioManager.ServerBroadcastSound(UltimateSoundType.Pickup, catchSoundPos, 0.70f, UnityEngine.Random.Range(0.95f, 1.05f));
            }

            if (UltimateRulesManager.IsPullPlay && !wasInFlight && UltimateRulesManager.LastThrower == null)
            {
                UltimateRulesManager.OffensiveTeam = catcherTeam;
                UltimateRulesManager.BroadcastAnnouncement($"#{catcher.Number.Value} {catcher.Username.Value} picked up the disc to Pull!", "#ffe97f");
                return;
            }

            if (!UltimateRulesManager.IsWarmup && wasInFlight && previousThrower != null)
            {
                if (UltimateRulesManager.AreOpponents(catcher, previousThrower))
                {
                    if (!wasPull)
                    {
                        if (UltimateRulesManager.IsInAttackingEndzone(catcher))
                        {
                            UltimateRulesManager.BroadcastAnnouncement($"CALLAHAN GOAL! Intercepted in the Endzone by #{catcher.Number.Value} {catcher.Username.Value}!", "#9b59b6");
                            UltimateRulesManager.TriggerGoal(catcherTeam, catcher, null, null, disc);
                            return;
                        }

                        UltimateRulesManager.BroadcastAnnouncement($"INTERCEPTION by #{catcher.Number.Value} {catcher.Username.Value}!", "#2ecc71");
                    }
                    else
                    {
                        UltimateRulesManager.BroadcastAnnouncement($"PULL CAUGHT by #{catcher.Number.Value} {catcher.Username.Value}!", "#2ecc71");
                    }

                    UltimateRulesManager.OffensiveTeam = catcherTeam;
                    UltimateRulesManager.LastThrower = null;
                    UltimateRulesManager.SecondLastThrower = null;
                    UltimateRulesManager.IsPullPlay = false;
                    UltimateRulesManager.IsCurrentThrowPull = false;

                    if (UltimateRulesManager.IsHolderPivotLocked)
                    {
                        WalkHolderToGoalLineIfInEndzone(catcher);
                    }
                    return;
                }
                else if (!UltimateRulesManager.IsPullPlay && !wasPull)
                {
                    if (wasTipped)
                    {
                        UltimateRulesManager.BroadcastAnnouncement($"SECOND-CHANCE CATCH off the tip by #{catcher.Number.Value} {catcher.Username.Value}!", "#2ecc71");
                    }

                    // In Ultimate, a goal is scored only if the receiver's FIRST point of contact is inside the attacking endzone!
                    if (UltimateRulesManager.IsInAttackingEndzone(catcher))
                    {
                        UltimateRulesManager.BroadcastAnnouncement($"GOAL! Caught in the Endzone by #{catcher.Number.Value} {catcher.Username.Value}!", "#2ecc71");
                        UltimateRulesManager.TriggerGoal(catcherTeam, catcher, previousThrower, UltimateRulesManager.SecondLastThrower, disc);
                        return;
                    }
                }
            }

            UltimateRulesManager.IsPullPlay = false;
            UltimateRulesManager.IsCurrentThrowPull = false;
            UltimateRulesManager.OffensiveTeam = UltimateRulesManager.IsWarmup ? PlayerTeam.None : catcherTeam;

            if (!UltimateRulesManager.IsWarmup && UltimateRulesManager.IsHolderPivotLocked)
            {
                WalkHolderToGoalLineIfInEndzone(catcher);
            }
        }

        private static void WalkHolderToGoalLineIfInEndzone(Player holder)
        {
            if (holder == null || holder.PlayerBody == null) return;
            Vector3 pos = holder.PlayerBody.transform.position;
            float goalLineZ = UltimateRulesManager.EndzoneZThreshold;

            if (Mathf.Abs(pos.z) > goalLineZ)
            {
                float clampedZ = Mathf.Clamp(pos.z, -goalLineZ, goalLineZ);
                float clampedX = Mathf.Clamp(pos.x, -(UltimateRulesManager.FieldHalfWidth - 0.5f), UltimateRulesManager.FieldHalfWidth - 0.5f);
                Vector3 goalLinePivot = new Vector3(clampedX, pos.y, clampedZ);
                holder.PlayerBody.Server_Teleport(goalLinePivot, holder.PlayerBody.transform.rotation);
                holder.PlayerBody.Server_Freeze((RigidbodyConstraints)10);
            }
        }

        private static void ApplyFrisbeeAerodynamics(Puck disc, Rigidbody rb)
        {
            rb.inertiaTensor = new Vector3(0.02f, 0.08f, 0.02f);
            rb.angularVelocity = Vector3.zero;

            Vector3 vel = rb.linearVelocity;
            Vector3 horizVel = new Vector3(vel.x, 0f, vel.z);
            float horizSpeed = horizVel.magnitude;
            float totalSpeed = vel.magnitude;

            if (totalSpeed < 0.35f) return;

            Vector3 velDir = vel / totalSpeed;
            Vector3 horizDir = (horizSpeed > 0.2f) ? (horizVel / horizSpeed) : disc.transform.forward;
            horizDir.y = 0f;
            if (horizDir.sqrMagnitude < 0.001f) horizDir = Vector3.forward;
            horizDir.Normalize();

            Vector3 discNormal = disc.transform.up;
            bool isInverted = discNormal.y < -0.1f;
            Vector3 effectiveNormal = isInverted ? -discNormal : discNormal;

            float sinAlpha = -Vector3.Dot(velDir, effectiveNormal);
            float aoaDeg = Mathf.Asin(Mathf.Clamp(sinAlpha, -1f, 1f)) * Mathf.Rad2Deg;
            float clampedAoA = Mathf.Clamp(aoaDeg, -12f, 24f);

            float gravityMag = Mathf.Abs(Physics.gravity.y);
            float baseGlideRatio = isInverted ? 0.56f : 0.78f;
            float aoaLiftBonus = clampedAoA * 0.018f;
            float speedLiftFactor = Mathf.Clamp01(horizSpeed / 8.5f);

            float verticalLiftAccel = gravityMag * Mathf.Clamp((baseGlideRatio + aoaLiftBonus) * speedLiftFactor, 0.10f, 1.08f);

            if (vel.y > 0.5f)
            {
                verticalLiftAccel *= Mathf.Clamp01(1.0f - (vel.y / 10.0f));
            }
            Vector3 liftForce = Vector3.up * verticalLiftAccel;

            float linearDrag = 0.24f + Mathf.Max(0f, clampedAoA) * 0.012f;
            Vector3 dragForce = -vel * linearDrag;

            Vector3 rightHoriz = Vector3.Cross(Vector3.up, horizDir);
            float bankLateralComponent = Vector3.Dot(effectiveNormal, rightHoriz);
            Vector3 bankCurveForce = rightHoriz * (bankLateralComponent * horizSpeed * 0.48f);

            if (horizSpeed > 0.5f && horizSpeed < 8.5f)
            {
                float spinSign = UltimateRulesManager.ActiveFlightSpinSign;
                Vector3 fadeDir = -rightHoriz * spinSign;
                float fadeStrength = Mathf.Clamp01((8.5f - horizSpeed) / 8.5f) * 1.45f;
                bankCurveForce += fadeDir * fadeStrength;
            }

            rb.AddForce(liftForce + dragForce + bankCurveForce, ForceMode.Acceleration);

            if (horizSpeed > 0.4f)
            {
                Quaternion horizonLook = Quaternion.LookRotation(horizDir, Vector3.up);
                Vector3 localUp = Quaternion.Inverse(horizonLook) * disc.transform.up;

                float currentRollDeg = Mathf.Atan2(-localUp.x, localUp.y) * Mathf.Rad2Deg;
                float currentPitchDeg = Mathf.Atan2(localUp.z, localUp.y) * Mathf.Rad2Deg;

                float targetPitchDeg = Mathf.Lerp(currentPitchDeg, -2.5f, Time.fixedDeltaTime * 1.8f);
                float fadeRollRate = (horizSpeed < 7.5f) ? (-UltimateRulesManager.ActiveFlightSpinSign * 4.5f * Time.fixedDeltaTime) : 0f;
                float targetRollDeg = Mathf.Lerp(currentRollDeg + fadeRollRate, 0f, Time.fixedDeltaTime * 0.15f);

                Quaternion targetFlightRot = horizonLook * Quaternion.Euler(targetPitchDeg, 0f, targetRollDeg);
                rb.MoveRotation(Quaternion.Slerp(disc.transform.rotation, targetFlightRot, Time.fixedDeltaTime * 8.0f));
            }
        }
    }

    // =========================================================================
    // 9. REMOVE RINK BOARDS/GLASS & SPAWN GRASS STADIUM FIELD (Level)
    // =========================================================================
    [HarmonyPatch(typeof(Goal), "Server_OnPuckEnterGoal")]
    public static class Goal_DisableHockeyGoalTrigger_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(Level), "OnNetworkPostSpawn")]
    public static class Level_GrassStadiumSetup_Patch
    {
        private static int _cachedBarrierLayer = -1;
        private static PhysicsMaterial _cachedBarrierMaterial = null;
        private static PhysicsMaterial _cachedIceMaterial = null;

        [HarmonyPostfix]
        public static void Postfix(Level __instance)
        {
            try
            {
                UltimateNetworkSync.EnsureRegistered();
                UltimateAudioManager.EnsureNetworkRegistered();
                UltimateDiveMod.EnsureNetworkRegistered();

                if (__instance == null) return;

                // 1. Expand Level.Bounds so cameras/minimap/spectators know the field is 350x350m
                __instance.Bounds = new Bounds(Vector3.zero, new Vector3(350f, 50f, 350f));

                int iceLayer = LayerMask.NameToLayer("Ice");
                if (iceLayer < 0) iceLayer = 0;
                float iceSurfaceY = 0f;

                Transform existingGrassRoot = __instance.transform.Find("UltimateGrassFieldRoot");

                // 2. Sweep ALL objects in the scene:
                var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
                foreach (var obj in allObjects)
                {
                    if (obj == null || !obj.scene.IsValid()) continue;

                    if (existingGrassRoot != null && (obj.transform == existingGrassRoot || obj.transform.IsChildOf(existingGrassRoot)))
                        continue;

                    if (obj.name.Contains("Ultimate") || obj.name.Contains("GrassTurf") || obj.name.Contains("GrassField"))
                        continue;

                    if (obj.GetComponentInParent<Player>() != null || obj.GetComponentInParent<PlayerBody>() != null) continue;
                    if (obj.GetComponentInParent<Puck>() != null || obj.GetComponentInParent<Stick>() != null) continue;

                    string nameLower = obj.name.ToLowerInvariant();

                    // A. Capture Ice layer, material, and surface height from vanilla Ice, then disable its small rink collider
                    // so our massive 350m x 350m un-rotated Ice floor handles 100% of ground physics without any edge drop-offs!
                    if (obj.name == "Ice" || obj.name == "Ice Bottom" || obj.name == "Ice Top" || obj.layer == iceLayer)
                    {
                        if (obj.layer != 0) iceLayer = obj.layer;

                        foreach (var rend in obj.GetComponentsInChildren<Renderer>(true))
                            rend.enabled = false;

                        foreach (var col in obj.GetComponentsInChildren<Collider>(true))
                        {
                            if (col.sharedMaterial != null && _cachedIceMaterial == null)
                                _cachedIceMaterial = col.sharedMaterial;
                            if (Mathf.Abs(col.bounds.max.y) < 5f)
                                iceSurfaceY = col.bounds.max.y;

                            // Disable the vanilla rink-sized Ice collider so its edge at +/-30m never trips players!
                            col.enabled = false;
                        }
                        continue;
                    }

                    // B. Capture the exact physics layer & material used by the vanilla Rink Barriers/Boards before hiding them!
                    bool isRinkStructure = nameLower.Contains("barrier") || nameLower.Contains("border") ||
                                           nameLower.Contains("glass") || nameLower.Contains("board") ||
                                           nameLower.Contains("goal") || nameLower.Contains("net") ||
                                           nameLower.Contains("roof") || nameLower.Contains("ceiling") ||
                                           nameLower.Contains("bleacher") || nameLower.Contains("rink") ||
                                           nameLower.Contains("arena") || nameLower.Contains("stadium") ||
                                           nameLower.Contains("wall") || nameLower.Contains("pillar") ||
                                           nameLower.Contains("rafter") || nameLower.Contains("truss");

                    if (isRinkStructure)
                    {
                        foreach (var col in obj.GetComponentsInChildren<Collider>(true))
                        {
                            if (col != null && !col.isTrigger && col.gameObject.layer != iceLayer)
                            {
                                if (_cachedBarrierLayer < 0 || col.gameObject.layer != 0)
                                    _cachedBarrierLayer = col.gameObject.layer;
                                if (_cachedBarrierMaterial == null && col.sharedMaterial != null)
                                    _cachedBarrierMaterial = col.sharedMaterial;
                            }
                        }

                        DisableRenderersAndNonGroundColliders(obj, iceSurfaceY, existingGrassRoot);
                    }
                }

                // 3. Also sweep all direct children of Level
                foreach (Transform child in __instance.transform)
                {
                    if (child.name == "UltimateGrassFieldRoot") continue;
                    if (child.name.Contains("Ice")) continue;
                    if (IsCriticalObject(child.gameObject)) continue;

                    DisableRenderersAndNonGroundColliders(child.gameObject, iceSurfaceY, existingGrassRoot);
                }

                int barrierLayer = ResolvePlayerBlockingLayer(iceLayer);

                // 4. Rebuild our 350m x 350m Grass Ultimate Field, Fence Colliders & Painted Lines!
                BuildGrassUltimateField(
                    __instance.transform,
                    iceSurfaceY,
                    iceLayer,
                    barrierLayer,
                    _cachedBarrierMaterial ?? _cachedIceMaterial,
                    _cachedIceMaterial);

                if (!UltimateFrisbeePlugin.IsHeadlessServer)
                {
                    SetupOutdoorSunnyLighting(__instance.transform);
                }
                MoveSpawnPositionsToGoalLines();
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"Error building Grass Ultimate Field: {ex}");
            }
        }

        private static int ResolvePlayerBlockingLayer(int fallbackIceLayer)
        {
            if (_cachedBarrierLayer >= 0) return _cachedBarrierLayer;

            string[] candidateNames = { "Boards", "Barrier", "Wall", "Walls", "Glass", "Border", "Rink", "Environment", "Level", "Default" };
            foreach (string name in candidateNames)
            {
                int l = LayerMask.NameToLayer(name);
                if (l > 0) return l;
            }
            return fallbackIceLayer;
        }

        public static string MapHockeyPositionNameToNumber(string rawName, int fallbackIndex)
        {
            if (!string.IsNullOrEmpty(rawName))
            {
                switch (rawName.Trim().ToUpperInvariant())
                {
                    case "RW":
                    case "1": return "1";
                    case "RD":
                    case "2": return "2";
                    case "C":
                    case "3": return "3";
                    case "LD":
                    case "4": return "4";
                    case "LW":
                    case "5": return "5";
                    case "G":
                    case "6": return "6";
                }
            }
            return ((fallbackIndex % 6) + 1).ToString();
        }

        public static void MoveSpawnPositionsToGoalLines()
        {
            PlayerPosition[] allPositions = UnityEngine.Object.FindObjectsByType<PlayerPosition>(FindObjectsSortMode.None);
            UIPositionSelect uiPosSelect = UnityEngine.Object.FindFirstObjectByType<UIPositionSelect>();

            var bluePositions = new List<PlayerPosition>();
            var redPositions = new List<PlayerPosition>();

            foreach (PlayerPosition pp in allPositions)
            {
                if (pp == null) continue;

                string fullName = (pp.transform.parent != null ? pp.transform.parent.name + "_" : "") + pp.name;
                string lower = fullName.ToLowerInvariant();

                bool isBlue = (pp.Team == PlayerTeam.Blue) ||
                              (pp.Team != PlayerTeam.Red && (lower.Contains("blue") || (!lower.Contains("red") && pp.transform.position.z >= 0f)));

                if (isBlue) bluePositions.Add(pp);
                else redPositions.Add(pp);
            }

            // Sort each team's positions into a consistent 1..6 order
            int GetSortOrder(PlayerPosition p)
            {
                string mapped = MapHockeyPositionNameToNumber(p.Name, 99);
                return int.TryParse(mapped, out int num) ? num : 99;
            }

            bluePositions.Sort((a, b) => GetSortOrder(a).CompareTo(GetSortOrder(b)));
            redPositions.Sort((a, b) => GetSortOrder(a).CompareTo(GetSortOrder(b)));

            void SetupTeamLineup(List<PlayerPosition> teamList, bool isBlue)
            {
                float teamZ = isBlue ? UltimateRulesManager.EndzoneZThreshold : -UltimateRulesManager.EndzoneZThreshold;
                for (int i = 0; i < teamList.Count; i++)
                {
                    PlayerPosition pp = teamList[i];
                    int slotNumber = (i % 6) + 1; // 1, 2, 3, 4, 5, 6

                    // 1. Rename position to 1..6 and force role to Attacker (Skater)
                    pp.Name = slotNumber.ToString();
                    pp.Role = PlayerRole.Attacker;

                    // 2. Line them up left-to-right from the camera's perspective
                    float xOffset = ((i % 6) - 2.5f) * 4.0f;
                    if (isBlue) xOffset = -xOffset; // Blue camera looks toward -Z, so +X is on the left of the screen

                    pp.transform.position = new Vector3(xOffset, 0.1f, teamZ);
                    pp.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0f, isBlue ? -1f : 1f), Vector3.up);

                    // 3. Refresh the UI button label if UIPositionSelect is active
                    if (uiPosSelect != null)
                    {
                        uiPosSelect.StylePosition(pp);
                    }
                }
            }

            SetupTeamLineup(bluePositions, true);
            SetupTeamLineup(redPositions, false);
        }

        private static void DisableRenderersAndNonGroundColliders(GameObject root, float groundY, Transform protectedGrassRoot)
        {
            if (protectedGrassRoot != null && (root.transform == protectedGrassRoot || root.transform.IsChildOf(protectedGrassRoot)))
                return;

            foreach (var cloth in root.GetComponentsInChildren<Cloth>(true))
                cloth.enabled = false;

            foreach (var rend in root.GetComponentsInChildren<Renderer>(true))
            {
                if (protectedGrassRoot != null && rend.transform.IsChildOf(protectedGrassRoot))
                    continue;
                if (!rend.gameObject.name.Contains("Ultimate") && !rend.gameObject.name.Contains("GrassTurf"))
                    rend.enabled = false;
            }

            foreach (var col in root.GetComponentsInChildren<Collider>(true))
            {
                if (protectedGrassRoot != null && col.transform.IsChildOf(protectedGrassRoot))
                    continue;
                if (col.gameObject.name.Contains("GrassTurf"))
                    continue;

                // Disable all vanilla rink/arena colliders so nothing invisible blocks the grass field!
                col.enabled = false;
            }
        }

        private static bool IsCriticalObject(GameObject obj)
        {
            string name = obj.name;
            if (name.Contains("Spawn") || name.Contains("Position") || name.Contains("Audio") ||
                name.Contains("Sound") || name.Contains("Manager") || name.Contains("Camera") ||
                name.Contains("PostProcessing") || name.Contains("Light"))
            {
                return true;
            }
            return false;
        }

        private static void SetupOutdoorSunnyLighting(Transform levelRoot)
        {
            // 1. Force all cameras in the scene to render up to 5,000m away!
            ExtendAllCamerasDrawDistance();

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.68f, 0.72f, 0.75f);

            if (levelRoot.Find("UltimateOutdoorSun") == null)
            {
                GameObject sunObj = new GameObject("UltimateOutdoorSun");
                sunObj.transform.SetParent(levelRoot, false);
                sunObj.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
                Light sun = sunObj.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.color = new Color(1.0f, 0.97f, 0.90f);
                sun.intensity = 1.15f;
                sun.shadows = LightShadows.Soft;
                sun.shadowBias = 0.02f;
                sun.shadowNormalBias = 0.4f;
            }
        }

        public static void ExtendAllCamerasDrawDistance()
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;

            // Prevent LODs (Level of Detail groups) from popping out early
            QualitySettings.lodBias = 5.0f;

            // Disable indoor distance fog if the base game has it enabled
            RenderSettings.fog = false;

            // An array of 32 zeros tells Unity to NOT cull any layer earlier than farClipPlane
            float[] emptyCullDistances = new float[32];

            foreach (Camera cam in Camera.allCameras)
            {
                ApplyCameraLongDistanceSettings(cam, emptyCullDistances);
            }

            foreach (Camera cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                ApplyCameraLongDistanceSettings(cam, emptyCullDistances);
            }
        }

        public static void ApplyCameraLongDistanceSettings(Camera cam, float[] emptyCullDistances = null)
        {
            if (cam == null) return;

            // 1. Extend Far Clip Plane to 20,000 meters so distant mountains never get cut off
            if (cam.farClipPlane < 20000f)
            {
                cam.farClipPlane = 20000f;
            }

            // 2. Disable indoor arena Occlusion Culling (which hides meshes outside the hockey rink box)
            cam.useOcclusionCulling = false;

            // 3. Clear any per-layer distance limits
            if (emptyCullDistances == null) emptyCullDistances = new float[32];
            cam.layerCullDistances = emptyCullDistances;
        }

        private static bool IsFenceTransform(Transform t)
        {
            Transform curr = t;
            while (curr != null)
            {
                if (curr.name.IndexOf("Fence", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                curr = curr.parent;
            }
            return false;
        }

        private static void SetupFenceColliders(Transform customFieldRoot, Transform fieldRoot, int barrierLayer, int iceLayer, PhysicsMaterial wallMat)
        {
            Transform existingWalls = fieldRoot.Find("UltimateFenceCollidersRoot");
            if (existingWalls != null) UnityEngine.Object.Destroy(existingWalls.gameObject);

            GameObject wallRoot = new GameObject("UltimateFenceCollidersRoot");
            wallRoot.transform.SetParent(fieldRoot, false);

            int fenceSegmentsBuilt = 0;

            // Iterate ONLY over individual MeshRenderers inside the Fence hierarchy (never parent containers!)
            foreach (MeshRenderer rend in customFieldRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (rend == null || !IsFenceTransform(rend.transform)) continue;

                Bounds worldBounds = rend.bounds;
                if (worldBounds.size.sqrMagnitude < 0.05f) continue;

                // Safety check: Never build a fence collider inside the playable field + 2m runoff margin!
                // (Playable field is X: +/-18.5m, Z: +/-50m. If a bounds center is inside X: +/-19m and Z: +/-50.5m, skip it!)
                Vector3 c = worldBounds.center;
                if (Mathf.Abs(c.x) < UltimateRulesManager.FieldHalfWidth + 0.5f &&
                    Mathf.Abs(c.z) < UltimateRulesManager.FieldHalfLength + 0.5f)
                {
                    continue;
                }

                // Also skip any huge combined bounding box that spans across both sides of the field
                if (worldBounds.size.x > 35f && worldBounds.size.z > 35f)
                {
                    continue;
                }

                int[] layersToApply = (barrierLayer != iceLayer) ? new int[] { barrierLayer, iceLayer } : new int[] { barrierLayer };

                foreach (int layer in layersToApply)
                {
                    GameObject wallObj = new GameObject($"FenceBarrier_{rend.gameObject.name}_L{layer}");
                    wallObj.layer = layer;
                    wallObj.transform.SetParent(wallRoot.transform, true);

                    Vector3 center = worldBounds.center;
                    center.y = 4.5f;
                    wallObj.transform.position = center;
                    wallObj.transform.rotation = Quaternion.identity;

                    BoxCollider box = wallObj.AddComponent<BoxCollider>();
                    box.isTrigger = false;
                    if (wallMat != null) box.sharedMaterial = wallMat;

                    Vector3 size = worldBounds.size;
                    if (size.x < size.z)
                        size.x = Mathf.Max(size.x, 0.55f);
                    else
                        size.z = Mathf.Max(size.z, 0.55f);

                    size.y = 11.0f;
                    box.size = size;
                }

                fenceSegmentsBuilt++;
            }

            UltimateFrisbeePlugin.ModLogger.Info($"Built {fenceSegmentsBuilt} solid Fence barrier colliders outside playable field.");
        }

        private static GameObject BuildGrassUltimateField(
            Transform levelRoot,
            float groundSurfaceY,
            int iceLayer,
            int barrierLayer,
            PhysicsMaterial wallMat,
            PhysicsMaterial iceMat)
        {
            // Destroy any old UltimateGrassFieldRoot so changes to lines/colliders are always freshly built!
            Transform existing = levelRoot.Find("UltimateGrassFieldRoot");
            if (existing != null)
            {
                UnityEngine.Object.DestroyImmediate(existing.gameObject);
            }

            GameObject fieldRoot = new GameObject("UltimateGrassFieldRoot");
            fieldRoot.transform.SetParent(levelRoot, false);

            float visualGrassTopY = groundSurfaceY;

            if (UltimateAssetLoader.CustomGrassFieldPrefab != null)
            {
                GameObject customField = UnityEngine.Object.Instantiate(UltimateAssetLoader.CustomGrassFieldPrefab, fieldRoot.transform);
                customField.name = "UltimateCustomGrassField";

                // Stop LODGroups from hiding distant mountains (DO NOT modify sharedMesh.bounds here,
                // because visualGrassTopY and SetupFenceColliders rely on accurate renderer bounds!)
                foreach (LODGroup lod in customField.GetComponentsInChildren<LODGroup>(true))
                {
                    lod.enabled = false;
                }

                // Disable ALL built-in colliders on the custom prefab!
                foreach (Collider col in customField.GetComponentsInChildren<Collider>(true))
                {
                    col.enabled = false;
                }

                Shader urpLit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

                foreach (Renderer r in customField.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = true;
                    string rName = r.gameObject.name.ToLowerInvariant();

                    // Hide the dark-green baked field lines inside the prefab so our crisp Blue/Red/White lines show cleanly!
                    if (rName.Contains("line") || rName.Contains("marking") || rName.Contains("border"))
                    {
                        r.enabled = false;
                        continue;
                    }

                    bool isGrassMesh = rName.Contains("grass") || rName.Contains("turf") || rName.Contains("field") || rName.Contains("ground") || rName.Contains("pitch");
                    if (isGrassMesh && r.bounds.size.x > 10f && r.bounds.size.z > 10f)
                    {
                        // Capture the true top Y of the visual grass mesh!
                        visualGrassTopY = Mathf.Max(visualGrassTopY, r.bounds.max.y);
                    }

                    Material[] mats = r.materials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] == null) continue;
                        mats[i] = UltimateAssetLoader.ConvertBundleMaterialToNative(mats[i], isGrassMesh ? 0.08f : 0.15f, isGrassMesh);
                    }
                    r.materials = mats;
                }

                // Build rock-solid barrier colliders ONLY on the actual Fence meshes outside the field!
                SetupFenceColliders(customField.transform, fieldRoot.transform, barrierLayer, iceLayer, wallMat);
            }

            // Create our unified 350m x 350m Ground Collider on the ICE LAYER so players can walk smoothly
            // everywhere (including behind the endzones up to the fences) and NEVER fall through the map!
            GameObject turfObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            turfObj.name = "GrassTurfSurface";
            turfObj.layer = iceLayer;
            turfObj.transform.SetParent(fieldRoot.transform, true);
            // Top of this 1.0m thick cube is at visualGrassTopY
            turfObj.transform.position = new Vector3(0f, visualGrassTopY - 0.5f, 0f);
            turfObj.transform.rotation = Quaternion.identity;
            turfObj.transform.localScale = new Vector3(350f, 1.0f, 350f);

            BoxCollider turfCol = turfObj.GetComponent<BoxCollider>();
            if (turfCol != null)
            {
                turfCol.enabled = true;
                turfCol.isTrigger = false;
                if (iceMat != null) turfCol.sharedMaterial = iceMat;
            }

            MeshRenderer turfRenderer = turfObj.GetComponent<MeshRenderer>();
            if (UltimateAssetLoader.CustomGrassFieldPrefab != null || UltimateFrisbeePlugin.IsHeadlessServer)
            {
                if (turfRenderer != null) turfRenderer.enabled = false;
            }
            else
            {
                Shader std = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
                if (std != null && turfRenderer != null)
                {
                    var grassMat = new Material(std) { color = new Color(0.18f, 0.45f, 0.20f) };
                    if (grassMat.HasProperty("_Smoothness")) grassMat.SetFloat("_Smoothness", 0.08f);
                    if (grassMat.HasProperty("_Glossiness")) grassMat.SetFloat("_Glossiness", 0.08f);
                    turfRenderer.material = grassMat;
                }
            }

            if (!UltimateFrisbeePlugin.IsHeadlessServer)
            {
                float hw = UltimateRulesManager.FieldHalfWidth;      // 18.5m
                float hl = UltimateRulesManager.FieldHalfLength;     // 50m
                float ez = UltimateRulesManager.EndzoneZThreshold;   // 32m
                float brickZ = UltimateRulesManager.BrickMarkZ;      // 14m

                // Place the lines 1.2cm (0.012m) above the highest grass surface so they ALWAYS render above the grass
                // while staying well below resting discs (0.035m) and cones!
                float lineY = visualGrassTopY + 0.012f;

                Color chalkWhite = new Color(0.95f, 0.96f, 0.95f, 1f);
                Color chalkBlue  = new Color(0.15f, 0.58f, 1.00f, 1f);
                Color chalkRed   = new Color(1.00f, 0.22f, 0.20f, 1f);
                Color chalkMid   = new Color(0.92f, 0.94f, 0.92f, 1f);

                // Soft Blue & Red diagonal/hash turf stripes inside the Endzones using opaque URP/Lit meshes (never culled by URP!)
                Color blueZoneStripe = new Color(0.16f, 0.48f, 0.85f, 1f);
                Color redZoneStripe  = new Color(0.85f, 0.24f, 0.22f, 1f);
                DrawEndzoneHashMarks(fieldRoot.transform, "BlueEndzoneHashes", ez, hl, hw, visualGrassTopY + 0.008f, blueZoneStripe);
                DrawEndzoneHashMarks(fieldRoot.transform, "RedEndzoneHashes", -hl, -ez, hw, visualGrassTopY + 0.008f, redZoneStripe);

                // Central Sidelines (White)
                DrawFieldLine(fieldRoot.transform, "LeftSidelineCenter",  new Vector3(-hw, lineY, -ez), new Vector3(-hw, lineY, ez),  chalkWhite, 0.20f);
                DrawFieldLine(fieldRoot.transform, "RightSidelineCenter", new Vector3(hw, lineY, -ez),  new Vector3(hw, lineY, ez),   chalkWhite, 0.20f);

                // Blue Endzone Perimeter (+32m to +50m)
                DrawFieldLine(fieldRoot.transform, "BlueLeftSideline",  new Vector3(-hw, lineY, ez), new Vector3(-hw, lineY, hl), chalkBlue, 0.24f);
                DrawFieldLine(fieldRoot.transform, "BlueRightSideline", new Vector3(hw, lineY, ez),  new Vector3(hw, lineY, hl),  chalkBlue, 0.24f);
                DrawFieldLine(fieldRoot.transform, "NorthBackline",     new Vector3(-hw, lineY, hl), new Vector3(hw, lineY, hl),  chalkBlue, 0.26f);
                DrawFieldLine(fieldRoot.transform, "NorthGoalLine",     new Vector3(-hw, lineY, ez), new Vector3(hw, lineY, ez),  chalkBlue, 0.26f);

                // Red Endzone Perimeter (-32m to -50m)
                DrawFieldLine(fieldRoot.transform, "RedLeftSideline",   new Vector3(-hw, lineY, -hl), new Vector3(-hw, lineY, -ez), chalkRed, 0.24f);
                DrawFieldLine(fieldRoot.transform, "RedRightSideline",  new Vector3(hw, lineY, -hl),  new Vector3(hw, lineY, -ez),  chalkRed, 0.24f);
                DrawFieldLine(fieldRoot.transform, "SouthBackline",     new Vector3(-hw, lineY, -hl), new Vector3(hw, lineY, -hl),  chalkRed, 0.26f);
                DrawFieldLine(fieldRoot.transform, "SouthGoalLine",     new Vector3(-hw, lineY, -ez), new Vector3(hw, lineY, -ez),  chalkRed, 0.26f);

                // Midfield Line
                DrawFieldLine(fieldRoot.transform, "MidfieldLine", new Vector3(-hw, lineY, 0f), new Vector3(hw, lineY, 0f), chalkMid, 0.16f);
            }

            return fieldRoot;
        }

        private static void DrawEndzoneHashMarks(Transform parent, string name, float zMin, float zMax, float halfWidth, float yHeight, Color color)
        {
            GameObject group = new GameObject(name);
            group.transform.SetParent(parent, false);

            // Draw clean team-colored yard/endzone stripes every 4.5 meters inside the 18m deep endzone
            for (float z = zMin + 4.5f; z < zMax - 1.0f; z += 4.5f)
            {
                DrawFieldLine(group.transform, $"EndzoneStripe_{z:F1}",
                    new Vector3(-halfWidth + 0.5f, yHeight, z),
                    new Vector3(halfWidth - 0.5f, yHeight, z),
                    color, 0.15f);
            }
        }

        private static void DrawFieldLine(Transform parent, string name, Vector3 start, Vector3 end, Color color, float width = 0.20f)
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;

            Vector3 delta = end - start;
            float length = delta.magnitude;
            if (length < 0.01f) return;

            GameObject lineObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            lineObj.name = name;
            UnityEngine.Object.Destroy(lineObj.GetComponent<Collider>());
            lineObj.transform.SetParent(parent, false);

            Vector3 midPoint = (start + end) * 0.5f;
            Quaternion forwardRot = Quaternion.LookRotation(delta.normalized, Vector3.up);
            lineObj.transform.position = midPoint;
            lineObj.transform.rotation = forwardRot;
            lineObj.transform.localScale = new Vector3(width, 0.004f, length);

            MeshRenderer mr = lineObj.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = true;

                Material template = UltimateAssetLoader.GetNativeTemplateMaterial();
                if (template != null)
                {
                    Material lineMat = new Material(template);
                    if (lineMat.HasProperty("_BaseColor")) lineMat.SetColor("_BaseColor", color);
                    if (lineMat.HasProperty("_Color")) lineMat.SetColor("_Color", color);
                    lineMat.color = color;

                    if (lineMat.HasProperty("_EmissionColor"))
                    {
                        lineMat.EnableKeyword("_EMISSION");
                        lineMat.SetColor("_EmissionColor", color * 0.40f);
                    }
                    if (lineMat.HasProperty("_Smoothness")) lineMat.SetFloat("_Smoothness", 0.1f);
                    if (lineMat.HasProperty("_Glossiness")) lineMat.SetFloat("_Glossiness", 0.1f);
                    if (lineMat.HasProperty("_Metallic")) lineMat.SetFloat("_Metallic", 0f);
                    mr.material = lineMat;
                }
            }
        }
    }

    // =========================================================================
    // 10. GAME MODE HOOKS (Pull Lineups at +/- 32m Goal Lines)
    // =========================================================================
    [HarmonyPatch(typeof(PlayerBody), "ApplyCustomizations")]
    public static class PlayerBody_RemoveGoaliePads_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerBody __instance)
        {
            if (__instance.PlayerMesh != null)
            {
                __instance.PlayerMesh.SetLegsPadsActive(false);
            }
        }
    }

    [HarmonyPatch(typeof(StandardGameMode<StandardGameModeConfig>), "OnFaceOffStarted")]
    public static class StandardGameMode_OnFaceOffStarted_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(object __instance)
        {
            Level existingLevel = UnityEngine.Object.FindFirstObjectByType<Level>();
            if (existingLevel != null)
            {
                Level_GrassStadiumSetup_Patch.Postfix(existingLevel);
            }

            UltimateRulesManager.ActiveGameModeInstance = __instance;
            UltimateRulesManager.ResetPlayState();

            if (!NetworkRoles.IsServer) return;

            if (MonoBehaviourSingleton<PlayerManager>.Instance != null)
            {
                var players = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(false);
                int blueIdx = 0, redIdx = 0;

                foreach (var player in players)
                {
                    if (!player.IsCharacterSpawned || player.PlayerBody == null) continue;

                    PlayerTeam pTeam = UltimateRulesManager.GetTeam(player);
                    float teamZ = (pTeam == PlayerTeam.Blue) ? UltimateRulesManager.EndzoneZThreshold : -UltimateRulesManager.EndzoneZThreshold;
                    int idx = (pTeam == PlayerTeam.Blue) ? blueIdx++ : redIdx++;
                    float xOffset = (idx - 1.5f) * 4.0f;

                    Vector3 lineupPos = new Vector3(xOffset, 0.25f, teamZ);
                    Quaternion lookCenter = Quaternion.LookRotation(new Vector3(0f, 0f, -Mathf.Sign(teamZ)), Vector3.up);

                    player.PlayerBody.Server_Teleport(lineupPos, lookCenter);
                    player.PlayerBody.Server_Freeze((RigidbodyConstraints)10);
                }
            }

            UltimateRulesManager.BroadcastAnnouncement($"Line up on the Goal Lines! {UltimateRulesManager.PullingTeam} will Pull!", "#ffe97f");
        }
    }

    [HarmonyPatch(typeof(BaseGameMode<BaseGameModeConfig>), "OnChatCommand")]
    public static class UltimateVisualTuning_ChatCommands_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Player player, string command, string[] args)
        {
            if (command == "/armrot" && args.Length == 3 &&
                float.TryParse(args[0], out float ax) && float.TryParse(args[1], out float ay) && float.TryParse(args[2], out float az))
            {
                UltimateVisualTuning.ArmRotationOffset = new Vector3(ax, ay, az);
                UltimateRulesManager.BroadcastAnnouncement($"ArmRotationOffset set to ({ax}, {ay}, {az})");
                return false;
            }
            if ((command == "/armscale" || command == "/armthick") && args.Length == 1 && float.TryParse(args[0], out float athick))
            {
                UltimateVisualTuning.ArmThicknessMultiplier = athick;
                UltimateRulesManager.BroadcastAnnouncement($"ArmThicknessMultiplier set to {athick}");
                return false;
            }
            if (command == "/armlen" && args.Length == 1 && float.TryParse(args[0], out float alen))
            {
                UltimateVisualTuning.ArmLengthMultiplier = alen;
                UltimateRulesManager.BroadcastAnnouncement($"ArmLengthMultiplier set to {alen}");
                return false;
            }
            if (command == "/discrot" && args.Length == 3 &&
                float.TryParse(args[0], out float dx) && float.TryParse(args[1], out float dy) && float.TryParse(args[2], out float dz))
            {
                UltimateVisualTuning.DiscMeshRotationOffset = new Vector3(dx, dy, dz);
                UltimateRulesManager.BroadcastAnnouncement($"DiscMeshRotationOffset set to ({dx}, {dy}, {dz})");
                return false;
            }
            if (command == "/discscale" && args.Length == 1 && float.TryParse(args[0], out float dscale))
            {
                UltimateVisualTuning.DiscMeshScale = dscale;
                UltimateRulesManager.BroadcastAnnouncement($"DiscMeshScale set to {dscale}");
                return false;
            }
            if (command == "/discoffset" && args.Length == 3 &&
                float.TryParse(args[0], out float ox) && float.TryParse(args[1], out float oy) && float.TryParse(args[2], out float oz))
            {
                UltimateVisualTuning.HeldDiscHandOffset = new Vector3(ox, oy, oz);
                UltimateRulesManager.BroadcastAnnouncement($"HeldDiscHandOffset set to ({ox}, {oy}, {oz})");
                return false;
            }
            if (command == "/armreach" && args.Length == 1 && float.TryParse(args[0], out float areach))
            {
                UltimateVisualTuning.ArmReachCompression = Mathf.Clamp(areach, 0.25f, 1.0f);
                UltimateRulesManager.BroadcastAnnouncement($"ArmReachCompression set to {UltimateVisualTuning.ArmReachCompression:F2}");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PuckManager), "Server_SpawnPucksForPhase")]
    public static class PuckManager_SingleDiscOnly_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(PuckManager __instance, GamePhase phase)
        {
            if (!NetworkRoles.IsServer) return;

            var pucks = __instance.GetPucks(false).ToList();

            // During Warmup, allow multiple discs! If the server config only spawned 1, spawn extra warmup discs.
            if (phase == GamePhase.Warmup)
            {
                // Spread out existing warmup discs along the field so players can grab them easily
                for (int i = 0; i < pucks.Count; i++)
                {
                    if (pucks[i] == null || pucks[i].Rigidbody == null) continue;
                    float xSpread = ((i % 5) - 2) * 5.0f;
                    float zSpread = ((i / 5) - 1) * 8.0f;
                    Vector3 warmupSpot = new Vector3(xSpread, UltimateVisualTuning.DiscHalfThickness + 0.05f, zSpread);
                    pucks[i].Rigidbody.position = warmupSpot;
                    pucks[i].transform.position = warmupSpot;
                }
                return;
            }

            if (phase == GamePhase.Warmup)
            {
                UltimateRulesManager.ResetPlayState();

                if (MonoBehaviourSingleton<PlayerManager>.Instance != null)
                {
                    foreach (var p in MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(false))
                    {
                        if (p != null && p.IsCharacterSpawned && p.PlayerBody != null)
                            p.PlayerBody.Server_Unfreeze();
                    }
                }

                // Spread out existing warmup discs along the field so players can grab them easily
                for (int i = 0; i < pucks.Count; i++)
                {
                    if (pucks[i] == null || pucks[i].Rigidbody == null) continue;
                    pucks[i].Rigidbody.isKinematic = false;
                    pucks[i].Server_Unfreeze();
                    float xSpread = ((i % 5) - 2) * 5.0f;
                    float zSpread = ((i / 5) - 1) * 8.0f;
                    Vector3 warmupSpot = new Vector3(xSpread, UltimateVisualTuning.DiscHalfThickness + 0.05f, zSpread);
                    pucks[i].Rigidbody.position = warmupSpot;
                    pucks[i].transform.position = warmupSpot;
                }
                return;
            }

            // During FaceOff or Play, keep ONLY the single match Frisbee and despawn extras!
            for (int i = 1; i < pucks.Count; i++)
            {
                if (pucks[i] != null)
                {
                    __instance.Server_DespawnPuck(pucks[i]);
                }
            }

            Puck disc = pucks.FirstOrDefault();
            if (disc == null) return;

            if (phase == GamePhase.FaceOff || phase == GamePhase.Play)
            {
                Puck_UltimatePhysicsAndCatch_Patch.SetupPullDiscAndTeam(disc, isPlayStart: (phase == GamePhase.Play));
            }
        }
    }

    // =========================================================================
    // 11. INTERCEPT STANDARD SPAWNS, ROLES & POSITION NAMES (1-6, ALL SKATERS)
    // =========================================================================
    [HarmonyPatch]
    public static class PlayerSpawning_EndzoneGoalLines_Patches
    {
        public static void GetGoalLineSpawnForPlayer(Player player, out Vector3 spawnPos, out Quaternion spawnRot)
        {
            bool isBlue = (UltimateRulesManager.GetTeam(player) == PlayerTeam.Blue);
            float goalLineZ = isBlue ? UltimateRulesManager.EndzoneZThreshold : -UltimateRulesManager.EndzoneZThreshold;

            float xOffset = UnityEngine.Random.Range(-10.0f, 10.0f);
            if (player != null && player.PlayerPosition != null)
            {
                xOffset = Mathf.Clamp(player.PlayerPosition.transform.position.x, -15f, 15f);
            }

            spawnPos = new Vector3(xOffset + UnityEngine.Random.Range(-0.75f, 0.75f), 0.25f, goalLineZ);
            spawnRot = Quaternion.LookRotation(new Vector3(0f, 0f, isBlue ? -1f : 1f), Vector3.up);
        }

        // Force every PlayerPosition to use Role = Attacker and Name = "1".."6" on spawn
        [HarmonyPatch(typeof(PlayerPosition), "OnNetworkPreSpawn")]
        [HarmonyPrefix]
        public static void ForcePositionAttackerPreSpawn(PlayerPosition __instance)
        {
            if (__instance == null) return;
            __instance.Role = PlayerRole.Attacker;
            __instance.Name = Level_GrassStadiumSetup_Patch.MapHockeyPositionNameToNumber(__instance.Name, 0);
        }

        [HarmonyPatch(typeof(PlayerPosition), "OnNetworkSpawn")]
        [HarmonyPrefix]
        public static void ForcePositionAttackerOnSpawn(PlayerPosition __instance)
        {
            if (__instance == null) return;
            __instance.Role = PlayerRole.Attacker;
            __instance.Name = Level_GrassStadiumSetup_Patch.MapHockeyPositionNameToNumber(__instance.Name, 0);
        }

        // Guarantee UIPositionSelect always styles positions as "1".."6" and Role = Attacker
        [HarmonyPatch(typeof(UIPositionSelect), "StylePosition")]
        [HarmonyPrefix]
        public static void EnsurePositionNumberBeforeStyle(PlayerPosition playerPosition)
        {
            if (playerPosition == null) return;
            playerPosition.Role = PlayerRole.Attacker;
            playerPosition.Name = Level_GrassStadiumSetup_Patch.MapHockeyPositionNameToNumber(playerPosition.Name, 0);
        }

        // Prevent any player's GameState from ever being set to PlayerRole.Goalie
        [HarmonyPatch(typeof(Player), "Server_SetGameState")]
        [HarmonyPrefix]
        public static void ForceAttackerGameStateRole(ref PlayerRole? role)
        {
            if (role.HasValue && role.Value == PlayerRole.Goalie)
            {
                role = PlayerRole.Attacker;
            }
        }

        // Force Attacker prefab & Goal Line position on character spawn
        [HarmonyPatch(typeof(Player), "Server_SpawnCharacter")]
        [HarmonyPrefix]
        public static void OverrideSpawnCharacter(Player __instance, ref Vector3 position, ref Quaternion rotation, ref PlayerRole role)
        {
            role = PlayerRole.Attacker;
            if (__instance == null) return;
            PlayerTeam team = UltimateRulesManager.GetTeam(__instance);
            if (team == PlayerTeam.Blue || team == PlayerTeam.Red)
            {
                GetGoalLineSpawnForPlayer(__instance, out position, out rotation);
            }
        }

        // Force Attacker body prefab even if Server_SpawnPlayerBody is called directly
        [HarmonyPatch(typeof(Player), "Server_SpawnPlayerBody")]
        [HarmonyPrefix]
        public static void OverrideSpawnPlayerBody(Player __instance, ref Vector3 position, ref Quaternion rotation, ref PlayerRole role)
        {
            role = PlayerRole.Attacker;
            if (__instance == null) return;
            PlayerTeam team = UltimateRulesManager.GetTeam(__instance);
            if (team == PlayerTeam.Blue || team == PlayerTeam.Red)
            {
                GetGoalLineSpawnForPlayer(__instance, out position, out rotation);
            }
        }

        // Force Attacker stick prefab even if Server_SpawnStick is called directly
        [HarmonyPatch(typeof(Player), "Server_SpawnStick")]
        [HarmonyPrefix]
        public static void OverrideSpawnStick(ref PlayerRole role)
        {
            role = PlayerRole.Attacker;
        }

        [HarmonyPatch(typeof(PlayerBody), "Server_Teleport")]
        [HarmonyPrefix]
        public static void OverrideFaceOffTeleport(PlayerBody __instance, ref Vector3 position, ref Quaternion rotation)
        {
            if (__instance == null || __instance.Player == null) return;

            if (Mathf.Abs(position.z) < UltimateRulesManager.EndzoneZThreshold - 2.0f)
            {
                Player player = __instance.Player;
                PlayerTeam team = UltimateRulesManager.GetTeam(player);
                if (team == PlayerTeam.Blue || team == PlayerTeam.Red)
                {
                    GetGoalLineSpawnForPlayer(player, out position, out rotation);
                }
            }
        }
    }

    // =========================================================================
    // 11B. SAFELY BLOCK MID-PLAY RESPAWN / POSITION SELECT
    // =========================================================================
    [HarmonyPatch]
    public static class PlayerController_BlockMidPlayRespawn_Patches
    {
        private static MethodBase _targetMethod;

        [HarmonyPrepare]
        public static bool Prepare()
        {
            // Search PlayerController and Player for the position select / claim method without crashing Harmony!
            string[] candidateMethods = new[]
            {
                "Event_Client_OnPlayerRequestPositionSelect",
                "Event_OnPlayerRequestPositionSelect",
                "OnPlayerRequestPositionSelect",
                "Server_SelectPositionRpc",
                "Server_ClaimPosition"
            };

            Type[] candidateTypes = new[] { typeof(PlayerController), typeof(Player), typeof(PlayerPosition) };

            foreach (var type in candidateTypes)
            {
                if (type == null) continue;
                foreach (var methodName in candidateMethods)
                {
                    var m = AccessTools.Method(type, methodName);
                    if (m != null)
                    {
                        _targetMethod = m;
                        return true;
                    }
                }
            }

            // If none of those exact names exist, skip this patch gracefully instead of crashing the whole mod!
            UltimateFrisbeePlugin.ModLogger.Info("Skipping BlockMidPlayRespawn patch (method not found on PlayerController).");
            return false;
        }

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod() => _targetMethod;

        [HarmonyPrefix]
        public static bool BlockPositionSelectDuringPlay()
        {
            if (NetworkBehaviourSingleton<GameManager>.Instance != null &&
                NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.Play)
            {
                return false;
            }
            return true;
        }
    }

    // =========================================================================
    // 12. HIDE VANILLA MINIMAP & MUTE STICK-TO-ICE TAP/DRAG AUDIO
    // =========================================================================
    [HarmonyPatch(typeof(UIMinimap))]
    public static class UIMinimap_Hide_Patch
    {
        [HarmonyPatch("Show")]
        [HarmonyPrefix]
        public static bool ShowPrefix(UIMinimap __instance, ref bool __result)
        {
            __instance.Hide();
            __result = false;
            return false;
        }

        [HarmonyPatch("Update")]
        [HarmonyPrefix]
        public static bool UpdatePrefix(UIMinimap __instance)
        {
            if (__instance.IsVisible)
            {
                __instance.Hide();
            }
            return false;
        }
    }

    [HarmonyPatch]
    public static class StickAndPositioner_MuteAllStickAudio_Patch
    {
        private static readonly FieldInfo _iceHitAudioField = AccessTools.Field(typeof(StickPositioner), "iceHitAudioSource");
        private static readonly FieldInfo _iceDragAudioField = AccessTools.Field(typeof(StickPositioner), "iceDragAudioSource");
        private static readonly FieldInfo _windAudioField = AccessTools.Field(typeof(StickPositioner), "windAudioSource");
        private static readonly FieldInfo _syncAudioSourceField = AccessTools.Field(typeof(SynchronizedAudio), "audioSource");

        private static readonly HashSet<SynchronizedAudio> _mutedStickAudios = new HashSet<SynchronizedAudio>();
        
        public static void RegisterAndMuteExternalAudio(SynchronizedAudio syncAudio)
        {
            if (syncAudio == null) return;
            _mutedStickAudios.Add(syncAudio);

            AudioSource src = _syncAudioSourceField?.GetValue(syncAudio) as AudioSource ?? syncAudio.GetComponent<AudioSource>();
            if (src != null)
            {
                src.mute = true;
                src.volume = 0f;
                src.Stop();
            }
        }

        
        private static bool ShouldMuteAudio(SynchronizedAudio syncAudio)
        {
            if (syncAudio == null) return false;
            if (_mutedStickAudios.Contains(syncAudio)) return true;

            string objName = syncAudio.gameObject.name.ToLowerInvariant();
            if (objName.Contains("icehit") || objName.Contains("icedrag") ||
                objName.Contains("ice hit") || objName.Contains("ice drag") ||
                objName.Contains("stickhit") || objName.Contains("stickdrag"))
            {
                RegisterAndMuteExternalAudio(syncAudio);
                return true;
            }

            var sp = syncAudio.GetComponentInParent<StickPositioner>();
            if (sp != null)
            {
                var wind = _windAudioField?.GetValue(sp) as SynchronizedAudio;
                if (syncAudio != wind)
                {
                    RegisterAndMuteExternalAudio(syncAudio);
                    return true;
                }
            }

            if (syncAudio.GetComponentInParent<Stick>() != null)
            {
                RegisterAndMuteExternalAudio(syncAudio);
                return true;
            }

            return false;
        }

        [HarmonyPatch(typeof(StickPositioner), "Awake")]
        [HarmonyPostfix]
        public static void StickPositionerAwakePostfix(StickPositioner __instance)
        {
            RegisterAndMuteExternalAudio(_iceHitAudioField?.GetValue(__instance) as SynchronizedAudio);
            RegisterAndMuteExternalAudio(_iceDragAudioField?.GetValue(__instance) as SynchronizedAudio);
        }

        [HarmonyPatch(typeof(StickPositioner), "OnNetworkPostSpawn")]
        [HarmonyPostfix]
        public static void StickPositionerPostSpawnPostfix(StickPositioner __instance)
        {
            RegisterAndMuteExternalAudio(_iceHitAudioField?.GetValue(__instance) as SynchronizedAudio);
            RegisterAndMuteExternalAudio(_iceDragAudioField?.GetValue(__instance) as SynchronizedAudio);
        }

        [HarmonyPatch(typeof(StickPositioner), "UpdateAudio")]
        [HarmonyPostfix]
        public static void UpdateAudioPostfix(StickPositioner __instance)
        {
            var hitAudio = _iceHitAudioField?.GetValue(__instance) as SynchronizedAudio;
            var dragAudio = _iceDragAudioField?.GetValue(__instance) as SynchronizedAudio;

            RegisterAndMuteExternalAudio(hitAudio);
            RegisterAndMuteExternalAudio(dragAudio);

            if (NetworkRoles.IsServer && dragAudio != null)
            {
                dragAudio.Server_SetVolume(0f);
            }
        }

        [HarmonyPatch(typeof(StickPositioner), "OnGrounded", new Type[] { typeof(GameObject) })]
        [HarmonyPrefix]
        public static bool OnGroundedPrefix(StickPositioner __instance)
        {
            __instance.IsGrounded = true;
            return false;
        }

        [HarmonyPatch(typeof(SynchronizedAudio), "Server_Play")]
        [HarmonyPrefix]
        public static bool PreventStickServerPlay(SynchronizedAudio __instance)
        {
            if (ShouldMuteAudio(__instance))
            {
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(SynchronizedAudio), "Server_PlayRpc")]
        [HarmonyPrefix]
        public static bool PreventStickClientPlayRpc(SynchronizedAudio __instance)
        {
            if (ShouldMuteAudio(__instance))
            {
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(SynchronizedAudio), "Server_SetVolume")]
        [HarmonyPrefix]
        public static void ForceZeroStickVolumeServer(SynchronizedAudio __instance, ref float volume)
        {
            if (ShouldMuteAudio(__instance))
            {
                volume = 0f;
            }
        }

        [HarmonyPatch(typeof(SynchronizedAudio), "OnVolumeChanged")]
        [HarmonyPrefix]
        public static void ForceZeroStickVolumeClient(SynchronizedAudio __instance, ref byte newVolume)
        {
            if (ShouldMuteAudio(__instance))
            {
                newVolume = 0;
            }
        }
    }
    // =========================================================================
    // 13. RUNNING CAMERA HEAD-BOB, TURF FOOTSTEPS & 3D FRISBEE AUDIO SYSTEM
    // =========================================================================
    public enum UltimateSoundType : byte
    {
        Throw = 0,
        Catch = 1,
        BlockTip = 2,
        Pickup = 3,
        GroundHit = 4,
        Footstep = 5
    }

    public static class UltimateAudioManager
    {
        public const string SoundMessageName = "ULTIMATE/Sfx";
        private static CustomMessagingManager _registeredSfxCmm = null;

        public static AudioClip ThrowClip;
        public static AudioClip CatchClip;
        public static AudioClip BlockTipClip;
        public static AudioClip PickupClip;
        public static AudioClip GroundHitClip;
        public static AudioClip FootstepClip;

        public static void Initialize(string pluginFolder)
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;
            // 1. Clean, warm, non-harsh procedural acoustic synthesis (NO white-noise snare rattle!)
            ThrowClip = CreateProceduralClip("Ult_Throw", 0.22f, (t, dur) =>
            {
                // Smooth low-mid air whoosh (filtered sine-modulated sweep from 280Hz down to 140Hz)
                float env = Mathf.Sin((t / dur) * Mathf.PI);
                float freq = Mathf.Lerp(260f, 130f, t / dur);
                float whoosh = Mathf.Sin(t * 2f * Mathf.PI * freq + Mathf.Sin(t * 95f) * 2.5f) * env * 0.28f;
                return Mathf.Clamp(whoosh, -1f, 1f);
            });

            CatchClip = CreateProceduralClip("Ult_Catch", 0.085f, (t, dur) =>
            {
                // Solid, dull plastic-in-hand thud (fast pitch drop from 320Hz -> 110Hz, zero white noise)
                float env = Mathf.Exp(-t * 55f);
                float pitchDrop = Mathf.Lerp(320f, 105f, Mathf.Clamp01(t / 0.035f));
                float palmThud = Mathf.Sin(t * 2f * Mathf.PI * pitchDrop) * env * 0.65f;
                float plasticBody = Mathf.Sin(t * 2f * Mathf.PI * 410f) * Mathf.Exp(-t * 85f) * 0.25f;
                return Mathf.Clamp(palmThud + plasticBody, -1f, 1f);
            });

            BlockTipClip = CreateProceduralClip("Ult_BlockTip", 0.095f, (t, dur) =>
            {
                // Hollow plastic rim knock (two short woody/plastic sine modes, no snare hiss)
                float env = Mathf.Exp(-t * 48f);
                float pitchDrop = Mathf.Lerp(480f, 190f, Mathf.Clamp01(t / 0.03f));
                float rimKnock = (Mathf.Sin(t * 2f * Mathf.PI * pitchDrop) * 0.6f +
                                  Mathf.Sin(t * 2f * Mathf.PI * 620f) * Mathf.Exp(-t * 75f) * 0.3f) * env;
                return Mathf.Clamp(rimKnock, -1f, 1f);
            });

            PickupClip = CreateProceduralClip("Ult_Pickup", 0.065f, (t, dur) =>
            {
                // Very soft, quiet, muted finger-grip thump (pure low-frequency soft sine drop, 160Hz -> 75Hz)
                float env = Mathf.Sin(Mathf.Clamp01(t / dur) * Mathf.PI) * Mathf.Exp(-t * 35f);
                float freq = Mathf.Lerp(160f, 75f, t / dur);
                float softGrip = Mathf.Sin(t * 2f * Mathf.PI * freq) * env * 0.32f;
                return Mathf.Clamp(softGrip, -1f, 1f);
            });

            GroundHitClip = CreateProceduralClip("Ult_GroundHit", 0.09f, (t, dur) =>
            {
                // Muffled turf thump (low 110Hz -> 55Hz damped thud)
                float env = Mathf.Exp(-t * 45f);
                float freq = Mathf.Lerp(120f, 52f, t / dur);
                float turfThud = Mathf.Sin(t * 2f * Mathf.PI * freq) * env * 0.45f;
                return Mathf.Clamp(turfThud, -1f, 1f);
            });

            FootstepClip = CreateProceduralClip("Ult_Footstep", 0.06f, (t, dur) =>
            {
                // Ultra-subtle, deep soft turf step (90Hz -> 42Hz)
                float env = Mathf.Sin(Mathf.Clamp01(t / dur) * Mathf.PI) * Mathf.Exp(-t * 42f);
                float freq = Mathf.Lerp(90f, 42f, t / dur);
                float step = Mathf.Sin(t * 2f * Mathf.PI * freq) * env * 0.25f;
                return Mathf.Clamp(step, -1f, 1f);
            });

            // 2. Optional: If custom .wav files exist in the plugin folder, load them
            if (!string.IsNullOrEmpty(pluginFolder))
            {
                TryLoadCustomWav(Path.Combine(pluginFolder, "throw.wav"), ref ThrowClip);
                TryLoadCustomWav(Path.Combine(pluginFolder, "catch.wav"), ref CatchClip);
                TryLoadCustomWav(Path.Combine(pluginFolder, "block.wav"), ref BlockTipClip);
                TryLoadCustomWav(Path.Combine(pluginFolder, "pickup.wav"), ref PickupClip);
                TryLoadCustomWav(Path.Combine(pluginFolder, "ground.wav"), ref GroundHitClip);
                TryLoadCustomWav(Path.Combine(pluginFolder, "step.wav"), ref FootstepClip);
            }
        }

        public static void EnsureNetworkRegistered()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null) return;
            if (_registeredSfxCmm == nm.CustomMessagingManager) return;

            nm.CustomMessagingManager.UnregisterNamedMessageHandler(SoundMessageName);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(SoundMessageName, OnSoundMessageReceived);
            _registeredSfxCmm = nm.CustomMessagingManager;
        }

        public static void UnregisterNetwork()
        {
            if (_registeredSfxCmm != null)
            {
                _registeredSfxCmm.UnregisterNamedMessageHandler(SoundMessageName);
            }
            _registeredSfxCmm = null;
        }

        public static void ServerBroadcastSound(UltimateSoundType type, Vector3 position, float volume = 1f, float pitch = 1f)
        {
            EnsureNetworkRegistered();
            PlayLocal3DSound(type, position, volume, pitch);

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
            if (nm.ConnectedClientsIds == null || nm.ConnectedClientsIds.Count == 0) return;

            var writer = new FastBufferWriter(24, Allocator.Temp);
            try
            {
                writer.WriteValueSafe((byte)type);
                writer.WriteValueSafe(position);
                writer.WriteValueSafe(volume);
                writer.WriteValueSafe(pitch);
                nm.CustomMessagingManager.SendNamedMessageToAll(SoundMessageName, writer, NetworkDelivery.Unreliable);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnSoundMessageReceived(ulong senderId, FastBufferReader reader)
        {
            if (NetworkRoles.IsServer) return;
            try
            {
                reader.ReadValueSafe(out byte rawType);
                reader.ReadValueSafe(out Vector3 pos);
                reader.ReadValueSafe(out float volume);
                reader.ReadValueSafe(out float pitch);
                PlayLocal3DSound((UltimateSoundType)rawType, pos, volume, pitch);
            }
            catch { }
        }

        public static void PlayLocal3DSound(UltimateSoundType type, Vector3 position, float volume = 1f, float pitch = 1f)
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;
            AudioClip clip = null;
            switch (type)
            {
                case UltimateSoundType.Throw:
                    clip = ThrowClip;
                    break;
                case UltimateSoundType.Catch:
                    clip = CatchClip;
                    break;
                case UltimateSoundType.BlockTip:
                    clip = BlockTipClip;
                    break;
                case UltimateSoundType.Pickup:
                    clip = PickupClip;
                    break;
                case UltimateSoundType.GroundHit:
                    clip = GroundHitClip;
                    break;
                case UltimateSoundType.Footstep:
                    clip = FootstepClip;
                    break;
            }

            if (clip == null) return;

            GameObject sfxObj = new GameObject($"UltSfx_{type}");
            sfxObj.transform.position = position;

            AudioSource src = sfxObj.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 0.85f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 2.5f;
            src.maxDistance = 45.0f;

            // 1. Pitch & Volume Micro-Jitter (breaks up identical consecutive plays, especially on Throws)
            float pitchJitter = (type == UltimateSoundType.Footstep)
                ? UnityEngine.Random.Range(0.88f, 1.13f)
                : UnityEngine.Random.Range(0.94f, 1.06f);
            float volJitter = UnityEngine.Random.Range(0.90f, 1.08f);

            src.volume = Mathf.Clamp01(volume * volJitter);
            src.pitch = Mathf.Clamp(pitch * pitchJitter, 0.5f, 2.0f);

            // 2. Micro-Transient Sample Offset:
            // Randomly skip the first 0% to 2.5% of the waveform so the initial "attack" transients aren't identical
            if (clip.samples > 500 && UnityEngine.Random.value > 0.35f)
            {
                int maxSampleSkip = Mathf.Min(clip.samples / 40, 600); // Up to ~13ms
                src.timeSamples = UnityEngine.Random.Range(0, maxSampleSkip);
            }

            // 3. Procedural Tone/Timbre Variation via Low-Pass Filter:
            // Randomly varies how "bright/snappy" vs "warm/muffled" each hit or step sounds!
            AudioLowPassFilter lpf = sfxObj.AddComponent<AudioLowPassFilter>();
            if (type == UltimateSoundType.Footstep || type == UltimateSoundType.GroundHit)
            {
                // Turf sounds: vary between deep soft grass and crisp turf
                lpf.cutoffFrequency = UnityEngine.Random.Range(1800f, 9500f);
                lpf.lowpassResonanceQ = UnityEngine.Random.Range(1.0f, 1.4f);
            }
            else if (type == UltimateSoundType.Catch || type == UltimateSoundType.BlockTip)
            {
                // Plastic catch/block: vary between palm-muffled thud (3.5kHz) and crisp plastic snap (18kHz)
                lpf.cutoffFrequency = UnityEngine.Random.Range(3500f, 18000f);
                lpf.lowpassResonanceQ = UnityEngine.Random.Range(1.0f, 1.6f);
            }
            else
            {
                // Throw / Pickup: subtle air tone shift
                lpf.cutoffFrequency = UnityEngine.Random.Range(4500f, 20000f);
            }

            src.Play();

            UnityEngine.Object.Destroy(sfxObj, (clip.length / Mathf.Max(0.5f, src.pitch)) + 0.15f);
        }

        private static AudioClip CreateProceduralClip(string name, float durationSec, Func<float, float, float> generator)
        {
            int sampleRate = 44100;
            int sampleCount = Mathf.Max(1, Mathf.RoundToInt(durationSec * sampleRate));
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                samples[i] = generator(t, durationSec);
            }

            AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static void TryLoadCustomWav(string filePath, ref AudioClip targetClip)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                byte[] bytes = File.ReadAllBytes(filePath);
                if (bytes.Length < 44) return;

                int channels = BitConverter.ToInt16(bytes, 22);
                int sampleRate = BitConverter.ToInt32(bytes, 24);
                int bitsPerSample = BitConverter.ToInt16(bytes, 34);

                int pos = 12;
                while (pos < bytes.Length - 8)
                {
                    string chunkId = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                    int chunkSize = BitConverter.ToInt32(bytes, pos + 4);
                    if (chunkId == "data")
                    {
                        pos += 8;
                        int bytesPerSample = bitsPerSample / 8;
                        int totalSamples = chunkSize / bytesPerSample;
                        int frameCount = totalSamples / Mathf.Max(1, channels);
                        float[] data = new float[totalSamples];

                        for (int i = 0; i < totalSamples && (pos + i * bytesPerSample + 1) < bytes.Length; i++)
                        {
                            if (bitsPerSample == 16)
                            {
                                short s = BitConverter.ToInt16(bytes, pos + i * 2);
                                data[i] = s / 32768f;
                            }
                            else if (bitsPerSample == 8)
                            {
                                data[i] = (bytes[pos + i] - 128) / 128f;
                            }
                        }

                        AudioClip custom = AudioClip.Create(Path.GetFileNameWithoutExtension(filePath), frameCount, channels, sampleRate, false);
                        custom.SetData(data, 0);
                        targetClip = custom;
                        UltimateFrisbeePlugin.ModLogger.Info($"Loaded custom sound: {filePath}");
                        return;
                    }
                    pos += 8 + chunkSize;
                }
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"Failed to load custom wav {filePath}: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // 14. DRIFT-FREE RUNNING STRIDE & CAMERA BOB
    // =========================================================================
    public class UltimateRunningStrideController : MonoBehaviour
    {
        public PlayerBody Body;
        private float _stridePhase = 0f;
        private float _lastSinSign = 1f;
        private Vector3 _lastWorldPos = Vector3.zero;
        private float _smoothedSpeed = 0f;
        private Vector3 _initialMeshLocalPos = Vector3.zero;
        private bool _capturedMeshPos = false;

        private void Start()
        {
            if (Body != null) _lastWorldPos = Body.transform.position;
            Level_GrassStadiumSetup_Patch.ExtendAllCamerasDrawDistance();
        }

        private void LateUpdate()
        {
            if (Body == null || UltimateFrisbeePlugin.IsHeadlessServer) return;

            if (Body.PlayerMesh != null && !_capturedMeshPos)
            {
                _initialMeshLocalPos = Body.PlayerMesh.transform.localPosition;
                _capturedMeshPos = true;
            }

            float dt = Mathf.Max(0.0001f, Time.deltaTime);
            Vector3 currentPos = Body.transform.position;
            Vector3 horizDelta = currentPos - _lastWorldPos;
            horizDelta.y = 0f;
            _lastWorldPos = currentPos;

            float instantSpeed = horizDelta.magnitude / dt;
            if (instantSpeed > 25f) instantSpeed = 0f;
            _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, instantSpeed, Mathf.Clamp01(dt * 12f));

            bool isGrounded = currentPos.y < 0.65f;
            bool isFrozenHolder = (UltimateNetworkSync.ClientSyncedHolderId != ulong.MaxValue &&
                                   Body.Player != null &&
                                   Body.Player.OwnerClientId == UltimateNetworkSync.ClientSyncedHolderId);

            if (isGrounded && !isFrozenHolder && _smoothedSpeed > 0.65f)
            {
                float stepFreq = Mathf.Lerp(9.0f, 15.5f, Mathf.Clamp01(_smoothedSpeed / 9.5f));
                _stridePhase += dt * stepFreq;
                if (_stridePhase > Mathf.PI * 4f) _stridePhase -= Mathf.PI * 4f;

                float bobWave = Mathf.Sin(_stridePhase);
                float speedFactor = Mathf.Clamp01((_smoothedSpeed - 0.65f) / 7.5f);

                if (_lastSinSign < 0f && bobWave >= 0f)
                {
                    float stepVol = Mathf.Lerp(0.05f, 0.16f, speedFactor);
                    float stepPitch = UnityEngine.Random.Range(0.94f, 1.06f);
                    UltimateAudioManager.PlayLocal3DSound(UltimateSoundType.Footstep, currentPos, stepVol, stepPitch);
                }
                _lastSinSign = bobWave;

                if (Body.PlayerMesh != null && _capturedMeshPos)
                {
                    float meshBounceY = Mathf.Abs(bobWave) * 0.035f * speedFactor;
                    Body.PlayerMesh.transform.localPosition = _initialMeshLocalPos + new Vector3(0f, meshBounceY, 0f);
                }
            }
            else
            {
                if (Body.PlayerMesh != null && _capturedMeshPos)
                {
                    Body.PlayerMesh.transform.localPosition = Vector3.Lerp(
                        Body.PlayerMesh.transform.localPosition,
                        _initialMeshLocalPos,
                        Mathf.Clamp01(dt * 10f));
                }
            }
        }
    }

    [HarmonyPatch(typeof(PlayerBody), "OnNetworkPostSpawn")]
    public static class PlayerBody_RunningStrideAndMuteSkateAudio_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerBody __instance)
        {
            if (__instance == null) return;

            if (NetworkRoles.IsServer)
            {
                UltimateNetworkSync.ServerBroadcastHolderState(UltimateRulesManager.Holder, UltimateNetworkSync.ClientSyncedThrowStyle);
            }


            if (!UltimateFrisbeePlugin.IsHeadlessServer)
            {
                // Attach the full-body rigged Ultimate Frisbee athlete!
                UltimatePlayerRigManager.AttachRigToPlayerBody(__instance);
                // Ensure the newly spawned PlayerCamera also has 5,000m far clip distance!
                Level_GrassStadiumSetup_Patch.ExtendAllCamerasDrawDistance();
                var runner = __instance.GetComponent<UltimateRunningStrideController>();
                if (runner == null)
                {
                    runner = __instance.gameObject.AddComponent<UltimateRunningStrideController>();
                    runner.Body = __instance;
                }

                var diveInput = __instance.GetComponent<UltimateDiveClientInput>();
                if (diveInput == null)
                {
                    diveInput = __instance.gameObject.AddComponent<UltimateDiveClientInput>();
                    diveInput.Body = __instance;
                }
            }

            foreach (var syncAudio in __instance.GetComponentsInChildren<SynchronizedAudio>(true))
            {
                if (syncAudio == null) continue;
                string nameLower = syncAudio.gameObject.name.ToLowerInvariant();
                if (nameLower.Contains("skate") || nameLower.Contains("ice") || nameLower.Contains("slide") || nameLower.Contains("scrape") || nameLower.Contains("brake"))
                {
                    StickAndPositioner_MuteAllStickAudio_Patch.RegisterAndMuteExternalAudio(syncAudio);
                }
            }

            foreach (var src in __instance.GetComponentsInChildren<AudioSource>(true))
            {
                if (src == null) continue;
                string nameLower = src.gameObject.name.ToLowerInvariant();
                if (nameLower.Contains("skate") || nameLower.Contains("ice") || nameLower.Contains("slide") || nameLower.Contains("scrape") || nameLower.Contains("brake"))
                {
                    src.mute = true;
                    src.volume = 0f;
                }
            }
        }
    }
    // =========================================================================
    // 15. ULTIMATE FRISBEE LAYOUT / DIVING MECHANIC (Adapted from DiveMod.cs)
    // =========================================================================
    public static class UltimateDiveMod
    {
        public const string DiveMessageName = "ULTIMATE/Dive";

        // Tunable physics values matching DashFall's DiveMod
        public static float MinStaminaForDive = 0.25f;
        public static float MinAxis = 0.1f;
        public static float DiveVelocity = 7.5f;                 // Forward layout burst speed (m/s)
        public static float DiveTorque = 13.5f;                  // Forward pitch torque that tips the player horizontal
        public static float DiveAutoClearIfNoFallSeconds = 0.65f;
        public static bool EnableDiveFallenDrag = true;
        public static float DiveFallenDragAmount = 2.8f;         // Turf chest-slide drag while fallen

        internal static readonly Dictionary<ulong, float> DiveStartedAt = new Dictionary<ulong, float>();
        internal static readonly HashSet<ulong> DiveToggled = new HashSet<ulong>();

        private static CustomMessagingManager _registeredCmm = null;

        public static void EnsureNetworkRegistered()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null) return;
            if (_registeredCmm == nm.CustomMessagingManager) return;

            nm.CustomMessagingManager.UnregisterNamedMessageHandler(DiveMessageName);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(DiveMessageName, OnDiveMessageReceived);
            _registeredCmm = nm.CustomMessagingManager;
        }

        public static void Dispose()
        {
            if (_registeredCmm != null)
            {
                _registeredCmm.UnregisterNamedMessageHandler(DiveMessageName);
            }
            _registeredCmm = null;
            DiveStartedAt.Clear();
            DiveToggled.Clear();
        }

        public static void ClientSendDiveAction()
        {
            EnsureNetworkRegistered();
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient || nm.CustomMessagingManager == null) return;

            if (NetworkRoles.IsServer)
            {
                Player localPlayer = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerByClientId(nm.LocalClientId);
                if (localPlayer != null && localPlayer.PlayerInput != null)
                {
                    TryStartDiveNow(localPlayer.PlayerInput);
                }
                return;
            }

            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)1);
                nm.CustomMessagingManager.SendNamedMessage(DiveMessageName, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
            }
        }

        public static void ClientSendCancelThrowAction()
        {
            EnsureNetworkRegistered();
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient || nm.CustomMessagingManager == null) return;

            if (NetworkRoles.IsServer)
            {
                Player localPlayer = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerByClientId(nm.LocalClientId);
                if (localPlayer != null)
                {
                    ServerCancelPlayerThrow(localPlayer);
                }
                return;
            }

            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)2); // 2 = Cancel Throw / Pump Fake
                nm.CustomMessagingManager.SendNamedMessage(DiveMessageName, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
            }
        }

        public static void ServerCancelPlayerThrow(Player player)
        {
            if (player == null) return;
            if (UltimateRulesManager.WindupTrackers.TryGetValue(player.OwnerClientId, out var tracker))
            {
                tracker.CancelCharge(player.PlayerBody);
            }
        }

        private static void OnDiveMessageReceived(ulong senderClientId, FastBufferReader reader)
        {
            if (!NetworkRoles.IsServer) return;
            if (MonoBehaviourSingleton<PlayerManager>.Instance == null) return;

            byte actionType = 1;
            try { reader.ReadValueSafe(out actionType); } catch { }

            Player player = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(senderClientId);
            if (player == null) return;

            if (actionType == 2)
            {
                ServerCancelPlayerThrow(player);
            }
            else if (player.PlayerInput != null)
            {
                TryStartDiveNow(player.PlayerInput);
            }
        }

        private static bool IsFaceOff() =>
            NetworkBehaviourSingleton<GameManager>.Instance != null &&
            NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.FaceOff;

        public static void TryStartDiveNow(PlayerInput input)
        {
            var player = input?.Player;
            if (player == null) return;
            var body = player.PlayerBody;
            if (body == null || body.Rigidbody == null) return;

            // Cannot layout while airborne, already fallen, during FaceOff lineup, or while holding the disc!
            if (!body.IsGrounded || body.HasFallen.Value || IsFaceOff()) return;
            if (UltimateRulesManager.Holder == player || UltimateNetworkSync.IsPlayerHoldingAnyDisc(player.OwnerClientId)) return;

            var id = player.NetworkObjectId;
            if (DiveToggled.Contains(id)) return;

            if (body.Stamina != null && body.Stamina.Value < MinStaminaForDive) return;

            DiveToggled.Add(id);
            DiveStartedAt[id] = Time.time;
            if (!IsFaceOff() && body.Stamina != null)
            {
                body.Stamina.Value = 0f;
            }

            var move = input.MoveInput.ServerValue;
            Vector3 dir = (Mathf.Abs(move.y) > MinAxis ? body.transform.forward * Mathf.Sign(move.y) : Vector3.zero)
                        + (Mathf.Abs(move.x) > MinAxis ? body.transform.right * Mathf.Sign(move.x) : Vector3.zero);
            if (dir == Vector3.zero) dir = body.transform.forward;
            dir.Normalize();

            body.Rigidbody.constraints &= ~(RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ);
            body.Rigidbody.AddForce(dir * DiveVelocity, ForceMode.VelocityChange);
            body.Rigidbody.AddTorque(Vector3.Cross(Vector3.up, dir).normalized * DiveTorque, ForceMode.VelocityChange);

            // Play a subtle turf/air whoosh sound on layout launch
            UltimateAudioManager.ServerBroadcastSound(UltimateSoundType.Throw, body.transform.position, 0.55f, 0.78f);
        }

        public static void ClearDiveState(Player player)
        {
            if (player == null) return;
            var id = player.NetworkObjectId;
            DiveToggled.Remove(id);
            DiveStartedAt.Remove(id);
        }

        public static bool IsPlayerDiving(Player player)
        {
            if (player == null) return false;
            return DiveToggled.Contains(player.NetworkObjectId);
        }
    }

    // Client-side hotkey listener (Default: F key or Mouse4/Forward Button, matching DashFall)
    public class UltimateDiveClientInput : MonoBehaviour
    {
        public PlayerBody Body;
        private float _lastDiveSendTime = -999f;
        private const float ClientDiveCooldown = 0.5f;

        private static bool IsTypingOrPaused()
        {
            if (Time.timeScale <= 0f) return true;
            if (Cursor.visible && Cursor.lockState != CursorLockMode.Locked) return true;

            try
            {
                var chat = MonoBehaviourSingleton<UIManager>.Instance?.Chat;
                if (chat != null && chat.IsFocused) return true;
            }
            catch { }

            return false;
        }

        private void Update()
        {
            if (Body == null || Body.Player == null || !Body.Player.IsLocalPlayer) return;
            if (IsTypingOrPaused()) return;

            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;

            // If holding a disc: Right-Click, R key, or Spacebar cancels the throw windup (Pump Fake!)
            if (UltimateNetworkSync.IsPlayerHoldingAnyDisc(Body.Player.OwnerClientId))
            {
                bool cancelPressed = (kb != null && (kb.rKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)) ||
                                     (mouse != null && mouse.rightButton.wasPressedThisFrame);
                if (cancelPressed)
                {
                    UltimateDiveMod.ClientSendCancelThrowAction();
                }
                return;
            }

            bool divePressed = (kb != null && kb.fKey.wasPressedThisFrame);

            if (divePressed && (Time.unscaledTime - _lastDiveSendTime) >= ClientDiveCooldown)
            {
                _lastDiveSendTime = Time.unscaledTime;
                UltimateDiveMod.ClientSendDiveAction();
            }
        }
    }

   // Slow down sprint stamina drain, auto-clear dive state, and apply turf slide drag when fallen
    [HarmonyPatch(typeof(PlayerBody), "FixedUpdate")]
    public static class PlayerBody_DiveAutoClearAndDrag_Patch
    {
        private static readonly FieldInfo _ambientDragField = AccessTools.Field(typeof(Movement), "ambientDrag")
                                                           ?? AccessTools.Field(typeof(Movement), "drag");
        private static readonly PropertyInfo _ambientDragProp = AccessTools.Property(typeof(Movement), "AmbientDrag");

        private static readonly MethodInfo _recoverMethod =
            AccessTools.Method(typeof(PlayerBody), "Server_Recover") ??
            AccessTools.Method(typeof(PlayerBody), "Server_StandUp") ??
            AccessTools.Method(typeof(PlayerBody), "Recover") ??
            AccessTools.Method(typeof(PlayerBody), "StandUp") ??
            AccessTools.Method(typeof(PlayerBody), "OnStandUp");

        // Tracks previous tick stamina per player so we can scale down sprinting stamina drain
        internal static readonly Dictionary<ulong, float> PrevStamina = new Dictionary<ulong, float>();

        // Gently damps residual angular velocity right after standing up WITHOUT locking RigidbodyConstraints
        internal static readonly Dictionary<ulong, float> PostDiveStabilizeUntil = new Dictionary<ulong, float>();

        [HarmonyPostfix]
        public static void Postfix(PlayerBody __instance)
        {
            if (!NetworkRoles.IsServer || __instance == null) return;
            var player = __instance.Player;
            if (player == null) return;
            var id = player.NetworkObjectId;
            Rigidbody rb = __instance.Rigidbody;

            // -----------------------------------------------------------------
            // 0. POST-DIVE ANGULAR DAMPING (Prevents double-trip WITHOUT locking constraints!)
            // -----------------------------------------------------------------
            if (PostDiveStabilizeUntil.TryGetValue(id, out float stabilizeUntil))
            {
                if (Time.time < stabilizeUntil && rb != null)
                {
                    // Ensure X/Z rotation constraints are NEVER left locked from anything else
                    rb.constraints &= ~(RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ);

                    // Dampen pitch/roll angular velocity so the stand-up impulse doesn't re-tip the player
                    Vector3 av = rb.angularVelocity;
                    rb.angularVelocity = new Vector3(av.x * 0.25f, av.y, av.z * 0.25f);
                }
                else
                {
                    PostDiveStabilizeUntil.Remove(id);
                }
            }

            // -----------------------------------------------------------------
            // 1. SLOW DOWN SPRINT STAMINA DEPLETION WHEN RUNNING
            // -----------------------------------------------------------------
            if (__instance.Stamina != null)
            {
                float currentStamina = __instance.Stamina.Value;
                bool isHoldingDisc = (UltimateRulesManager.Holder == player) ||
                                     UltimateNetworkSync.IsPlayerHoldingAnyDisc(player.OwnerClientId);
                bool isDiving = UltimateDiveMod.DiveToggled.Contains(id);
                bool isSprinting = __instance.IsSprinting != null && __instance.IsSprinting.Value;

                if (PrevStamina.TryGetValue(id, out float prevStamina))
                {
                    float drain = prevStamina - currentStamina;

                    // Only refund gradual sprinting drain (ignores instant 0f dive cost & throw wind-up meter)
                    if (isSprinting && !isHoldingDisc && !isDiving && drain > 0f && drain < 0.15f)
                    {
                        float mult = Mathf.Clamp(UltimateRulesManager.SprintStaminaDrainMultiplier, 0.05f, 1.0f);
                        float adjustedStamina = Mathf.Clamp01(prevStamina - (drain * mult));
                        __instance.Stamina.Value = adjustedStamina;
                        currentStamina = adjustedStamina;
                    }
                }

                PrevStamina[id] = currentStamina;
            }

            // -----------------------------------------------------------------
            // 2. DIVE DRAG, ANTI-ROLL STABILIZATION & NATURAL STAND-UP RECOVERY
            // -----------------------------------------------------------------
            if (!UltimateDiveMod.DiveToggled.Contains(id)) return;
            if (!UltimateDiveMod.DiveStartedAt.TryGetValue(id, out var t0)) return;

            float elapsed = Time.time - t0;
            bool hasFallen = __instance.HasFallen != null && __instance.HasFallen.Value;

            // Prevent sideways barrel-rolling on the grass during a layout while allowing forward pitch
            if (rb != null && hasFallen && elapsed < 1.35f)
            {
                Vector3 angVel = rb.angularVelocity;
                rb.angularVelocity = new Vector3(angVel.x * 0.85f, angVel.y * 0.25f, angVel.z * 0.25f);
            }

            // Apply turf chest-slide drag when fallen
            if (UltimateDiveMod.EnableDiveFallenDrag && hasFallen && __instance.Movement != null)
            {
                if (_ambientDragProp != null && _ambientDragProp.CanWrite)
                {
                    _ambientDragProp.SetValue(__instance.Movement, UltimateDiveMod.DiveFallenDragAmount);
                }
                else if (_ambientDragField != null)
                {
                    _ambientDragField.SetValue(__instance.Movement, UltimateDiveMod.DiveFallenDragAmount);
                }
            }

            if (elapsed < UltimateDiveMod.DiveAutoClearIfNoFallSeconds) return;

            float horizSpeed = rb != null ? new Vector2(rb.linearVelocity.x, rb.linearVelocity.z).magnitude : 0f;

            // Allow full layout animation & chest slide to finish (~1.35s, or 1.10s once slide slows down)
            bool shouldRecover = (elapsed >= 1.45f) || (elapsed >= 1.15f && horizSpeed < 1.0f);

            if (hasFallen && shouldRecover)
            {
                if (_recoverMethod != null && _recoverMethod.GetParameters().Length == 0)
                {
                    try { _recoverMethod.Invoke(__instance, null); } catch { }
                }

                if (__instance.HasFallen != null)
                {
                    __instance.HasFallen.Value = false;
                }

                if (rb != null)
                {
                    // Kill leftover tumbling spin so vanilla upright spring smoothly stands the capsule up
                    // without popping into the air or locking RigidbodyConstraints!
                    rb.angularVelocity = Vector3.zero;
                }

                PostDiveStabilizeUntil[id] = Time.time + 0.40f;
                UltimateDiveMod.ClearDiveState(player);
                return;
            }

            if (!hasFallen && __instance.IsGrounded)
            {
                UltimateDiveMod.ClearDiveState(player);
            }
        }
    }

    // Clean up per-player dive dictionaries when a player body despawns
    [HarmonyPatch(typeof(PlayerBody), "OnNetworkDespawn")]
    public static class PlayerBody_OnNetworkDespawn_DivePatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerBody __instance)
        {
            var player = __instance != null ? __instance.Player : null;
            if (player == null) return;
            var id = player.NetworkObjectId;

            UltimateDiveMod.DiveToggled.Remove(id);
            UltimateDiveMod.DiveStartedAt.Remove(id);
            PlayerBody_DiveAutoClearAndDrag_Patch.PrevStamina.Remove(id);
            PlayerBody_DiveAutoClearAndDrag_Patch.PostDiveStabilizeUntil.Remove(id);
        }
    }
    
    [HarmonyPatch(typeof(PlayerCamera), "OnNetworkPostSpawn")]
    public static class PlayerCamera_ExtendFarClip_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerCamera __instance)
        {
            if (__instance == null || UltimateFrisbeePlugin.IsHeadlessServer) return;

            foreach (Camera cam in __instance.GetComponentsInChildren<Camera>(true))
            {
                Level_GrassStadiumSetup_Patch.ApplyCameraLongDistanceSettings(cam);
            }
            Level_GrassStadiumSetup_Patch.ExtendAllCamerasDrawDistance();
        }
    }
// =========================================================================
    // 16. FULL-BODY SCULPTED ULTIMATE FRISBEE PLAYER (NO HOCKEY HELMET!)
    // =========================================================================
    public class UltimatePlayerMarker : MonoBehaviour
    {
        public PlayerTeam? LastAppliedTeam = null;
    }

    public class UltimateVanillaMeshSuppressor : MonoBehaviour
    {
        private bool _meshesCleared = false;

        public void SuppressVanillaBody()
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                // Keep ONLY the floating Username and Number text for the back of the jersey!
                if (r.gameObject.name == "Username" || r.gameObject.name == "Number") continue;

                // Hide ALL vanilla body AND vanilla hockey-helmet PlayerHead renderers!
                r.enabled = false;
                r.forceRenderingOff = true;

                if (!_meshesCleared && r.GetComponentInParent<PlayerHead>(true) == null)
                {
                    if (r is SkinnedMeshRenderer smr) smr.sharedMesh = null;
                    else if (r is MeshRenderer)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf != null) mf.sharedMesh = null;
                    }
                }
            }
            _meshesCleared = true;
        }

        private void LateUpdate() => SuppressVanillaBody();
    }

    public class UltimateSkinAndTeamSync : MonoBehaviour
    {
        private PlayerHead _vanillaHead;
        private Color _lastSkinColor = Color.clear;
        private Color _lastHairColor = Color.clear;

        public void Initialize(PlayerHead vanillaHead)
        {
            _vanillaHead = vanillaHead;
            SyncHeadColors(force: true);
        }

        private void LateUpdate()
        {
            SyncHeadColors(force: false);
        }

        public void SyncHeadColors(bool force)
        {
            if (_vanillaHead == null) return;

            Color skinCol = new Color(0.92f, 0.74f, 0.60f, 1f);
            Color hairCol = new Color(0.18f, 0.12f, 0.08f, 1f);
            bool foundSkin = false;
            bool foundHair = false;

            foreach (var r in _vanillaHead.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterial == null) continue;
                string n = (r.gameObject.name + "_" + r.sharedMaterial.name).ToLowerInvariant();
                Material m = r.sharedMaterial;
                Color c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor")
                        : m.HasProperty("_Color") ? m.GetColor("_Color")
                        : m.color;

                if (!foundHair && (n.Contains("hair") || n.Contains("beard") || n.Contains("mustache") || n.Contains("brow")))
                {
                    hairCol = c;
                    foundHair = true;
                }
                else if (!foundSkin && (n.Contains("head") || n.Contains("skin") || n.Contains("face")))
                {
                    skinCol = c;
                    foundSkin = true;
                }
            }

            if (!force &&
                Vector4.Distance(skinCol, _lastSkinColor) < 0.01f &&
                Vector4.Distance(hairCol, _lastHairColor) < 0.01f)
            {
                return;
            }

            _lastSkinColor = skinCol;
            _lastHairColor = hairCol;

            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                string objName = r.gameObject.name;
                bool isSkinObj = objName.Contains("Custom_Head") || objName.Contains("Arm") ||
                                 objName.Contains("Glove") || objName.Contains("Leg");
                bool isHairObj = objName.Contains("Custom_Hair");

                Material[] mats = r.materials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    string matName = mats[i].name;

                    if (isSkinObj || matName.IndexOf("Skin", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (mats[i].HasProperty("_BaseColor")) mats[i].SetColor("_BaseColor", skinCol);
                        if (mats[i].HasProperty("_Color")) mats[i].SetColor("_Color", skinCol);
                        mats[i].color = skinCol;
                        changed = true;
                    }
                    else if (isHairObj || matName.IndexOf("Hair", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (mats[i].HasProperty("_BaseColor")) mats[i].SetColor("_BaseColor", hairCol);
                        if (mats[i].HasProperty("_Color")) mats[i].SetColor("_Color", hairCol);
                        mats[i].color = hairCol;
                        changed = true;
                    }
                }
                if (changed) r.materials = mats;
            }
        }
    }

    public class UltimateAnimationBridge : MonoBehaviour
    {
        private Animator _animator;
        private Player _player;
        private PlayerBody _body;
        private Movement _movement;
        private Vector3 _lastPos;
        private float _smoothedSpeed;
        private bool _wasHoldingDisc;
        private UltimatePlayerMarker _marker;

        public void Initialize(Player player, PlayerBody body, Movement movement)
        {
            _animator = GetComponent<Animator>();
            if (_animator != null)
            {
                _animator.applyRootMotion = false; // Prevent Root Motion from drifting off the collider!
            }
            _player = player;
            _body = body;
            _movement = movement;
            _marker = GetComponent<UltimatePlayerMarker>();
            if (_body != null) _lastPos = _body.transform.position;
        }

        private void Update()
        {
            if (_animator == null || _player == null || _body == null) return;

            // Continuously ensure Team Blue / Team Red materials match the player's current team!
            if (_marker != null)
            {
                PlayerTeam currentTeam = UltimateRulesManager.GetTeam(_player);
                if ((currentTeam == PlayerTeam.Blue || currentTeam == PlayerTeam.Red) &&
                    _marker.LastAppliedTeam != currentTeam)
                {
                    UltimatePlayerRigManager.ApplyTeamColors(_marker, currentTeam);
                }
            }

            // Compute reliable horizontal speed from both Rigidbody velocity and world position delta
            float rbHorizSpeed = 0f;
            if (_body.Rigidbody != null)
            {
                Vector3 v = _body.Rigidbody.linearVelocity;
                rbHorizSpeed = new Vector2(v.x, v.z).magnitude;
            }

            float posDeltaSpeed = 0f;
            if (Time.deltaTime > 0.0001f)
            {
                Vector3 curH = new Vector3(_body.transform.position.x, 0f, _body.transform.position.z);
                Vector3 lastH = new Vector3(_lastPos.x, 0f, _lastPos.z);
                posDeltaSpeed = Vector3.Distance(curH, lastH) / Time.deltaTime;
                _lastPos = _body.transform.position;
            }

            float netVarSpeed = (_body.Speed != null) ? _body.Speed.Value : 0f;
            float targetSpeed = Mathf.Max(rbHorizSpeed, posDeltaSpeed, _player.IsLocalPlayer ? netVarSpeed : 0f);
            if (targetSpeed > 28f) targetSpeed = rbHorizSpeed; // Ignore teleport spikes

            bool isHoldingDisc = UltimateNetworkSync.IsPlayerHoldingAnyDisc(_player.OwnerClientId);
            bool isSprinting = _body.IsSprinting != null && _body.IsSprinting.Value && !isHoldingDisc && targetSpeed > 1.5f;

            if (isHoldingDisc)
            {
                targetSpeed = 0f; // Pivot foot locked
            }
            else if (isSprinting)
            {
                // Ensure BlendTree hits the top Sprint threshold (8.5) reliably when sprinting
                targetSpeed = Mathf.Max(targetSpeed * 1.25f, 8.5f);
            }
            else if (targetSpeed > 0.35f)
            {
                // Ensure BlendTree enters the Jog/Run threshold (4.0+) cleanly instead of sliding in Idle
                targetSpeed = Mathf.Max(targetSpeed * 1.15f, 3.8f);
            }

            _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, targetSpeed, Time.deltaTime * 14f);

            // Runtime fix for the 3.05s Pivot State exit transition in the Animator Controller:
            // The instant a player releases the disc (or if they are moving while not holding a disc),
            // snap out of Pivot State into Locomotion!
            if (_wasHoldingDisc && !isHoldingDisc)
            {
                _animator.CrossFadeInFixedTime("Locomotion", 0.12f, 0);
            }
            else if (!isHoldingDisc && _smoothedSpeed > 1.0f)
            {
                AnimatorStateInfo stateInfo = _animator.GetCurrentAnimatorStateInfo(0);
                if (stateInfo.IsName("Pivot State"))
                {
                    _animator.CrossFadeInFixedTime("Locomotion", 0.10f, 0);
                }
            }
            _wasHoldingDisc = isHoldingDisc;

            float currentTurn = 0f;
            if (_player.IsLocalPlayer && _movement != null)
            {
                currentTurn = _movement.TurnSpeed * (_movement.IsTurningLeft ? -1f : 1f);
            }

            // Only trigger Crouching Idle if nearly stationary; sliding while running causes wide-stance skating!
            bool isCrouchingIdle = _body.IsSliding != null && _body.IsSliding.Value && _smoothedSpeed < 2.0f;

            _animator.SetFloat("fSpeed", _smoothedSpeed);
            _animator.SetFloat("fTurn", currentTurn);
            _animator.SetBool("bIsSprinting", isSprinting);
            _animator.SetBool("bIsSliding", isCrouchingIdle);
            _animator.SetBool("bIsJumping", _body.IsJumping);
            _animator.SetBool("bHasFallen", (_body.HasFallen != null && _body.HasFallen.Value) || UltimateDiveMod.IsPlayerDiving(_player));
            _animator.SetBool("bIsGrounded", _body.IsGrounded);
            _animator.SetBool("bIsHoldingDisc", isHoldingDisc);

            bool isFallenOrDiving = (_body.HasFallen != null && _body.HasFallen.Value) || UltimateDiveMod.IsPlayerDiving(_player);
            _animator.SetBool("bHasFallen", isFallenOrDiving);

            // Counter-rotate the visual athlete mesh against the physics capsule's pitch/roll when fallen
            // so the Layout/Prone animation doesn't double-rotate 90° + 90° = 180°!
            if (isFallenOrDiving)
            {
                Vector3 flatForward = Vector3.ProjectOnPlane(_body.transform.forward, Vector3.up);
                if (flatForward.sqrMagnitude < 0.01f)
                    flatForward = Vector3.ProjectOnPlane(_body.transform.up, Vector3.up);
                if (flatForward.sqrMagnitude > 0.01f)
                {
                    Quaternion worldYawOnly = Quaternion.LookRotation(flatForward.normalized, Vector3.up);
                    transform.rotation = Quaternion.Slerp(transform.rotation, worldYawOnly, Time.deltaTime * 18f);
                }
            }
            else
            {
                transform.localRotation = Quaternion.Slerp(transform.localRotation, Quaternion.identity, Time.deltaTime * 18f);
            }
        }
    }

    [RequireComponent(typeof(Animator))]
    public class UltimatePlayerIKBridge : MonoBehaviour
    {
        private Animator _animator;
        private Player _player;
        private PlayerBody _body;
        private Transform _headBone;
        private Transform _spineBone;
        private Quaternion _lastHeadRot = Quaternion.identity;
        private Quaternion _lastSpineRot = Quaternion.identity;

        private Renderer[] _allBodyRenderers;

        public void Initialize(Player player, PlayerBody body)
        {
            _animator = GetComponent<Animator>();
            _player = player;
            _body = body;

            if (_animator != null && _animator.isHuman)
            {
                _headBone = _animator.GetBoneTransform(HumanBodyBones.Head);
                _spineBone = _animator.GetBoneTransform(HumanBodyBones.Spine);
            }

            // Cache all renderers on the custom athlete body so we can cleanly hide them in 1st-person
            var bodyList = new List<Renderer>();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (r.name == "Username" || r.name == "Number") continue;
                bodyList.Add(r);
            }
            _allBodyRenderers = bodyList.ToArray();
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (_animator == null || !_animator.isHuman || _player == null || _player.Stick == null || _body == null)
                return;

            try
            {
                bool isFallen = _body.HasFallen != null && _body.HasFallen.Value;
                bool isDiving = UltimateDiveMod.IsPlayerDiving(_player);

                // While initially launching into a layout dive, reach the catching hand out (weight = 0.85f),
                // but once laying prone/standing up on the turf, release IK (weight = 0f) so arms don't point into the sky!
                float ikWeight = isFallen ? (isDiving ? 0.5f : 0.0f) : 1.0f;
                if (ikWeight <= 0.01f)
                {
                    _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0f);
                    _animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0f);
                    _animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
                    _animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 0f);
                    return;
                }

                bool isLeft = _player.Handedness != null && _player.Handedness.Value == PlayerHandedness.Left;
                AvatarIKGoal throwingHand = isLeft ? AvatarIKGoal.LeftHand : AvatarIKGoal.RightHand;
                AvatarIKHint throwingElbow = isLeft ? AvatarIKHint.LeftElbow : AvatarIKHint.RightElbow;
                AvatarIKGoal offHand = isLeft ? AvatarIKGoal.RightHand : AvatarIKGoal.LeftHand;

                Vector3 handWorldPos = UltimateRulesManager.GetHandWorldPosition(_player.Stick, out Vector3 shoulderAnchor, out Vector3 shaftDir);
                float wristRoll = _player.Stick.BladeAngle * UltimateVisualTuning.WristRollMultiplier;
                Quaternion aimRot = Quaternion.LookRotation(shaftDir, Vector3.up) * Quaternion.Euler(0f, 0f, wristRoll);

                Quaternion handBoneAlign = Quaternion.Euler(0f, isLeft ? -90f : 90f, 0f);
                Quaternion finalHandIKRot = aimRot * handBoneAlign;

                _animator.SetIKPositionWeight(throwingHand, ikWeight);
                _animator.SetIKRotationWeight(throwingHand, ikWeight);
                _animator.SetIKPosition(throwingHand, handWorldPos);
                _animator.SetIKRotation(throwingHand, finalHandIKRot);

                Vector3 elbowHintPos = Vector3.Lerp(shoulderAnchor, handWorldPos, 0.45f)
                                     + Vector3.down * 0.25f
                                     + _body.transform.right * (isLeft ? -0.18f : 0.18f);
                _animator.SetIKHintPositionWeight(throwingElbow, 0.65f * ikWeight);
                _animator.SetIKHintPosition(throwingElbow, elbowHintPos);

                Vector3 offHandLocal = new Vector3(isLeft ? 0.28f : -0.28f, 1.05f, 0.14f);
                Vector3 offHandWorld = _body.transform.TransformPoint(offHandLocal);
                _animator.SetIKPositionWeight(offHand, 0.35f * ikWeight);
                _animator.SetIKRotationWeight(offHand, 0f);
                _animator.SetIKPosition(offHand, offHandWorld);
            }
            catch
            {
                _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0f);
                _animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
            }
        }

        public static bool IsLocalPlayerInFirstPerson(Player player, PlayerBody body)
        {
            if (player == null || !player.IsLocalPlayer || body == null) return false;
            Camera cam = Camera.main;
            if (cam == null) return true;

            // Use body.transform.up so when the player lays flat during a dive, the head offset rotates with them!
            Vector3 localHeadPos = body.transform.position + body.transform.up * 1.5f;
            float distToHead = Vector3.Distance(cam.transform.position, localHeadPos);
            float distToCenter = Vector3.Distance(cam.transform.position, body.transform.position + Vector3.up * 0.8f);

            return Mathf.Min(distToHead, distToCenter) < 1.85f;
        }

        private void LateUpdate()
        {
            if (_player == null || _body == null) return;

            // Hide the custom body mesh on the LocalPlayer when in 1st-person so the collar/sleeves/torso
            // never block the camera (just like SkaterMod's LocalPlayerVisibility)!
            // If the player switches to 3rd-person camera, the full body automatically appears!
            if (_player.IsLocalPlayer && _allBodyRenderers != null)
            {
                bool showBody = !IsLocalPlayerInFirstPerson(_player, _body);
                for (int i = 0; i < _allBodyRenderers.Length; i++)
                {
                    Renderer r = _allBodyRenderers[i];
                    if (r != null && r.enabled != showBody)
                    {
                        r.enabled = showBody;
                    }
                }
            }

            if (_headBone != null && _spineBone != null && _player.PlayerInput != null)
            {
                Vector2 lookAngle = _player.IsLocalPlayer
                    ? _player.PlayerInput.LookAngleInput.ClientValue
                    : _player.PlayerInput.LookAngleInput.ServerValue;

                float yaw = Mathf.Clamp(lookAngle.y, -85f, 85f);
                Quaternion targetSpine = Quaternion.Euler(0f, yaw * 0.55f, 0f);
                Quaternion targetHead = Quaternion.Euler(0f, yaw * 0.45f, 0f);

                _lastSpineRot = Quaternion.Lerp(_lastSpineRot, targetSpine, Time.deltaTime * 12f);
                _lastHeadRot = Quaternion.Lerp(_lastHeadRot, targetHead, Time.deltaTime * 12f);

                _spineBone.localRotation = _lastSpineRot;
                _headBone.localRotation = _lastHeadRot;
            }
        }
    }

    public static class UltimatePlayerRigManager
    {
        public static void AttachRigToPlayerBody(PlayerBody body)
        {
            if (body == null || UltimateAssetLoader.CustomPlayerPrefab == null) return;
            if (body.GetComponentInChildren<UltimatePlayerMarker>(true) != null) return;

            Player player = body.Player;
            if (player == null) return;

            Transform origMeshTransform = body.transform.Find("Player Mesh");
            if (origMeshTransform == null) return;

            PlayerMesh playerMesh = origMeshTransform.GetComponent<PlayerMesh>();
            if (playerMesh == null) return;

            // 1. Suppress vanilla floating hockey torso AND vanilla hockey-helmet head!
            var suppressor = origMeshTransform.GetComponent<UltimateVanillaMeshSuppressor>()
                          ?? origMeshTransform.gameObject.AddComponent<UltimateVanillaMeshSuppressor>();
            suppressor.SuppressVanillaBody();

            // 2. Instantiate our sculpted Ultimate Frisbee Athlete (which includes Custom_Head + Backward Cap!)
            GameObject athlete = UnityEngine.Object.Instantiate(UltimateAssetLoader.CustomPlayerPrefab, body.transform);
            athlete.name = "UltimateAthleteInstance";
            athlete.transform.localPosition = Vector3.zero;
            athlete.transform.localRotation = Quaternion.identity;

            UltimateAssetLoader.SmoothMeshNormals(athlete, 55f);
            UltimateAssetLoader.ApplyRuntimeMaterials(athlete, 0.15f);

            foreach (var smr in athlete.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.updateWhenOffscreen = true;
            }

            var marker = athlete.AddComponent<UltimatePlayerMarker>();

            // 3. Reparent Username & Number onto the back of the jersey (mixamorig:Spine2)
            Transform spineBone = FindChildRecursive(athlete.transform, "mixamorig:Spine2");
            if (spineBone != null)
            {
                Transform usernameTf = FindChildRecursive(origMeshTransform, "Username");
                if (usernameTf != null)
                {
                    SetActiveAndVisible(usernameTf);
                    usernameTf.SetParent(spineBone, false);
                    string uname = player.Username.Value.ToString();
                    Vector3 scale = new Vector3(1.25f, 0.5f, 1.0f);
                    if (uname.Length > 9) scale.x *= (9f / uname.Length);
                    usernameTf.localPosition = new Vector3(0.001f, 0.09f, -0.204f);
                    usernameTf.localRotation = Quaternion.Euler(4.874f, 0f, 0f);
                    usernameTf.localScale = scale;
                }

                Transform numberTf = FindChildRecursive(origMeshTransform, "Number");
                if (numberTf != null)
                {
                    SetActiveAndVisible(numberTf);
                    numberTf.SetParent(spineBone, false);
                    numberTf.localPosition = new Vector3(0f, -0.15f, -0.228f);
                    numberTf.localRotation = Quaternion.Euler(4.874f, 0f, 0f);
                    numberTf.localScale = new Vector3(0.9f, 0.8f, 1.0f);
                }
            }

            // 4. Sync Skin & Hair Colors from the hidden vanilla PlayerHead, plus attach Animation & IK Bridges
            if (playerMesh.PlayerHead != null)
            {
                athlete.AddComponent<UltimateSkinAndTeamSync>().Initialize(playerMesh.PlayerHead);
            }

            athlete.AddComponent<UltimateAnimationBridge>().Initialize(player, body, body.GetComponent<Movement>());
            athlete.AddComponent<UltimatePlayerIKBridge>().Initialize(player, body);

            // 5. Apply Team Colors to Jersey, Backward Cap, Wristbands & Socks
            ApplyTeamColors(marker, UltimateRulesManager.GetTeam(player));
        }

        public static void ApplyTeamColors(UltimatePlayerMarker marker, PlayerTeam team)
        {
            if (marker == null || marker.LastAppliedTeam == team) return;
            marker.LastAppliedTeam = team;

            Color teamColor = (team == PlayerTeam.Red)
                ? new Color(0.90f, 0.22f, 0.20f, 1f)
                : new Color(0.18f, 0.52f, 0.92f, 1f);

            foreach (var r in marker.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (r.gameObject.name == "Username" || r.gameObject.name == "Number") continue;

                bool isTeamPart = r.gameObject.name.Contains("Torso") ||
                                  r.gameObject.name.Contains("Sleeve") ||
                                  r.gameObject.name.Contains("Band") ||
                                  r.gameObject.name.Contains("Cap_") ||
                                  r.gameObject.name.Contains("Sock");

                Material[] mats = r.materials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    string mName = mats[i].name;
                    if (isTeamPart ||
                        mName.IndexOf("Jersey", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        mName.IndexOf("Band", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (mats[i].HasProperty("_BaseColor")) mats[i].SetColor("_BaseColor", teamColor);
                        if (mats[i].HasProperty("_Color")) mats[i].SetColor("_Color", teamColor);
                        mats[i].color = teamColor;
                        changed = true;
                    }
                }
                if (changed) r.materials = mats;
            }
        }

        private static void SetActiveAndVisible(Transform t)
        {
            if (t == null) return;
            t.gameObject.SetActive(true);
            foreach (var r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                Transform res = FindChildRecursive(child, name);
                if (res != null) return res;
            }
            return null;
        }
    }

    [HarmonyPatch(typeof(PlayerMesh), "SetJerseyID")]
    public static class PlayerMesh_UltimateTeamColor_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerMesh __instance, int jerseyID, PlayerTeam team)
        {
            var marker = __instance.GetComponentInParent<PlayerBody>()?.GetComponentInChildren<UltimatePlayerMarker>(true);
            if (marker != null)
            {
                UltimatePlayerRigManager.ApplyTeamColors(marker, team);
            }
        }
    }
}