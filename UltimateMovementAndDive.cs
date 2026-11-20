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
    // SAFE LMB DISC-TRACK SUPPRESSOR (ONLY WHILE HOLDING A DISC!)
    // Preserves default LMB disc tracking 100% whenever NOT holding a disc.
    // =========================================================================
    public static class UltimateDiscTrackSuppressor
    {
        private static bool _scanned = false;
        private static readonly List<MemberInfo> _inputBoolWrapperMembers = new List<MemberInfo>();
        private static readonly List<MemberInfo> _cameraBoolMembers = new List<MemberInfo>();
        private static readonly List<MemberInfo> _inputActionMembers = new List<MemberInfo>();
        private static readonly HashSet<UnityEngine.InputSystem.InputAction> _disabledActions =
            new HashSet<UnityEngine.InputSystem.InputAction>();

        public static bool ShouldSuppressTrackForLocalPlayer()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient) return false;
            return UltimateNetworkSync.IsPlayerHoldingAnyDisc(nm.LocalClientId);
        }

        private static void EnsureScanned()
        {
            if (_scanned) return;
            _scanned = true;

            ScanTypeForTrackMembers(typeof(PlayerInput), _inputBoolWrapperMembers, _inputActionMembers);
            ScanTypeForTrackMembers(typeof(PlayerCamera), _cameraBoolMembers, _inputActionMembers);
        }

        private static void ScanTypeForTrackMembers(Type type, List<MemberInfo> boolTargets, List<MemberInfo> actionTargets)
        {
            if (type == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (FieldInfo f in type.GetFields(flags))
            {
                ClassifyMember(f, f.Name, f.FieldType, boolTargets, actionTargets);
            }
            foreach (PropertyInfo p in type.GetProperties(flags))
            {
                if (!p.CanRead) continue;
                ClassifyMember(p, p.Name, p.PropertyType, boolTargets, actionTargets);
            }
        }

        private static void ClassifyMember(
            MemberInfo member,
            string name,
            Type memberType,
            List<MemberInfo> boolTargets,
            List<MemberInfo> actionTargets)
        {
            string lower = name.ToLowerInvariant();
            bool nameMatches = lower.Contains("track") || lower.Contains("lookatpuck") ||
                               lower.Contains("puckcam") || lower.Contains("puckfocus") ||
                               (lower.Contains("focus") && !lower.Contains("chat") && !lower.Contains("ui"));
            if (!nameMatches) return;

            if (memberType == typeof(UnityEngine.InputSystem.InputAction))
            {
                actionTargets.Add(member);
                return;
            }

            if (memberType == typeof(bool))
            {
                boolTargets.Add(member);
                return;
            }

            // Puck synced input wrapper pattern (e.g. TrackInput.ClientValue / ServerValue)
            PropertyInfo clientVal = memberType.GetProperty("ClientValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo clientValField = memberType.GetField("ClientValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if ((clientVal != null && clientVal.PropertyType == typeof(bool)) ||
                (clientValField != null && clientValField.FieldType == typeof(bool)))
            {
                boolTargets.Add(member);
            }
        }

        public static void ApplySuppressionState(Player localPlayer, bool suppress)
        {
            EnsureScanned();

            if (!suppress)
            {
                RestoreDisabledActions();
                return;
            }

            if (localPlayer == null) return;

            if (localPlayer.PlayerInput != null)
            {
                ZeroOutMembers(localPlayer.PlayerInput, _inputBoolWrapperMembers);
                DisableActionsOn(localPlayer.PlayerInput);
            }

            var cam = localPlayer.PlayerCamera;
            if (cam != null)
            {
                ZeroOutMembers(cam, _cameraBoolMembers);
                DisableActionsOn(cam);
            }
        }

        public static void SuppressPlayerCameraDirect(PlayerCamera cam)
        {
            if (cam == null) return;
            EnsureScanned();
            ZeroOutMembers(cam, _cameraBoolMembers);

            Player p = cam.GetComponentInParent<Player>();
            if (p != null && p.PlayerInput != null)
            {
                ZeroOutMembers(p.PlayerInput, _inputBoolWrapperMembers);
            }
        }

        public static void RestoreDisabledActions()
        {
            if (_disabledActions.Count == 0) return;
            foreach (var action in _disabledActions)
            {
                try
                {
                    if (action != null && !action.enabled) action.Enable();
                }
                catch { }
            }
            _disabledActions.Clear();
        }

        private static void DisableActionsOn(object instance)
        {
            if (instance == null) return;
            Type instType = instance.GetType();
            for (int i = 0; i < _inputActionMembers.Count; i++)
            {
                MemberInfo m = _inputActionMembers[i];
                if (m.DeclaringType == null || !m.DeclaringType.IsAssignableFrom(instType)) continue;
                try
                {
                    var action = (m is FieldInfo fi ? fi.GetValue(instance) : ((PropertyInfo)m).GetValue(instance))
                        as UnityEngine.InputSystem.InputAction;
                    if (action != null && action.enabled)
                    {
                        action.Disable();
                        _disabledActions.Add(action);
                    }
                }
                catch { }
            }
        }

        private static void ZeroOutMembers(object instance, List<MemberInfo> members)
        {
            if (instance == null) return;
            Type instType = instance.GetType();

            for (int i = 0; i < members.Count; i++)
            {
                MemberInfo m = members[i];
                if (m.DeclaringType == null || !m.DeclaringType.IsAssignableFrom(instType)) continue;
                try
                {
                    if (m is FieldInfo fi)
                    {
                        if (fi.FieldType == typeof(bool))
                        {
                            fi.SetValue(instance, false);
                        }
                        else
                        {
                            ZeroOutBoolWrapper(fi.GetValue(instance));
                        }
                    }
                    else if (m is PropertyInfo pi)
                    {
                        if (pi.PropertyType == typeof(bool) && pi.CanWrite)
                        {
                            pi.SetValue(instance, false);
                        }
                        else
                        {
                            ZeroOutBoolWrapper(pi.GetValue(instance));
                        }
                    }
                }
                catch { }
            }
        }

        private static void ZeroOutBoolWrapper(object wrapper)
        {
            if (wrapper == null) return;
            Type wt = wrapper.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var cp = wt.GetProperty("ClientValue", flags);
            if (cp != null && cp.CanWrite && cp.PropertyType == typeof(bool)) cp.SetValue(wrapper, false);
            var cf = wt.GetField("ClientValue", flags);
            if (cf != null && cf.FieldType == typeof(bool)) cf.SetValue(wrapper, false);

            var sp = wt.GetProperty("ServerValue", flags);
            if (sp != null && sp.CanWrite && sp.PropertyType == typeof(bool)) sp.SetValue(wrapper, false);
            var sf = wt.GetField("ServerValue", flags);
            if (sf != null && sf.FieldType == typeof(bool)) sf.SetValue(wrapper, false);
        }
    }

    // Intercepts PlayerCamera/PlayerController track start/performed handlers ONLY while holding a disc
    [HarmonyPatch]
    public static class DiscTrack_SuppressWhileHolding_Patch
    {
        private static readonly List<MethodBase> _targets = new List<MethodBase>();

        [HarmonyPrepare]
        public static bool Prepare()
        {
            _targets.Clear();
            Type[] candidateTypes = { typeof(PlayerCamera), typeof(PlayerController), typeof(PlayerInput) };

            foreach (Type t in candidateTypes)
            {
                if (t == null) continue;
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
                {
                    if (m == null || m.IsAbstract || m.ContainsGenericParameters) continue;
                    string lower = m.Name.ToLowerInvariant();

                    // Never block Canceled/Stop so releasing LMB always clears any active track!
                    if (lower.Contains("cancel") || lower.Contains("stop") || lower.Contains("end")) continue;

                    bool isTrackMethod =
                        lower.Contains("trackpuck") ||
                        lower.Contains("lookatpuck") ||
                        lower.Contains("puckcamera") ||
                        lower.Contains("ontrack") ||
                        (lower.Contains("track") && (lower.Contains("action") || lower.Contains("input") || lower.Contains("started") || lower.Contains("performed")));

                    if (isTrackMethod)
                    {
                        _targets.Add(m);
                    }
                }
            }
            return _targets.Count > 0;
        }

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods() => _targets;

        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !UltimateDiscTrackSuppressor.ShouldSuppressTrackForLocalPlayer();
        }
    }

    // Also zero out PlayerCamera's track state immediately before its Update/LateUpdate while holding a disc
    [HarmonyPatch]
    public static class PlayerCamera_SuppressTrackTick_Patch
    {
        private static readonly List<MethodBase> _targets = new List<MethodBase>();

        [HarmonyPrepare]
        public static bool Prepare()
        {
            _targets.Clear();
            var u = AccessTools.Method(typeof(PlayerCamera), "Update");
            var lu = AccessTools.Method(typeof(PlayerCamera), "LateUpdate");
            if (u != null) _targets.Add(u);
            if (lu != null) _targets.Add(lu);
            return _targets.Count > 0;
        }

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods() => _targets;

        [HarmonyPrefix]
        public static void Prefix(PlayerCamera __instance)
        {
            if (UltimateDiscTrackSuppressor.ShouldSuppressTrackForLocalPlayer())
            {
                UltimateDiscTrackSuppressor.SuppressPlayerCameraDirect(__instance);
            }
        }
    }

    // =========================================================================
    // CLIENT-SIDE LOCAL THROW TELEMETRY (0-Latency HUD Feed)
    // =========================================================================
    public static class UltimateLocalThrowTelemetry
    {
        public static bool IsHoldingDisc;
        public static bool IsArmed;
        public static bool WaitingForFreshLmb;
        public static UltimateThrowType ThrowType = UltimateThrowType.Backhand;
        public static float BladeRollDeg;
        public static float Coil01;
        public static float Whip01;
        public static float Power01;
        public static float Wobble01;
        public static float PivotExtension01;

        public static float LastFeedbackTime = -999f;
        public static string LastFeedbackText = "";
        public static Color LastFeedbackColor = Color.white;

        public static readonly UltimateRulesManager.ThrowMotionTracker LocalTracker =
            new UltimateRulesManager.ThrowMotionTracker();

        public static void RecordFeedback(string text, Color color)
        {
            LastFeedbackTime = Time.unscaledTime;
            LastFeedbackText = text;
            LastFeedbackColor = color;
        }

        public static void ResetHoldingState()
        {
            IsHoldingDisc = false;
            IsArmed = false;
            WaitingForFreshLmb = false;
            Coil01 = 0f;
            Whip01 = 0f;
            Power01 = 0f;
            Wobble01 = 0f;
            PivotExtension01 = 0f;
            LocalTracker.Reset();
        }
    }

    // =========================================================================
    // KEYBIND-AGNOSTIC JUMP & CROUCH/SLIDE READER (CLIENT & SERVER)
    // Never falls back to hardcoded Space/Ctrl. Reads live InputActions,
    // InputManager, PlayerInput wrappers, and Harmony-hooked native Jump calls.
    // =========================================================================
    public static class UltimateKeybindReader
    {
        private struct InputMemberBinding
        {
            public Type DeclaringType;
            public MemberInfo OuterMember;
            public MemberInfo ClientInner;
            public MemberInfo ServerInner;
            public MemberInfo ValueInner;
            public bool IsDirectBool;
            public bool IsCounter;
        }

        private static bool _membersScanned = false;
        private static bool _loggedDiagnostics = false;
        private static readonly List<InputMemberBinding> _jumpMembers = new List<InputMemberBinding>();
        private static readonly List<InputMemberBinding> _crouchMembers = new List<InputMemberBinding>();
        private static readonly List<MemberInfo> _actionContainerMembers = new List<MemberInfo>();

        private static readonly List<UnityEngine.InputSystem.InputAction> _jumpActions =
            new List<UnityEngine.InputSystem.InputAction>();
        private static readonly List<UnityEngine.InputSystem.InputAction> _crouchActions =
            new List<UnityEngine.InputSystem.InputAction>();

        // Tracks counter-based jump inputs (e.g. int/uint/byte press counters)
        private static readonly Dictionary<ulong, long> _lastJumpCounterClient = new Dictionary<ulong, long>();
        private static readonly Dictionary<ulong, long> _lastJumpCounterServer = new Dictionary<ulong, long>();

        private static float _lastActionScanTime = -999f;
        private const float ActionRescanInterval = 1.5f;

        private static Type _inputManagerType;

        public static void ResetCache()
        {
            _jumpActions.Clear();
            _crouchActions.Clear();
            _lastJumpCounterClient.Clear();
            _lastJumpCounterServer.Clear();
            _lastActionScanTime = -999f;
            _loggedDiagnostics = false;
        }

        private static void EnsureMembersScanned()
        {
            if (_membersScanned) return;
            _membersScanned = true;

            _inputManagerType = AccessTools.TypeByName("InputManager");

            ScanType(typeof(PlayerInput));
            ScanType(typeof(PlayerController));
            if (_inputManagerType != null) ScanType(_inputManagerType);
        }

        private static void ScanType(Type type)
        {
            if (type == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (FieldInfo f in type.GetFields(flags))
            {
                ClassifyMember(f, f.Name, f.FieldType);
            }
            foreach (PropertyInfo p in type.GetProperties(flags))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                ClassifyMember(p, p.Name, p.PropertyType);
            }
        }

        private static void ClassifyMember(MemberInfo member, string name, Type memberType)
        {
            if (memberType == null) return;

            if (memberType == typeof(UnityEngine.InputSystem.InputAction) ||
                memberType == typeof(UnityEngine.InputSystem.InputActionReference) ||
                memberType == typeof(UnityEngine.InputSystem.InputActionMap) ||
                memberType == typeof(UnityEngine.InputSystem.InputActionAsset) ||
                typeof(UnityEngine.InputSystem.IInputActionCollection2).IsAssignableFrom(memberType))
            {
                _actionContainerMembers.Add(member);
                return;
            }

            bool isJump = IsStrictInputMemberName(name, isJump: true);
            bool isCrouch = !isJump && IsStrictInputMemberName(name, isJump: false);
            if (!isJump && !isCrouch) return;

            var targetList = isJump ? _jumpMembers : _crouchMembers;

            if (memberType == typeof(bool))
            {
                targetList.Add(new InputMemberBinding
                {
                    DeclaringType = member.DeclaringType,
                    OuterMember = member,
                    IsDirectBool = true
                });
                return;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MemberInfo clientInner = FindInnerValueMember(memberType, "ClientValue", flags, out bool cCounter);
            MemberInfo serverInner = FindInnerValueMember(memberType, "ServerValue", flags, out bool sCounter);
            MemberInfo valueInner = FindInnerValueMember(memberType, "Value", flags, out bool vCounter);

            if (clientInner != null || serverInner != null || valueInner != null)
            {
                bool isCounter = cCounter || sCounter || vCounter;
                // Only allow integer counters for jump (not crouch)
                if (isCounter && !isJump) return;

                targetList.Add(new InputMemberBinding
                {
                    DeclaringType = member.DeclaringType,
                    OuterMember = member,
                    ClientInner = clientInner,
                    ServerInner = serverInner,
                    ValueInner = valueInner,
                    IsDirectBool = false,
                    IsCounter = isCounter
                });
            }
        }

        private static MemberInfo FindInnerValueMember(Type t, string memberName, BindingFlags flags, out bool isCounter)
        {
            isCounter = false;
            Type valType = null;
            MemberInfo found = null;

            PropertyInfo p = t.GetProperty(memberName, flags);
            if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
            {
                valType = p.PropertyType;
                found = p;
            }
            else
            {
                FieldInfo f = t.GetField(memberName, flags);
                if (f != null)
                {
                    valType = f.FieldType;
                    found = f;
                }
            }

            if (found == null || valType == null) return null;

            if (valType == typeof(bool) || valType == typeof(float))
                return found;

            if (valType == typeof(int) || valType == typeof(uint) || valType == typeof(byte) || valType == typeof(ulong))
            {
                isCounter = true;
                return found;
            }

            return null;
        }

        private static bool IsStrictInputMemberName(string name, bool isJump)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant().TrimStart('_');
            if (lower.Contains("sky") || lower.Contains("slider") || lower.Contains("time") ||
                lower.Contains("cooldown") || lower.Contains("force") || lower.Contains("vel") ||
                lower.Contains("speed") || lower.Contains("height") || lower.Contains("stamina") ||
                lower.Contains("audio") || lower.Contains("sound") || lower.Contains("can") ||
                lower.Contains("allow") || lower.Contains("has") || lower.Contains("was") ||
                lower.Contains("last") || lower.Contains("prev"))
                return false;

            if (isJump)
            {
                return lower == "jump" || lower == "jumpinput" || lower == "isjumping" ||
                       (lower.Contains("jump") && (lower.Contains("input") || lower.Contains("action") || lower.Contains("press")));
            }
            else
            {
                return lower == "slide" || lower == "slideinput" || lower == "issliding" ||
                       lower == "crouch" || lower == "crouchinput" || lower == "iscrouching" ||
                       ((lower.Contains("slide") || lower.Contains("crouch")) &&
                        (lower.Contains("input") || lower.Contains("action") || lower.Contains("press")));
            }
        }

        private static bool IsJumpActionName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            return lower.Contains("jump") && !lower.Contains("sky");
        }

        private static bool IsCrouchActionName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            if (lower.Contains("slider")) return false;
            return lower.Contains("slide") || lower.Contains("crouch") || lower.Contains("duck");
        }

        private static bool IsExcludedActionMap(UnityEngine.InputSystem.InputAction action)
        {
            string mapName = action?.actionMap?.name;
            if (string.IsNullOrEmpty(mapName)) return false;
            string lower = mapName.ToLowerInvariant();
            return lower.Contains("ui") || lower.Contains("menu") || lower.Contains("chat") || lower.Contains("debug");
        }

        private static bool HasEnabledAction(List<UnityEngine.InputSystem.InputAction> actions)
        {
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i] != null && actions[i].enabled) return true;
            }
            return false;
        }

        private static void TryRegisterAction(UnityEngine.InputSystem.InputAction action, string hintName = null)
        {
            if (action == null || IsExcludedActionMap(action)) return;

            bool isJump = IsJumpActionName(action.name) || IsJumpActionName(hintName);
            bool isCrouch = !isJump && (IsCrouchActionName(action.name) || IsCrouchActionName(hintName));

            if (isJump && !_jumpActions.Contains(action))
                _jumpActions.Add(action);
            else if (isCrouch && !_crouchActions.Contains(action))
                _crouchActions.Add(action);
        }

        private static object GetInputManagerInstance()
        {
            if (_inputManagerType == null) return null;
            try
            {
                var prop = AccessTools.Property(_inputManagerType, "Instance");
                if (prop != null) return prop.GetValue(null);
                var field = AccessTools.Field(_inputManagerType, "Instance");
                if (field != null) return field.GetValue(null);
            }
            catch { }
            return null;
        }

        private static void CollectActionsFromInstance(object instance)
        {
            if (instance == null) return;
            Type instType = instance.GetType();

            for (int i = 0; i < _actionContainerMembers.Count; i++)
            {
                MemberInfo m = _actionContainerMembers[i];
                if (m.DeclaringType == null || !m.DeclaringType.IsAssignableFrom(instType)) continue;

                try
                {
                    object val = ReadMember(m, instance);
                    if (val == null) continue;

                    if (val is UnityEngine.InputSystem.InputAction directAction)
                    {
                        TryRegisterAction(directAction, m.Name);
                    }
                    else if (val is UnityEngine.InputSystem.InputActionReference actionRef)
                    {
                        TryRegisterAction(actionRef.action, m.Name);
                    }
                    else if (val is UnityEngine.InputSystem.InputActionMap map)
                    {
                        var actions = map.actions;
                        for (int a = 0; a < actions.Count; a++) TryRegisterAction(actions[a]);
                    }
                    else if (val is UnityEngine.InputSystem.InputActionAsset asset)
                    {
                        var maps = asset.actionMaps;
                        for (int mi = 0; mi < maps.Count; mi++)
                        {
                            var actions = maps[mi].actions;
                            for (int a = 0; a < actions.Count; a++) TryRegisterAction(actions[a]);
                        }
                    }
                    else if (val is UnityEngine.InputSystem.IInputActionCollection2 collection)
                    {
                        foreach (var a in collection) TryRegisterAction(a);
                    }
                }
                catch { }
            }
        }

        private static void RefreshClientActions(Player localPlayer)
        {
            bool needsScan = _lastActionScanTime < 0f ||
                             ((Time.unscaledTime - _lastActionScanTime) >= ActionRescanInterval &&
                              (!HasEnabledAction(_jumpActions) || !HasEnabledAction(_crouchActions)));
            if (!needsScan) return;

            _lastActionScanTime = Time.unscaledTime;
            _jumpActions.Clear();
            _crouchActions.Clear();

            CollectActionsFromInstance(GetInputManagerInstance());
            if (localPlayer != null)
            {
                CollectActionsFromInstance(localPlayer.PlayerInput);
                CollectActionsFromInstance(localPlayer.GetComponent(typeof(PlayerController)));
            }

            if (!HasEnabledAction(_jumpActions) || !HasEnabledAction(_crouchActions))
            {
                try
                {
                    var enabledActions = UnityEngine.InputSystem.InputSystem.ListEnabledActions();
                    for (int i = 0; i < enabledActions.Count; i++)
                    {
                        TryRegisterAction(enabledActions[i]);
                    }
                }
                catch { }
            }

            if (!_loggedDiagnostics)
            {
                _loggedDiagnostics = true;
                LogDiagnostics();
            }
        }

        private static void LogDiagnostics()
        {
            try
            {
                string FormatActions(List<UnityEngine.InputSystem.InputAction> list)
                {
                    if (list.Count == 0) return "NONE";
                    return string.Join(", ", list.Select(a =>
                    {
                        var paths = new List<string>();
                        for (int i = 0; i < a.bindings.Count; i++) paths.Add(a.bindings[i].effectivePath);
                        return $"{a.actionMap?.name}/{a.name}[{string.Join("|", paths)}](enabled={a.enabled})";
                    }));
                }

                string jumpMem = _jumpMembers.Count == 0 ? "NONE" : string.Join(", ", _jumpMembers.Select(m => $"{m.DeclaringType.Name}.{m.OuterMember.Name}(counter={m.IsCounter})"));
                string crouchMem = _crouchMembers.Count == 0 ? "NONE" : string.Join(", ", _crouchMembers.Select(m => $"{m.DeclaringType.Name}.{m.OuterMember.Name}"));

                Debug.Log($"[UltimateKeybindReader] JumpActions={FormatActions(_jumpActions)} | CrouchActions={FormatActions(_crouchActions)} | JumpMembers={jumpMem} | CrouchMembers={crouchMem} | HookedJumpMethods={JumpInput_SkyJumpHook_Patch.HookedMethodSummary}");
            }
            catch { }
        }

        private static object ReadMember(MemberInfo m, object instance)
        {
            if (m is FieldInfo fi) return fi.GetValue(instance);
            if (m is PropertyInfo pi) return pi.GetValue(instance);
            return null;
        }

        private static bool EvaluateMembers(
            ulong playerKey,
            object instance,
            List<InputMemberBinding> members,
            bool isServer,
            out bool counterIncremented)
        {
            counterIncremented = false;
            if (instance == null) return false;
            Type instType = instance.GetType();
            bool anyHeld = false;

            for (int i = 0; i < members.Count; i++)
            {
                var b = members[i];
                if (b.DeclaringType == null || !b.DeclaringType.IsAssignableFrom(instType)) continue;

                try
                {
                    object outerVal = ReadMember(b.OuterMember, instance);
                    if (b.IsDirectBool)
                    {
                        if (outerVal is bool bv && bv) anyHeld = true;
                        continue;
                    }

                    if (outerVal == null) continue;
                    MemberInfo inner = isServer ? (b.ServerInner ?? b.ValueInner) : (b.ClientInner ?? b.ValueInner);
                    if (inner == null) continue;

                    object innerVal = ReadMember(inner, outerVal);
                    if (b.IsCounter)
                    {
                        long current = Convert.ToInt64(innerVal);
                        var cache = isServer ? _lastJumpCounterServer : _lastJumpCounterClient;
                        ulong key = (playerKey * 31UL) + (ulong)i;
                        if (cache.TryGetValue(key, out long prev) && current != prev)
                        {
                            counterIncremented = true;
                        }
                        cache[key] = current;
                    }
                    else
                    {
                        if (innerVal is bool ib && ib) anyHeld = true;
                        else if (innerVal is float f && f > 0.5f) anyHeld = true;
                    }
                }
                catch { }
            }

            return anyHeld;
        }

        public static bool IsCrouchHeldClient(Player localPlayer)
        {
            EnsureMembersScanned();
            RefreshClientActions(localPlayer);

            for (int i = 0; i < _crouchActions.Count; i++)
            {
                var action = _crouchActions[i];
                if (action != null && action.enabled && action.IsPressed()) return true;
            }

            if (localPlayer != null && localPlayer.PlayerInput != null)
            {
                if (EvaluateMembers(localPlayer.NetworkObjectId, localPlayer.PlayerInput, _crouchMembers, isServer: false, out _))
                    return true;
            }

            return false;
        }

        public static void CheckJumpClient(Player localPlayer, out bool pressedThisFrame, out bool isHeld)
        {
            EnsureMembersScanned();
            RefreshClientActions(localPlayer);

            pressedThisFrame = false;
            isHeld = false;

            for (int i = 0; i < _jumpActions.Count; i++)
            {
                var action = _jumpActions[i];
                if (action == null || !action.enabled) continue;
                if (action.WasPressedThisFrame()) pressedThisFrame = true;
                if (action.IsPressed()) isHeld = true;
            }

            if (localPlayer != null && localPlayer.PlayerInput != null)
            {
                bool held = EvaluateMembers(localPlayer.NetworkObjectId, localPlayer.PlayerInput, _jumpMembers, isServer: false, out bool counterFired);
                if (counterFired) pressedThisFrame = true;
                if (held) isHeld = true;
            }
        }

        public static bool IsCrouchHeldServer(Player player)
        {
            if (player == null || player.PlayerInput == null) return false;
            EnsureMembersScanned();
            return EvaluateMembers(player.NetworkObjectId, player.PlayerInput, _crouchMembers, isServer: true, out _);
        }

        public static bool CheckJumpServer(Player player, out bool counterFired)
        {
            counterFired = false;
            if (player == null || player.PlayerInput == null) return false;
            EnsureMembersScanned();
            return EvaluateMembers(player.NetworkObjectId, player.PlayerInput, _jumpMembers, isServer: true, out counterFired);
        }
    }

    // Intercepts Puck's native Jump callbacks/RPCs on PlayerInput, PlayerController & PlayerBody
    // so ANY rebound Jump key triggers Sky Jump immediately when crouched/recently crouched.
    [HarmonyPatch]
    public static class JumpInput_SkyJumpHook_Patch
    {
        private static readonly List<MethodBase> _targets = new List<MethodBase>();
        public static string HookedMethodSummary { get; private set; } = "NONE";

        [HarmonyPrepare]
        public static bool Prepare()
        {
            _targets.Clear();
            Type[] candidateTypes = { typeof(PlayerInput), typeof(PlayerController), typeof(PlayerBody) };

            foreach (Type t in candidateTypes)
            {
                if (t == null) continue;
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
                {
                    if (m == null || m.IsAbstract || m.IsStatic || m.IsSpecialName || m.ContainsGenericParameters)
                        continue;

                    string lower = m.Name.ToLowerInvariant();
                    if (!lower.Contains("jump")) continue;

                    if (lower.Contains("sky") || lower.Contains("cancel") || lower.Contains("stop") ||
                        lower.Contains("end") || lower.Contains("release") || lower.Contains("reset") ||
                        lower.Contains("clear") || lower.Contains("audio") || lower.Contains("sound") ||
                        lower.Contains("anim") || lower.Contains("can"))
                        continue;

                    _targets.Add(m);
                }
            }

            HookedMethodSummary = _targets.Count == 0
                ? "NONE"
                : string.Join(", ", _targets.Select(m => $"{m.DeclaringType?.Name}.{m.Name}"));

            return _targets.Count > 0;
        }

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods() => _targets;

        [HarmonyPrefix]
        public static void Prefix(object __instance, object[] __args)
        {
            if (__instance == null) return;

            // If this is a Unity InputAction callback, ignore Canceled (key release)
            if (__args != null && __args.Length == 1 &&
                __args[0] is UnityEngine.InputSystem.InputAction.CallbackContext ctx)
            {
                if (ctx.canceled) return;
            }

            Player player = null;
            if (__instance is PlayerInput pi) player = pi.Player;
            else if (__instance is PlayerBody pb) player = pb.Player;
            else if (__instance is Component comp) player = comp.GetComponentInParent<Player>() ?? comp.GetComponent<Player>();

            if (player == null) return;

            if (NetworkRoles.IsServer)
            {
                UltimateDiveMod.ServerTrySkyJump(player);
            }
            if (player.IsLocalPlayer)
            {
                UltimateDiveClientInput.OnNativeJumpDetected(player);
            }
        }
    }

    // =========================================================================
    // ULTIMATE FRISBEE LAYOUT / DIVING, STRAFE, SKY JUMP & THROW CLUTCH NETWORKING
    // =========================================================================
    public static class UltimateDiveMod
    {
        public const string DiveMessageName = "ULTIMATE/Dive";

        public static float MinStaminaForDive = 0.25f;
        public static float MinAxis = 0.1f;
        public static float DiveVelocity = 7.5f;
        public static float DiveTorque = 13.5f;
        public static float DiveAutoClearIfNoFallSeconds = 0.45f;
        public static bool EnableDiveFallenDrag = true;
        public static float DiveFallenDragAmount = 2.8f;

        internal static readonly Dictionary<ulong, float> DiveStartedAt = new Dictionary<ulong, float>();
        internal static readonly HashSet<ulong> DiveToggled = new HashSet<ulong>();

        // --- Q/E STRAFING, SKY-JUMP & LMB THROW CLUTCH STATES ---
        internal static readonly Dictionary<ulong, sbyte> ClientStrafeState = new Dictionary<ulong, sbyte>();
        internal static readonly Dictionary<ulong, bool> ClientThrowClutchHeld = new Dictionary<ulong, bool>();
        internal static readonly Dictionary<ulong, float> LastCrouchTime = new Dictionary<ulong, float>();
        internal static readonly Dictionary<ulong, float> LastSkyJumpTime = new Dictionary<ulong, float>();
        internal static readonly Dictionary<ulong, bool> WasJumpingLastTick = new Dictionary<ulong, bool>();
        internal static readonly Dictionary<ulong, float> LastGroundedTime = new Dictionary<ulong, float>();

        public static float StrafeRunSpeed = 5.8f;
        public static float StrafeSprintSpeed = 7.6f;
        public static float StrafeResponsiveness = 18.0f;
        public static float SkyJumpVerticalVelocity = 5.1f;
        public static float SkyJumpStaminaCost = 0.12f;

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
            ClientThrowClutchHeld.Clear();
            LastCrouchTime.Clear();
            LastSkyJumpTime.Clear();
            WasJumpingLastTick.Clear();
            UltimateKeybindReader.ResetCache();
            UltimateDiscTrackSuppressor.RestoreDisabledActions();
            UltimateLocalThrowTelemetry.ResetHoldingState();
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
                    TryStartDiveNow(localPlayer.PlayerInput);
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
                if (localPlayer != null) ServerCancelPlayerThrow(localPlayer);
                return;
            }

            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)2);
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
                writer.WriteValueSafe((byte)3);
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
                writer.WriteValueSafe((byte)4);
                nm.CustomMessagingManager.SendNamedMessage(DiveMessageName, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
            }
        }

        public static void ClientSendThrowClutchState(bool isPressed)
        {
            EnsureNetworkRegistered();
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsConnectedClient || nm.CustomMessagingManager == null) return;

            if (NetworkRoles.IsServer)
            {
                ClientThrowClutchHeld[nm.LocalClientId] = isPressed;
                return;
            }

            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            {
                writer.WriteValueSafe((byte)5); // 5 = LMB Throw Clutch State
                writer.WriteValueSafe((byte)(isPressed ? 1 : 0));
                nm.CustomMessagingManager.SendNamedMessage(DiveMessageName, NetworkManager.ServerClientId, writer, NetworkDelivery.Reliable);
            }
        }

        public static void ServerCancelPlayerThrow(Player player)
        {
            if (player == null) return;
            ClientThrowClutchHeld[player.OwnerClientId] = false;
            if (UltimateRulesManager.WindupTrackers.TryGetValue(player.OwnerClientId, out var tracker))
            {
                tracker.CancelArm();
            }
        }

        public static void ServerTrySkyJump(Player player)
        {
            if (player == null || player.PlayerBody == null || player.PlayerBody.Rigidbody == null) return;
            var body = player.PlayerBody;
            var rb = body.Rigidbody;
            ulong id = player.NetworkObjectId;

            bool recentlyGrounded = body.IsGrounded ||
                (LastGroundedTime.TryGetValue(id, out float lastGroundT) &&
                 (Time.time - lastGroundT) <= 0.12f &&
                 rb.linearVelocity.y >= -0.5f);

            if (IsFaceOff() || !recentlyGrounded || (body.HasFallen != null && body.HasFallen.Value)) return;
            if (UltimateRulesManager.Holder == player || UltimateNetworkSync.IsPlayerHoldingAnyDisc(player.OwnerClientId)) return;

            if (LastSkyJumpTime.TryGetValue(id, out float lastJump) && (Time.time - lastJump) < 0.65f) return;

            bool isCrouched = (body.IsSliding != null && body.IsSliding.Value) ||
                              UltimateKeybindReader.IsCrouchHeldServer(player);
            bool recentlyCrouched = LastCrouchTime.TryGetValue(id, out float crouchT) && (Time.time - crouchT) <= 0.35f;
            if (!isCrouched && !recentlyCrouched) return;

            if (body.Stamina != null && body.Stamina.Value < SkyJumpStaminaCost) return;

            LastSkyJumpTime[id] = Time.time;
            if (body.Stamina != null)
            {
                body.Stamina.Value = Mathf.Max(0f, body.Stamina.Value - SkyJumpStaminaCost);
            }

            if (body.IsSliding != null) body.IsSliding.Value = false;

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

            if (actionType == 5)
            {
                try
                {
                    reader.ReadValueSafe(out byte pressedByte);
                    ClientThrowClutchHeld[senderClientId] = (pressedByte != 0);
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

    // Client-side input listener: Q/E Strafe, LMB Throw Clutch (with Track Suppression), RMB Fake, Ctrl+Space SkyJump, F Dive
    public class UltimateDiveClientInput : MonoBehaviour
    {
        public PlayerBody Body;
        private float _lastDiveSendTime = -999f;
        private const float ClientDiveCooldown = 0.5f;
        private sbyte _lastSentStrafe = 0;
        private float _lastLocalCrouchTime = -999f;
        private bool _wasJumpHeldLastFrame = false;
        private bool _wasHoldingDisc = false;
        private bool _isLmbClutchSent = false;
        private bool _requireFreshLmbPress = false;
        private Vector3 _localPivotAnchor = Vector3.zero;
        private bool _hasLocalPivotAnchor = false;
        private float _localCatchStartTime = 0f;
        private static UltimateDiveClientInput _localInstance;
        private int _lastSkyJumpSentFrame = -1;

        public static void OnNativeJumpDetected(Player player)
        {
            var inst = _localInstance;
            if (inst == null || inst.Body == null || inst.Body.Player != player) return;
            if (IsTypingOrPaused()) return;
            if (UltimateNetworkSync.IsPlayerHoldingAnyDisc(player.OwnerClientId)) return;

            bool isCrouchedNow = (inst.Body.IsSliding != null && inst.Body.IsSliding.Value) ||
                                 UltimateKeybindReader.IsCrouchHeldClient(player);
            if (isCrouchedNow) inst._lastLocalCrouchTime = Time.unscaledTime;

            if ((Time.unscaledTime - inst._lastLocalCrouchTime) <= 0.35f &&
                inst._lastSkyJumpSentFrame != Time.frameCount)
            {
                inst._lastSkyJumpSentFrame = Time.frameCount;
                UltimateDiveMod.ClientSendSkyJumpAction();
            }
        }

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

        private void OnDisable()
        {
            _wasJumpHeldLastFrame = false;
            if (_wasHoldingDisc)
            {
                UltimateDiscTrackSuppressor.ApplySuppressionState(Body?.Player, false);
                UltimateLocalThrowTelemetry.ResetHoldingState();
                _wasHoldingDisc = false;
            }
            if (_localInstance == this) _localInstance = null;
        }

        private void Update()
        {
            if (Body == null || Body.Player == null || !Body.Player.IsLocalPlayer) return;
            _localInstance = this;

            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            Player player = Body.Player;

            bool isHolding = UltimateNetworkSync.IsPlayerHoldingAnyDisc(player.OwnerClientId);

            // Toggle LMB disc tracking suppression cleanly on state transitions
            if (isHolding != _wasHoldingDisc)
            {
                if (isHolding)
                {
                    // Just caught/picked up disc: suppress LMB disc track immediately!
                    UltimateDiscTrackSuppressor.ApplySuppressionState(player, true);

                    // Guard against accidental throw if player was holding LMB to track the incoming disc!
                    bool lmbHeldOnCatch = mouse != null && mouse.leftButton.isPressed;
                    _requireFreshLmbPress = lmbHeldOnCatch;
                    _isLmbClutchSent = false;
                    _localCatchStartTime = Time.time;
                    _hasLocalPivotAnchor = false;
                    UltimateLocalThrowTelemetry.LocalTracker.Reset();
                    UltimateDiveMod.ClientSendThrowClutchState(false);
                }
                else
                {
                    // No longer holding disc: restore LMB disc tracking immediately!
                    if (_isLmbClutchSent)
                    {
                        _isLmbClutchSent = false;
                        UltimateDiveMod.ClientSendThrowClutchState(false);
                    }
                    _requireFreshLmbPress = false;
                    _hasLocalPivotAnchor = false;
                    UltimateDiscTrackSuppressor.ApplySuppressionState(player, false);
                    UltimateLocalThrowTelemetry.ResetHoldingState();
                }
                _wasHoldingDisc = isHolding;
            }

            if (isHolding)
            {
                UltimateDiscTrackSuppressor.ApplySuppressionState(player, true);
            }

            if (IsTypingOrPaused())
            {
                _wasJumpHeldLastFrame = false;
                if (_lastSentStrafe != 0)
                {
                    _lastSentStrafe = 0;
                    UltimateDiveMod.ClientSendStrafeState(0);
                }
                if (_isLmbClutchSent)
                {
                    _isLmbClutchSent = false;
                    UltimateDiveMod.ClientSendThrowClutchState(false);
                    UltimateLocalThrowTelemetry.IsArmed = false;
                    UltimateLocalThrowTelemetry.LocalTracker.Reset();
                }
                return;
            }

            // --- 1. Q / E LATERAL STRAFE (works for BOTH Running & Pivot Lunge!) ---
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

            // --- 2. HOLDING DISC: LMB CLUTCH + COIL/WHIP + PUMP FAKE + LIVE HUD TELEMETRY ---
            if (isHolding)
            {
                UltimateKeybindReader.CheckJumpClient(player, out _, out _wasJumpHeldLastFrame);
                UpdateHoldingDiscControls(player, kb, mouse);
                return;
            }

            // --- 3. CROUCH + JUMP (RESPECTS CUSTOM BASE-GAME KEYBINDS) SKY JUMP ---
            bool isCrouchHeld = UltimateKeybindReader.IsCrouchHeldClient(player) ||
                                (Body.IsSliding != null && Body.IsSliding.Value);
            if (isCrouchHeld)
            {
                _lastLocalCrouchTime = Time.unscaledTime;
            }

            UltimateKeybindReader.CheckJumpClient(player, out bool jumpPressedThisFrame, out bool isJumpHeld);
            bool jumpTriggered = jumpPressedThisFrame || (isJumpHeld && !_wasJumpHeldLastFrame);
            _wasJumpHeldLastFrame = isJumpHeld;

            if (jumpTriggered &&
                (Time.unscaledTime - _lastLocalCrouchTime) <= 0.35f &&
                _lastSkyJumpSentFrame != Time.frameCount)
            {
                _lastSkyJumpSentFrame = Time.frameCount;
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

        private void UpdateHoldingDiscControls(
            Player player,
            UnityEngine.InputSystem.Keyboard kb,
            UnityEngine.InputSystem.Mouse mouse)
        {
            if (player.Stick == null || player.PlayerInput == null) return;

            bool isLeft = player.Handedness != null && player.Handedness.Value == PlayerHandedness.Left;
            Vector2 stickAngle = player.PlayerInput.StickRaycastOriginAngleInput.ClientValue;
            float bladeAngle = player.Stick.BladeAngle;

            // Approximate local pivot anchor once momentum stop settles (or read exact anchor if listen server)
            if (UltimateRulesManager.TryGetPivotAnchor(player.OwnerClientId, out Vector3 exactAnchor))
            {
                _localPivotAnchor = exactAnchor;
                _hasLocalPivotAnchor = true;
            }
            else if (!_hasLocalPivotAnchor && (Time.time - _localCatchStartTime) >= 0.45f)
            {
                _localPivotAnchor = Body.transform.position;
                _hasLocalPivotAnchor = true;
            }

            float pivotExt01 = 0f;
            if (_hasLocalPivotAnchor)
            {
                Vector3 delta = Body.transform.position - _localPivotAnchor;
                delta.y = 0f;
                pivotExt01 = Mathf.Clamp01(delta.magnitude / UltimateRulesManager.PivotMaxRadius);
            }

            UltimateThrowType liveType = UltimateRulesManager.DetermineThrowType(
                Body, player.Stick, stickAngle, isLeft, out _);

            UltimateLocalThrowTelemetry.IsHoldingDisc = true;
            UltimateLocalThrowTelemetry.ThrowType = liveType;
            UltimateLocalThrowTelemetry.BladeRollDeg = bladeAngle * UltimateVisualTuning.WristRollMultiplier;
            UltimateLocalThrowTelemetry.PivotExtension01 = pivotExt01;
            UltimateLocalThrowTelemetry.WaitingForFreshLmb = _requireFreshLmbPress;

            bool lmbPressed = mouse != null && mouse.leftButton.isPressed;
            bool cancelPressed = (kb != null && (kb.rKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)) ||
                                 (mouse != null && mouse.rightButton.wasPressedThisFrame);

            // Pump Fake / Cancel while armed
            if (cancelPressed && _isLmbClutchSent)
            {
                _isLmbClutchSent = false;
                _requireFreshLmbPress = lmbPressed;
                UltimateDiveMod.ClientSendCancelThrowAction();
                UltimateLocalThrowTelemetry.IsArmed = false;
                UltimateLocalThrowTelemetry.Coil01 = 0f;
                UltimateLocalThrowTelemetry.Whip01 = 0f;
                UltimateLocalThrowTelemetry.Power01 = 0f;
                UltimateLocalThrowTelemetry.Wobble01 = 0f;
                UltimateLocalThrowTelemetry.LocalTracker.Reset();
                UltimateLocalThrowTelemetry.RecordFeedback("PUMP FAKE", new Color(0.55f, 0.78f, 1.0f));
                return;
            }

            if (_requireFreshLmbPress)
            {
                if (!lmbPressed)
                {
                    _requireFreshLmbPress = false;
                }
                UltimateLocalThrowTelemetry.IsArmed = false;
                return;
            }

            // LMB Pressed -> Arm Throw!
            if (lmbPressed && !_isLmbClutchSent)
            {
                _isLmbClutchSent = true;
                UltimateDiveMod.ClientSendThrowClutchState(true);
                UltimateLocalThrowTelemetry.LocalTracker.BeginArm(Body, player.Stick, stickAngle, bladeAngle, liveType);
            }
            // LMB Held -> Update Live Coil, Whip & Wobble Telemetry!
            else if (lmbPressed && _isLmbClutchSent)
            {
                UltimateLocalThrowTelemetry.LocalTracker.UpdateArm(
                    Body, player.Stick, stickAngle, bladeAngle, isLeft, liveType, pivotExt01, Time.deltaTime);
            }
            // LMB Released -> Fire Throw!
            else if (!lmbPressed && _isLmbClutchSent)
            {
                var tr = UltimateLocalThrowTelemetry.LocalTracker;
                float pct = Mathf.RoundToInt(tr.GetCombinedThrowPower(liveType, pivotExt01) * 100f);
                float wob = tr.EstimatedWobble;
                float snap = tr.PeakWhip;

                string tag;
                Color col;
                if (wob >= 0.42f)
                {
                    tag = $"{liveType.ToString().ToUpperInvariant()} · {pct}% · WOBBLY";
                    col = new Color(1.0f, 0.58f, 0.22f);
                }
                else if (pct >= 75f)
                {
                    tag = $"{liveType.ToString().ToUpperInvariant()} · {pct}% · FULL POWER";
                    col = new Color(1.0f, 0.85f, 0.30f);
                }
                else if (snap >= 0.55f)
                {
                    tag = $"{liveType.ToString().ToUpperInvariant()} · {pct}% · CLEAN SNAP";
                    col = new Color(0.24f, 0.92f, 0.54f);
                }
                else
                {
                    tag = $"{liveType.ToString().ToUpperInvariant()} · {pct}% · TOUCH";
                    col = new Color(0.60f, 0.85f, 1.0f);
                }

                UltimateLocalThrowTelemetry.RecordFeedback(tag, col);

                _isLmbClutchSent = false;
                UltimateDiveMod.ClientSendThrowClutchState(false);
                tr.Reset();
            }

            var localTr = UltimateLocalThrowTelemetry.LocalTracker;
            UltimateLocalThrowTelemetry.IsArmed = _isLmbClutchSent;
            UltimateLocalThrowTelemetry.Coil01 = _isLmbClutchSent ? localTr.PeakCoil : 0f;
            UltimateLocalThrowTelemetry.Whip01 = _isLmbClutchSent ? localTr.PeakWhip : 0f;
            UltimateLocalThrowTelemetry.Power01 = _isLmbClutchSent ? localTr.GetCombinedThrowPower(liveType, pivotExt01) : 0f;
            UltimateLocalThrowTelemetry.Wobble01 = _isLmbClutchSent ? localTr.EstimatedWobble : 0f;
        }
    }

    // Active Pivot Lunge, Q/E Strafe, Sprint Stamina & Dive Recovery
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
            // 0B. ACTIVE PIVOT STEPPING / LUNGE AROUND PLANTED PIVOT FOOT
            // -----------------------------------------------------------------
            if (isHoldingDisc && !isFaceOff && rb != null &&
                UltimateRulesManager.TryGetPivotAnchor(player.OwnerClientId, out Vector3 pivotAnchor))
            {
                SetPlayerSkateTraction(id, __instance, 0f);
                if (__instance.IsSprinting != null) __instance.IsSprinting.Value = false;

                Vector2 move = player.PlayerInput != null ? player.PlayerInput.MoveInput.ServerValue : Vector2.zero;
                sbyte qeStrafe = UltimateDiveMod.ClientStrafeState.TryGetValue(player.OwnerClientId, out sbyte sdPivot) ? sdPivot : (sbyte)0;

                float fwdInput = Mathf.Clamp(move.y, -1f, 1f);
                float latInput = qeStrafe != 0 ? qeStrafe : Mathf.Clamp(move.x, -1f, 1f);

                Vector3 fwdDir = Vector3.ProjectOnPlane(__instance.transform.forward, Vector3.up).normalized;
                Vector3 rightDir = Vector3.ProjectOnPlane(__instance.transform.right, Vector3.up).normalized;

                Vector3 lungeDir = (fwdDir * fwdInput) + (rightDir * latInput);
                if (lungeDir.magnitude > 1f) lungeDir.Normalize();

                Vector3 targetWorldPos = pivotAnchor + lungeDir * UltimateRulesManager.PivotMaxRadius;

                // Keep pivot lunge strictly in-bounds (and behind the goal line if caught in central zone)
                if (!UltimateRulesManager.IsWarmup && !UltimateRulesManager.IsPullPlay)
                {
                    float maxX = UltimateRulesManager.FieldHalfWidth - 0.18f;
                    float maxZ = UltimateRulesManager.EndzoneZThreshold;
                    targetWorldPos.x = Mathf.Clamp(targetWorldPos.x, -maxX, maxX);
                    targetWorldPos.z = Mathf.Clamp(targetWorldPos.z, -maxZ, maxZ);
                }

                Vector3 currentPos = rb.position;
                Vector3 toTarget = targetWorldPos - currentPos;
                toTarget.y = 0f;

                Vector3 desiredHorizVel = Vector3.ClampMagnitude(toTarget * 11.5f, UltimateRulesManager.PivotLungeSpeed);
                rb.linearVelocity = new Vector3(desiredHorizVel.x, rb.linearVelocity.y, desiredHorizVel.z);

                // Hard radius tether so markers/physics can never shove the thrower off their planted foot
                Vector3 fromAnchor = currentPos - pivotAnchor;
                fromAnchor.y = 0f;
                float distFromAnchor = fromAnchor.magnitude;
                float maxAllowedDist = UltimateRulesManager.PivotMaxRadius + 0.04f;
                if (distFromAnchor > maxAllowedDist)
                {
                    Vector3 clampedPos = pivotAnchor + (fromAnchor / distFromAnchor) * maxAllowedDist;
                    clampedPos.y = currentPos.y;
                    rb.position = clampedPos;
                    __instance.transform.position = clampedPos;
                }
                return;
            }

            // -----------------------------------------------------------------
            // 1. TRACK GROUNDED, CROUCH (SLIDING) & JUMP INPUT FOR SKY JUMPS
            // -----------------------------------------------------------------
            if (__instance.IsGrounded)
            {
                UltimateDiveMod.LastGroundedTime[id] = Time.time;
            }

            if ((__instance.IsSliding != null && __instance.IsSliding.Value) ||
                UltimateKeybindReader.IsCrouchHeldServer(player))
            {
                UltimateDiveMod.LastCrouchTime[id] = Time.time;
            }

            bool serverJumpHeld = UltimateKeybindReader.CheckJumpServer(player, out bool serverJumpCounter);
            bool isJumpingNow = __instance.IsJumping || serverJumpHeld;
            bool wasJumping = UltimateDiveMod.WasJumpingLastTick.TryGetValue(id, out bool wj) && wj;
            UltimateDiveMod.WasJumpingLastTick[id] = isJumpingNow;

            if ((serverJumpCounter || (isJumpingNow && !wasJumping)) &&
                UltimateDiveMod.LastCrouchTime.TryGetValue(id, out float lastCrouchT) &&
                (Time.time - lastCrouchT) <= 0.35f)
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
                SetPlayerSkateTraction(id, __instance, 0f);

                Vector3 rightDir = Vector3.ProjectOnPlane(__instance.transform.right, Vector3.up).normalized;
                Vector3 forwardDir = Vector3.ProjectOnPlane(__instance.transform.forward, Vector3.up).normalized;

                Vector3 vel = rb.linearVelocity;
                float forwardSpeed = Vector3.Dot(vel, forwardDir);
                float currentRightSpeed = Vector3.Dot(vel, rightDir);

                bool hasSprintStamina = __instance.Stamina == null || __instance.Stamina.Value > 0.01f;
                float maxAllowedHorizSpeed = (isSprinting && hasSprintStamina)
                    ? UltimateDiveMod.StrafeSprintSpeed
                    : UltimateDiveMod.StrafeRunSpeed;

                float forwardRatio = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / maxAllowedHorizSpeed);
                float diagScale = Mathf.Sqrt(Mathf.Max(0.5f, 1f - (forwardRatio * forwardRatio * 0.5f)));

                float targetRightSpeed = strafeDir * maxAllowedHorizSpeed * diagScale;
                float newRightSpeed = Mathf.MoveTowards(
                    currentRightSpeed,
                    targetRightSpeed,
                    UltimateDiveMod.StrafeResponsiveness * Time.fixedDeltaTime
                );

                Vector3 newHorizVel = (forwardDir * forwardSpeed) + (rightDir * newRightSpeed);
                if (newHorizVel.magnitude > maxAllowedHorizSpeed)
                {
                    newHorizVel = newHorizVel.normalized * maxAllowedHorizSpeed;
                }

                rb.linearVelocity = new Vector3(newHorizVel.x, vel.y, newHorizVel.z);
            }
            else
            {
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
                    _ambientDragProp.SetValue(__instance.Movement, UltimateDiveMod.DiveFallenDragAmount);
                else if (_ambientDragField != null)
                    _ambientDragField.SetValue(__instance.Movement, UltimateDiveMod.DiveFallenDragAmount);
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

                if (__instance.HasFallen != null) __instance.HasFallen.Value = false;
                if (rb != null) rb.angularVelocity = Vector3.zero;

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
            UltimateDiveMod.ClientThrowClutchHeld.Remove(player.OwnerClientId);
            UltimateRulesManager.ClearPivotAnchor(player.OwnerClientId);
            UltimateDiveMod.LastCrouchTime.Remove(id);
            UltimateDiveMod.LastSkyJumpTime.Remove(id);
            UltimateDiveMod.LastGroundedTime.Remove(id);
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
