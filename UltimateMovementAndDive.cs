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
    // =========================================================================
    // RUNNING / TURF MOVEMENT PATCHES
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
        private bool _checkedAnimator = false;
        private bool _hasAnimatorFootsteps = false;

        private void Start()
        {
            if (Body != null) _lastWorldPos = Body.transform.position;
            Level_GrassStadiumSetup_Patch.ExtendAllCamerasDrawDistance();
        }

        private void LateUpdate()
        {
            if (Body == null || UltimateFrisbeePlugin.IsHeadlessServer) return;

            if (!_checkedAnimator)
            {
                _hasAnimatorFootsteps = Body.GetComponentInChildren<UltimateAnimationBridge>(true) != null;
                _checkedAnimator = true;
            }

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

                // Only play fallback sine-wave footsteps if the full-body Animator rig is NOT active
                if (!_hasAnimatorFootsteps && _lastSinSign < 0f && bobWave >= 0f)
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
                UltimateAppearanceManager.ServerBroadcastAllAppearances();
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
    // ULTIMATE FRISBEE LAYOUT / DIVING MECHANIC
    // =========================================================================
    public static class UltimateDiveMod
    {
        public const string DiveMessageName = "ULTIMATE/Dive";

        // Tunable physics values matching DashFall's DiveMod
        public static float MinStaminaForDive = 0.25f;
        public static float MinAxis = 0.1f;
        public static float DiveVelocity = 7.5f;                 // Forward layout burst speed (m/s)
        public static float DiveTorque = 13.5f;                  // Forward pitch torque that tips the player horizontal
        public static float DiveAutoClearIfNoFallSeconds = 0.45f;
        public static bool EnableDiveFallenDrag = true;
        public static float DiveFallenDragAmount = 2.8f;         // Turf chest-slide drag while fallen

        internal static readonly Dictionary<ulong, float> DiveStartedAt = new Dictionary<ulong, float>();
        internal static readonly HashSet<ulong> DiveToggled = new HashSet<ulong>();

        // --- Q/E STRAFING & CROUCH SKY-JUMP SETTINGS ---
        internal static readonly Dictionary<ulong, sbyte> ClientStrafeState = new Dictionary<ulong, sbyte>();
        internal static readonly Dictionary<ulong, float> LastCrouchTime = new Dictionary<ulong, float>();
        internal static readonly Dictionary<ulong, float> LastSkyJumpTime = new Dictionary<ulong, float>();
        internal static readonly Dictionary<ulong, bool> WasJumpingLastTick = new Dictionary<ulong, bool>();

        public static float StrafeRunSpeed = 5.8f;          // Normal sideways run speed (m/s)
        public static float StrafeSprintSpeed = 7.6f;       // Sprint sideways speed (m/s)
        public static float StrafeResponsiveness = 18.0f;   // How quickly you reach strafe speed
        public static float SkyJumpVerticalVelocity = 5.1f; // High vertical spring for sky battles!
        public static float SkyJumpStaminaCost = 0.12f;     // Stamina used by a crouch-spring jump

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
            ClientStrafeState.Clear();
            LastCrouchTime.Clear();
            LastSkyJumpTime.Clear();
            WasJumpingLastTick.Clear();
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

        public static void ClientSendStrafeState(sbyte strafeDir)
        {
            EnsureNetworkRegistered();
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient || nm.CustomMessagingManager == null) return;

            if (NetworkRoles.IsServer)
            {
                ClientStrafeState[nm.LocalClientId] = strafeDir;
                return;
            }

            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)3); // 3 = Q/E Strafe State
                writer.WriteValueSafe(strafeDir);
                nm.CustomMessagingManager.SendNamedMessage(DiveMessageName, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
            }
        }

        public static void ClientSendSkyJumpAction()
        {
            EnsureNetworkRegistered();
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient || nm.CustomMessagingManager == null) return;

            if (NetworkRoles.IsServer)
            {
                Player localPlayer = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerByClientId(nm.LocalClientId);
                if (localPlayer != null) ServerTrySkyJump(localPlayer);
                return;
            }

            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)4); // 4 = Crouch-Spring Sky Jump
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

        public static void ServerTrySkyJump(Player player)
        {
            if (player == null || player.PlayerBody == null || player.PlayerBody.Rigidbody == null) return;
            var body = player.PlayerBody;
            var rb = body.Rigidbody;
            ulong id = player.NetworkObjectId;

            if (IsFaceOff() || !body.IsGrounded || (body.HasFallen != null && body.HasFallen.Value)) return;
            if (UltimateRulesManager.Holder == player || UltimateNetworkSync.IsPlayerHoldingAnyDisc(player.OwnerClientId)) return;

            // Cooldown check (0.65s between sky jumps)
            if (LastSkyJumpTime.TryGetValue(id, out float lastJump) && (Time.time - lastJump) < 0.65f) return;

            // Must currently be crouching (Ctrl) OR have released Ctrl within the last 0.30s
            bool isCrouched = body.IsSliding != null && body.IsSliding.Value;
            bool recentlyCrouched = LastCrouchTime.TryGetValue(id, out float crouchT) && (Time.time - crouchT) <= 0.30f;
            if (!isCrouched && !recentlyCrouched) return;

            if (body.Stamina != null && body.Stamina.Value < SkyJumpStaminaCost) return;

            LastSkyJumpTime[id] = Time.time;
            if (body.Stamina != null)
            {
                body.Stamina.Value = Mathf.Max(0f, body.Stamina.Value - SkyJumpStaminaCost);
            }

            // Un-crouch the player so they spring upright into the air
            if (body.IsSliding != null) body.IsSliding.Value = false;

            // Apply high vertical launch velocity while preserving horizontal run/strafe momentum!
            Vector3 vel = rb.linearVelocity;
            vel.y = SkyJumpVerticalVelocity;
            rb.linearVelocity = vel;

            UltimateAudioManager.ServerBroadcastSound(UltimateSoundType.Throw, body.transform.position, 0.45f, 1.15f);
        }

        private static void OnDiveMessageReceived(ulong senderClientId, FastBufferReader reader)
        {
            if (!NetworkRoles.IsServer) return;
            if (MonoBehaviourSingleton<PlayerManager>.Instance == null) return;

            byte actionType = 1;
            try { reader.ReadValueSafe(out actionType); } catch { }

            if (actionType == 3)
            {
                try
                {
                    reader.ReadValueSafe(out sbyte strafeDir);
                    ClientStrafeState[senderClientId] = strafeDir;
                }
                catch { }
                return;
            }

            Player player = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(senderClientId);
            if (player == null) return;

            if (actionType == 2)
            {
                ServerCancelPlayerThrow(player);
            }
            else if (actionType == 4)
            {
                ServerTrySkyJump(player);
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
            sbyte strafe = ClientStrafeState.TryGetValue(player.OwnerClientId, out sbyte s) ? s : (sbyte)0;
            float horizAxis = Mathf.Abs(move.x) > MinAxis ? Mathf.Sign(move.x) : strafe;

            Vector3 dir = (Mathf.Abs(move.y) > MinAxis ? body.transform.forward * Mathf.Sign(move.y) : Vector3.zero)
                        + (Mathf.Abs(horizAxis) > MinAxis ? body.transform.right * horizAxis : Vector3.zero);
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
        private sbyte _lastSentStrafe = 0;
        private float _lastLocalCrouchTime = -999f;

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

            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;

            if (IsTypingOrPaused())
            {
                if (_lastSentStrafe != 0)
                {
                    _lastSentStrafe = 0;
                    UltimateDiveMod.ClientSendStrafeState(0);
                }
                return;
            }

            // --- 1. Q / E LATERAL STRAFE INPUT ---
            sbyte currentStrafe = 0;
            if (kb != null)
            {
                if (kb.qKey.isPressed) currentStrafe -= 1;
                if (kb.eKey.isPressed) currentStrafe += 1;
            }
            if (currentStrafe != _lastSentStrafe)
            {
                _lastSentStrafe = currentStrafe;
                UltimateDiveMod.ClientSendStrafeState(currentStrafe);
            }

            // --- 2. IF HOLDING DISC: PUMP FAKE (Right-Click / R / Space) ---
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

            // --- 3. CROUCH + JUMP (CTRL + SPACE) SKY JUMP ---
            bool isCrouchHeld = (kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)) ||
                                (Body.IsSliding != null && Body.IsSliding.Value);
            if (isCrouchHeld)
            {
                _lastLocalCrouchTime = Time.unscaledTime;
            }

            if (kb != null && kb.spaceKey.wasPressedThisFrame && (Time.unscaledTime - _lastLocalCrouchTime) <= 0.30f)
            {
                UltimateDiveMod.ClientSendSkyJumpAction();
            }

            // --- 4. F KEY DIVE / LAYOUT ---
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
        private static readonly FieldInfo _skateTractionField = AccessTools.Field(typeof(Skate), "traction");

        private static readonly MethodInfo _recoverMethod =
            AccessTools.Method(typeof(PlayerBody), "Server_Recover") ??
            AccessTools.Method(typeof(PlayerBody), "Server_StandUp") ??
            AccessTools.Method(typeof(PlayerBody), "Recover") ??
            AccessTools.Method(typeof(PlayerBody), "StandUp") ??
            AccessTools.Method(typeof(PlayerBody), "OnStandUp");

        internal static readonly Dictionary<ulong, float> PrevStamina = new Dictionary<ulong, float>();
        internal static readonly Dictionary<ulong, float> PostDiveStabilizeUntil = new Dictionary<ulong, float>();
        internal static readonly Dictionary<ulong, Skate[]> CachedSkates = new Dictionary<ulong, Skate[]>();

        private static void SetPlayerSkateTraction(ulong id, PlayerBody body, float tractionValue)
        {
            if (_skateTractionField == null || body == null) return;
            if (!CachedSkates.TryGetValue(id, out Skate[] skates) || skates == null)
            {
                skates = body.GetComponentsInChildren<Skate>(true);
                CachedSkates[id] = skates;
            }
            for (int i = 0; i < skates.Length; i++)
            {
                if (skates[i] != null) _skateTractionField.SetValue(skates[i], tractionValue);
            }
        }

        [HarmonyPostfix]
        public static void Postfix(PlayerBody __instance)
        {
            if (!NetworkRoles.IsServer || __instance == null) return;
            var player = __instance.Player;
            if (player == null) return;
            var id = player.NetworkObjectId;
            Rigidbody rb = __instance.Rigidbody;

            // -----------------------------------------------------------------
            // 0. POST-DIVE ANGULAR DAMPING
            // -----------------------------------------------------------------
            if (PostDiveStabilizeUntil.TryGetValue(id, out float stabilizeUntil))
            {
                if (Time.time < stabilizeUntil && rb != null)
                {
                    rb.constraints &= ~(RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ);
                    Vector3 av = rb.angularVelocity;
                    rb.angularVelocity = new Vector3(av.x * 0.25f, av.y, av.z * 0.25f);
                }
                else
                {
                    PostDiveStabilizeUntil.Remove(id);
                }
            }

            bool isHoldingDisc = (UltimateRulesManager.Holder == player) ||
                                 UltimateNetworkSync.IsPlayerHoldingAnyDisc(player.OwnerClientId);
            bool isDiving = UltimateDiveMod.DiveToggled.Contains(id);
            bool isSprinting = __instance.IsSprinting != null && __instance.IsSprinting.Value;
            bool hasFallen = __instance.HasFallen != null && __instance.HasFallen.Value;
            bool isFaceOff = NetworkBehaviourSingleton<GameManager>.Instance != null &&
                             NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.FaceOff;

            // -----------------------------------------------------------------
            // 1. TRACK CROUCH (SLIDING) & VANILLA JUMP TRANSITION FOR SKY JUMPS
            // -----------------------------------------------------------------
            if (__instance.IsSliding != null && __instance.IsSliding.Value)
            {
                UltimateDiveMod.LastCrouchTime[id] = Time.time;
            }

            bool isJumpingNow = __instance.IsJumping;
            bool wasJumping = UltimateDiveMod.WasJumpingLastTick.TryGetValue(id, out bool wj) && wj;
            UltimateDiveMod.WasJumpingLastTick[id] = isJumpingNow;

            // If vanilla jump just fired within 0.30s of crouching, upgrade it to a Sky Jump!
            if (isJumpingNow && !wasJumping &&
                UltimateDiveMod.LastCrouchTime.TryGetValue(id, out float lastCrouchT) &&
                (Time.time - lastCrouchT) <= 0.30f)
            {
                UltimateDiveMod.ServerTrySkyJump(player);
            }

            // -----------------------------------------------------------------
            // 1B. Q / E LATERAL RUNNING
            // -----------------------------------------------------------------
            sbyte strafeDir = 0;
            if (UltimateDiveMod.ClientStrafeState.TryGetValue(player.OwnerClientId, out sbyte sd))
            {
                strafeDir = sd;
            }

            if (rb != null && strafeDir != 0 && !isFaceOff && !isHoldingDisc && !hasFallen && !isDiving && __instance.IsGrounded)
            {
                // Disable Skate lateral traction while holding Q/E so Skate doesn't brake our sideways velocity
                SetPlayerSkateTraction(id, __instance, 0f);

                Vector3 rightDir = Vector3.ProjectOnPlane(__instance.transform.right, Vector3.up).normalized;
                Vector3 forwardDir = Vector3.ProjectOnPlane(__instance.transform.forward, Vector3.up).normalized;

                Vector3 vel = rb.linearVelocity;
                float forwardSpeed = Vector3.Dot(vel, forwardDir);
                float currentRightSpeed = Vector3.Dot(vel, rightDir);

                // Only grant Sprint strafe speed if the player actually has stamina and is actively sprinting
                bool hasSprintStamina = __instance.Stamina == null || __instance.Stamina.Value > 0.01f;
                float maxAllowedHorizSpeed = (isSprinting && hasSprintStamina)
                    ? UltimateDiveMod.StrafeSprintSpeed
                    : UltimateDiveMod.StrafeRunSpeed;

                // If the player is also moving forward/backward (W/S), scale down target lateral speed
                // so the diagonal vector magnitude never exceeds maxAllowedHorizSpeed!
                float forwardRatio = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / maxAllowedHorizSpeed);
                float diagScale = Mathf.Sqrt(Mathf.Max(0.5f, 1f - (forwardRatio * forwardRatio * 0.5f)));

                float targetRightSpeed = strafeDir * maxAllowedHorizSpeed * diagScale;
                float newRightSpeed = Mathf.MoveTowards(
                    currentRightSpeed,
                    targetRightSpeed,
                    UltimateDiveMod.StrafeResponsiveness * Time.fixedDeltaTime
                );

                // Reconstruct horizontal velocity and strictly clamp total horizontal speed
                Vector3 newHorizVel = (forwardDir * forwardSpeed) + (rightDir * newRightSpeed);
                if (newHorizVel.magnitude > maxAllowedHorizSpeed)
                {
                    newHorizVel = newHorizVel.normalized * maxAllowedHorizSpeed;
                }

                rb.linearVelocity = new Vector3(newHorizVel.x, vel.y, newHorizVel.z);
            }
            else
            {
                // Restore normal turf traction when not actively strafing
                SetPlayerSkateTraction(id, __instance, 2.5f);
            }

            // -----------------------------------------------------------------
            // 1C. SLOW DOWN SPRINT STAMINA DEPLETION WHEN RUNNING
            // -----------------------------------------------------------------
            if (__instance.Stamina != null)
            {
                float currentStamina = __instance.Stamina.Value;

                if (PrevStamina.TryGetValue(id, out float prevStamina))
                {
                    float drain = prevStamina - currentStamina;
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

            if (rb != null && hasFallen && elapsed < 1.35f)
            {
                Vector3 angVel = rb.angularVelocity;
                rb.angularVelocity = new Vector3(angVel.x * 0.85f, angVel.y * 0.25f, angVel.z * 0.25f);
            }

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
            UltimateDiveMod.ClientStrafeState.Remove(player.OwnerClientId);
            UltimateDiveMod.LastCrouchTime.Remove(id);
            UltimateDiveMod.LastSkyJumpTime.Remove(id);
            UltimateDiveMod.WasJumpingLastTick.Remove(id);
            PlayerBody_DiveAutoClearAndDrag_Patch.CachedSkates.Remove(id);
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
}