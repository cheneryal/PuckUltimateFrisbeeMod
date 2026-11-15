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
    // ULTIMATE FRISBEE RULES, THROW MODULATION & STATE ENGINE
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

        // --- PRE-STALL / DELAY OF GAME TRACKING (Anti-Clock Drain) ---
        public static float DiscStationaryStartTime = 0f;
        public static int LastPreStallAnnounced = 0;
        public static int LastPullHoldAnnounced = 0;
        public static float PreStallDuration = 12.0f;     // 12s for offense to pick up a grounded disc
        public static float PullPreStallDuration = 15.0f; // 15s for pulling team to pick up the pull

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
            DiscStationaryStartTime = 0f;
            LastPreStallAnnounced = 0;
            UltimateRulesManager.LastPullHoldAnnounced = 0;
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
                float effectiveRoll = Mathf.Abs(wristRoll * UltimateVisualTuning.WristRollMultiplier);
                // Trigger Hammer/Scoober if aiming overhead OR if the wrist is rolled upside-down (> 105 deg)
                if (stickAngle.x < -10f || effectiveRoll > 105f)
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
    // SERVER DISC PHYSICS, STAMINA-POWERED THROWS & RULES (Puck.FixedUpdate)
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

        private static float _lastWindTickTime = -1f;

        private static void UpdateGroundedDiscPreStall(Puck disc, Rigidbody rb)
        {
            if (UltimateRulesManager.IsWarmup || UltimateRulesManager.Holder != null || UltimateRulesManager.IsDiscInFlight)
            {
                UltimateRulesManager.DiscStationaryStartTime = 0f;
                UltimateRulesManager.LastPreStallAnnounced = 0;
                return;
            }

            // Determine which team is currently responsible for picking up the disc
            PlayerTeam responsibleTeam = UltimateRulesManager.IsPullPlay
                ? UltimateRulesManager.PullingTeam
                : UltimateRulesManager.OffensiveTeam;

            if (responsibleTeam != PlayerTeam.Blue && responsibleTeam != PlayerTeam.Red)
                return;

            // Only tick the clock if the responsible team actually has at least 1 spawned player on the field
            if (MonoBehaviourSingleton<PlayerManager>.Instance != null)
            {
                bool hasActiveTeamMember = MonoBehaviourSingleton<PlayerManager>.Instance
                    .GetPlayers(false)
                    .Any(p => p != null && p.IsCharacterSpawned && UltimateRulesManager.GetTeam(p) == responsibleTeam);

                if (!hasActiveTeamMember)
                {
                    UltimateRulesManager.DiscStationaryStartTime = Time.time;
                    return;
                }
            }

            if (UltimateRulesManager.DiscStationaryStartTime <= 0f)
            {
                UltimateRulesManager.DiscStationaryStartTime = Time.time;
                UltimateRulesManager.LastPreStallAnnounced = 0;
            }

            float maxAllowedTime = UltimateRulesManager.IsPullPlay
                ? UltimateRulesManager.PullPreStallDuration
                : UltimateRulesManager.PreStallDuration;

            float elapsed = Time.time - UltimateRulesManager.DiscStationaryStartTime;
            int remainingSec = Mathf.CeilToInt(maxAllowedTime - elapsed);

            // Announce countdown warnings at 5, 3, 2, 1 seconds left
            if (remainingSec <= 5 && remainingSec >= 1 && remainingSec != UltimateRulesManager.LastPreStallAnnounced)
            {
                if (remainingSec == 5 || remainingSec <= 3)
                {
                    UltimateRulesManager.LastPreStallAnnounced = remainingSec;
                    string actionText = UltimateRulesManager.IsPullPlay ? "pick up to Pull" : "pick up the disc";
                    UltimateRulesManager.BroadcastAnnouncement(
                        $"DELAY OF GAME WARNING: {responsibleTeam} has {remainingSec}s to {actionText}!",
                        "#f39c12");
                }
            }

            // Time expired -> Enforce Delay of Game Turnover!
            if (elapsed >= maxAllowedTime)
            {
                UltimateRulesManager.DiscStationaryStartTime = Time.time;
                UltimateRulesManager.LastPreStallAnnounced = 0;

                PlayerTeam? opposingTeam = Utils.GetOpposingTeam(responsibleTeam);
                if (opposingTeam == null) return;

                if (UltimateRulesManager.IsPullPlay)
                {
                    // If the Pulling team refuses to pick up the Pull, award the disc to the receiving team at the ATTACKING Brick Mark!
                    UltimateRulesManager.IsPullPlay = false;
                    UltimateRulesManager.IsCurrentThrowPull = false;
                    UltimateRulesManager.LastTurnoverTeam = responsibleTeam;
                    UltimateRulesManager.OffensiveTeam = opposingTeam.Value;

                    // Blue attacks -Z (-14m), Red attacks +Z (+14m)
                    float attackingBrickZ = (opposingTeam.Value == PlayerTeam.Blue)
                        ? -UltimateRulesManager.BrickMarkZ
                        : UltimateRulesManager.BrickMarkZ;
                    Vector3 penaltySpot = new Vector3(0f, UltimateVisualTuning.DiscHalfThickness + 0.01f, attackingBrickZ);

                    FlattenAndFreezeDiscOnGround(disc, rb, penaltySpot);
                    UltimateRulesManager.BroadcastAnnouncement(
                        $"DELAY OF GAME on {responsibleTeam} (Failed to Pull)! {opposingTeam.Value} awarded disc at Attacking Brick Mark!",
                        "#e74c3c");
                }
                else
                {
                    // Live play turnover: flip possession to the other team right where the disc lies!
                    UltimateRulesManager.LastTurnoverTeam = responsibleTeam;
                    UltimateRulesManager.OffensiveTeam = opposingTeam.Value;
                    UltimateRulesManager.LastThrower = null;
                    UltimateRulesManager.SecondLastThrower = null;

                    UltimateRulesManager.BroadcastAnnouncement(
                        $"DELAY OF GAME! {responsibleTeam} failed to pick up the disc in {maxAllowedTime:F0}s -> TURNOVER! {opposingTeam.Value} Disc!",
                        "#e74c3c");
                }
            }
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
            // Tick the server wind simulation once per physics frame
            if (Time.fixedTime != _lastWindTickTime)
            {
                _lastWindTickTime = Time.fixedTime;
                UltimateWindManager.ServerFixedUpdate();
            }

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
                    else if (!isWarmup && UltimateRulesManager.IsPullPlay && holdTime > 0.5f)
                    {
                        // Give the puller 15 seconds after picking up the disc to throw the Pull!
                        const float maxPullHoldTime = 15.0f;
                        int pullRemaining = Mathf.CeilToInt(maxPullHoldTime - holdTime);

                        if (pullRemaining <= 5 && pullRemaining >= 1 && pullRemaining != UltimateRulesManager.LastPullHoldAnnounced)
                        {
                            if (pullRemaining == 5 || pullRemaining <= 3)
                            {
                                UltimateRulesManager.LastPullHoldAnnounced = pullRemaining;
                                UltimateRulesManager.BroadcastAnnouncement($"PULL CLOCK: {pullRemaining}s to throw the Pull!", "#e67e22");
                            }
                        }

                        if (holdTime >= maxPullHoldTime)
                        {
                            UltimateRulesManager.LastPullHoldAnnounced = 0;
                            tracker.Reset(holder.PlayerBody);
                            holder.PlayerBody.Server_Unfreeze();

                            PlayerTeam pullerTeam = UltimateRulesManager.GetTeam(holder);
                            PlayerTeam? receivingTeam = Utils.GetOpposingTeam(pullerTeam);

                            UltimateRulesManager.Holder = null;
                            UltimateRulesManager.IsHolderPivotLocked = false;
                            UltimateRulesManager.IsPullPlay = false;
                            UltimateRulesManager.IsCurrentThrowPull = false;
                            UltimateRulesManager.LastTurnoverTeam = pullerTeam;
                            UltimateRulesManager.OffensiveTeam = receivingTeam ?? PlayerTeam.None;
                            UltimateRulesManager.ReleaseTime = Time.time;
                            UltimateNetworkSync.ServerBroadcastHolderState(null, spinByte, __instance);

                            // Spot at the ATTACKING Brick Mark (18m in front of the pulling team's endzone!)
                            float attackingBrickZ = (receivingTeam == PlayerTeam.Blue)
                                ? -UltimateRulesManager.BrickMarkZ
                                : UltimateRulesManager.BrickMarkZ;
                            Vector3 penaltySpot = new Vector3(0f, UltimateVisualTuning.DiscHalfThickness + 0.01f, attackingBrickZ);

                            FlattenAndFreezeDiscOnGround(__instance, rb, penaltySpot);
                            UltimateRulesManager.BroadcastAnnouncement(
                                $"DELAY OF GAME on {pullerTeam} (Held Pull > 15s)! {UltimateRulesManager.OffensiveTeam} awarded disc at Attacking Brick Mark!",
                                "#e74c3c");
                            return;
                        }
                    }

                    // CRITICAL: Always return here when UltimateRulesManager.Holder != null
                    // so execution NEVER falls through to Section C (Ground Pickup)!
                    return;
                }


                // -----------------------------------------------------------------
                // B. TRACK IN-BOUNDS FLIGHT CROSSING POINT FOR ACCURATE OOB SPOTS
                // -----------------------------------------------------------------
                Vector3 discPos = __instance.transform.position;
                bool isOutOfBounds = UltimateRulesManager.IsPositionOutOfBounds(discPos);

                if (UltimateRulesManager.IsDiscInFlight && !UltimateRulesManager.IsDiscTumblingOnGround)
                {
                    if (!isOutOfBounds)
                    {
                        // Disc is currently inside the field lines (even if it curved OOB earlier and came back in!)
                        UltimateRulesManager.LastInBoundsFlightPos = discPos;
                        UltimateRulesManager.WentOutOfBoundsInFlight = false;
                    }
                    else
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
                            // Prevent machine-gun re-pickup on the exact frame a disc is released/dropped!
                            if (player == UltimateRulesManager.LastThrower && timeSinceRelease < 0.35f)
                                continue;
                            float horizFootDist = Vector2.Distance(
                                new Vector2(discPos.x, discPos.z),
                                new Vector2(bodyBasePos.x, bodyBasePos.z));
                            float horizHandDist = Vector2.Distance(
                                new Vector2(discPos.x, discPos.z),
                                new Vector2(handPos.x, handPos.z));

                            if (sweptHandDist <= 1.45f || horizHandDist <= 1.35f || horizFootDist <= 1.45f)
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
                // Only count as touching the ground when actually down at grass level!
                bool rimTouchingGround = (discPos.y <= minCenterY + 0.025f);

                if (rimTouchingGround)
                {
                    ResolveRimGroundContactAndTumble(__instance, rb, minCenterY);
                }

                if (UltimateRulesManager.IsDiscInFlight)
                {
                    if (isOutOfBounds && rimTouchingGround)
                    {
                        UltimateRulesManager.IsDiscInFlight = false;
                        UltimateRulesManager.IsDiscTumblingOnGround = false;

                        Vector3 crossingSpot = UltimateRulesManager.WentOutOfBoundsInFlight
                            ? UltimateRulesManager.LastInBoundsFlightPos
                            : discPos;

                        if (isWarmup)
                        {
                            // In Warmup: bring any disc that lands out of bounds / outside the fence back onto the sideline!
                            FlattenAndFreezeDiscOnGround(__instance, rb, GetWarmupSidelineReturnSpot(discPos));
                            return;
                        }

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
                    if (isOutOfBounds)
                    {
                        UltimateRulesManager.IsDiscInFlight = false;
                        UltimateRulesManager.IsDiscTumblingOnGround = false;

                        if (isWarmup)
                        {
                            FlattenAndFreezeDiscOnGround(__instance, rb, GetWarmupSidelineReturnSpot(discPos));
                            return;
                        }

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
                            ? GetWarmupSidelineReturnSpot(finalPos)
                            : GetLegalFieldSpotAfterTurnover(finalPos);

                        FlattenAndFreezeDiscOnGround(__instance, rb, restSpot);
                        return;
                    }
                }
                else if (!UltimateRulesManager.IsDiscInFlight && rimTouchingGround && rb.linearVelocity.sqrMagnitude < 0.05f)
                {
                    bool waitingForPullPickup = UltimateRulesManager.IsPullPlay && UltimateRulesManager.Holder == null;
                    Vector3 restSpot = waitingForPullPickup
                        ? new Vector3(discPos.x, UltimateVisualTuning.DiscHalfThickness + 0.01f, discPos.z)
                        : (isWarmup ? GetWarmupSidelineReturnSpot(discPos) : GetLegalFieldSpotAfterTurnover(discPos));

                    FlattenAndFreezeDiscOnGround(__instance, rb, restSpot);

                    if (!isWarmup)
                    {
                        UpdateGroundedDiscPreStall(__instance, rb);
                    }
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
            float maxX = UltimateRulesManager.FieldHalfWidth - 0.5f;   // 18.0m (just inside sideline)
            float maxZ = UltimateRulesManager.FieldHalfLength - 0.5f;  // 49.5m (keep Z along the sideline; WalkHolderToGoalLineIfInEndzone will walk the player up when they pick it up!)

            float legalX = Mathf.Clamp(rawSpot.x, -maxX, maxX);
            float legalZ = Mathf.Clamp(rawSpot.z, -maxZ, maxZ);

            return new Vector3(legalX, UltimateVisualTuning.DiscHalfThickness + 0.01f, legalZ);
        }

        private static bool IsOutsideFencePerimeter(Vector3 pos)
        {
            // The physical fence sits ~2.0m outside the 18.5m sideline and 50.0m backline
            return Mathf.Abs(pos.x) > (UltimateRulesManager.FieldHalfWidth + 1.6f) ||
                   Mathf.Abs(pos.z) > (UltimateRulesManager.FieldHalfLength + 1.6f);
        }

        private static Vector3 GetWarmupSidelineReturnSpot(Vector3 rawSpot)
        {
            // In Warmup, if the disc is still inside the physical fence, let it rest right where it is!
            // Only snap it back onto the sideline if it flew over/outside the fence where players can't reach it.
            if (!IsOutsideFencePerimeter(rawSpot))
            {
                return new Vector3(rawSpot.x, UltimateVisualTuning.DiscHalfThickness + 0.01f, rawSpot.z);
            }

            float sidelineX = UltimateRulesManager.FieldHalfWidth - 0.5f;   // 18.0m (just inside the sideline)
            float backlineZ = UltimateRulesManager.FieldHalfLength - 0.5f;  // 49.5m (just inside the backline)

            float legalX = Mathf.Clamp(rawSpot.x, -sidelineX, sidelineX);
            float legalZ = Mathf.Clamp(rawSpot.z, -backlineZ, backlineZ);

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

            // Start the Delay-of-Game / Pre-Stall clock as soon as the disc is stationary on the ground!
            if (UltimateRulesManager.DiscStationaryStartTime <= 0f)
            {
                UltimateRulesManager.DiscStationaryStartTime = Time.time;
                UltimateRulesManager.LastPreStallAnnounced = 0;
            }
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

            // Allow full -165 to +165 deg roll from mouse wheel so players can throw steep blades, Hammers, and Scoobers!
            float initialRollDeg = Mathf.Clamp(hyzerAngle, -165f, 165f);
            if (throwType == UltimateThrowType.Hammer && Mathf.Abs(initialRollDeg) < 110f)
            {
                // If throwing an overhead Hammer without manually rolling the mouse wheel all the way over,
                // blend your mouse-wheel angle into the inverted Hammer release angle:
                initialRollDeg = Mathf.Clamp((145f * spinSign) + (hyzerAngle * 0.25f), -170f, 170f);
            }

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
            UltimateRulesManager.DiscStationaryStartTime = 0f;
            UltimateRulesManager.LastPreStallAnnounced = 0;

            sbyte spinByte = (sbyte)(spinSign >= 0f ? 1 : -1);

            // If in Warmup, also check if this throw is aimed toward a partner so their Mouse1 tracks it too!
            Player warmupReceiver = null;
            if (UltimateRulesManager.IsWarmup && MonoBehaviourSingleton<PlayerManager>.Instance != null)
            {
                float bestScore = float.MaxValue;
                foreach (var p in MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers(false))
                {
                    if (p == null || p == thrower || !p.IsCharacterSpawned || p.PlayerBody == null) continue;
                    // Don't override a player who is currently holding a different disc
                    if (UltimateNetworkSync.IsPlayerHoldingAnyDisc(p.OwnerClientId)) continue;

                    Vector3 toPlayer = p.PlayerBody.transform.position - launchPos;
                    toPlayer.y = 0f;
                    float dist = toPlayer.magnitude;
                    if (dist > 2.0f && dist < 65.0f)
                    {
                        float angle = Vector3.Angle( new Vector3(launchDir.x, 0f, launchDir.z), toPlayer.normalized);
                        if (angle < 28f && angle < bestScore)
                        {
                            bestScore = angle;
                            warmupReceiver = p;
                        }
                    }
                }
            }

            UltimateNetworkSync.RecordPlayerDiscInteraction(thrower, disc);
            UltimateNetworkSync.ServerBroadcastHolderState(null, spinByte, disc, warmupReceiver ?? thrower);
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
            UltimateNetworkSync.RecordPlayerDiscInteraction(deflector, disc);
            UltimateNetworkSync.ServerBroadcastHolderState(null, (sbyte)(UltimateRulesManager.ActiveFlightSpinSign >= 0f ? 1 : -1), disc, deflector);

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

            // --- SEPARATE BLOCK vs BOBBLE SOUND TRIGGER ---
            UltimateSoundType sfxType = isDefensivePlay ? UltimateSoundType.Block : UltimateSoundType.Bobble;
            float sfxPitch = isHandTip ? UnityEngine.Random.Range(1.02f, 1.12f) : UnityEngine.Random.Range(0.90f, 0.98f);
            float sfxVol = isDefensivePlay ? 1.0f : 0.85f;

            UltimateAudioManager.ServerBroadcastSound(sfxType, discPos, sfxVol, sfxPitch);

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
            UltimateRulesManager.LastPullHoldAnnounced = 0;

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

            if (wasInFlight && horizSpeed > 0.65f)
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
            // Do not clamp the puller if they are behind their endzone picking up to Pull
            if (UltimateRulesManager.IsPullPlay) return;

            Vector3 pos = holder.PlayerBody.transform.position;
            float goalLineZ = UltimateRulesManager.EndzoneZThreshold;
            float maxX = UltimateRulesManager.FieldHalfWidth - 0.35f;

            // Clamp both Endzone Z AND Sideline X so picking up while standing OOB places pivot on the line!
            if (Mathf.Abs(pos.z) > goalLineZ || Mathf.Abs(pos.x) > maxX)
            {
                float clampedZ = Mathf.Clamp(pos.z, -goalLineZ, goalLineZ);
                float clampedX = Mathf.Clamp(pos.x, -maxX, maxX);
                Vector3 legalPivot = new Vector3(clampedX, pos.y, clampedZ);
                holder.PlayerBody.Server_Teleport(legalPivot, holder.PlayerBody.transform.rotation);
                holder.PlayerBody.Server_Freeze((RigidbodyConstraints)10);
            }
        }

        private static void ApplyFrisbeeAerodynamics(Puck disc, Rigidbody rb)
        {
            rb.inertiaTensor = new Vector3(0.02f, 0.08f, 0.02f);
            rb.angularVelocity = Vector3.zero;

            Vector3 vel = rb.linearVelocity;
            Vector3 windVel = UltimateWindManager.CurrentWindVelocity * UltimateWindManager.AerodynamicWindScale;

            // True aerodynamic velocity of the disc relative to the moving air mass!
            Vector3 airVel = vel - windVel;
            Vector3 horizAirVel = new Vector3(airVel.x, 0f, airVel.z);
            float horizAirSpeed = horizAirVel.magnitude;
            float totalAirSpeed = airVel.magnitude;

            Vector3 horizGroundVel = new Vector3(vel.x, 0f, vel.z);
            float horizGroundSpeed = horizGroundVel.magnitude;

            if (totalAirSpeed < 0.35f) return;

            Vector3 airVelDir = airVel / totalAirSpeed;
            Vector3 horizDir = (horizAirSpeed > 0.2f) ? (horizAirVel / horizAirSpeed) : disc.transform.forward;
            horizDir.y = 0f;
            if (horizDir.sqrMagnitude < 0.001f) horizDir = Vector3.forward;
            horizDir.Normalize();

            Vector3 discNormal = disc.transform.up;
            bool isInverted = discNormal.y < -0.1f;
            Vector3 effectiveNormal = isInverted ? -discNormal : discNormal;

            // Angle of Attack (AoA) relative to the oncoming wind + flight path
            float sinAlpha = -Vector3.Dot(airVelDir, effectiveNormal);
            float aoaDeg = Mathf.Asin(Mathf.Clamp(sinAlpha, -1f, 1f)) * Mathf.Rad2Deg;
            float clampedAoA = Mathf.Clamp(aoaDeg, -12f, 24f);

            float gravityMag = Mathf.Abs(Physics.gravity.y);
            float baseGlideRatio = isInverted ? 0.56f : 0.78f;
            float aoaLiftBonus = clampedAoA * 0.018f;

            // Headwind increases horizAirSpeed -> more lift! Tailwind decreases horizAirSpeed -> less lift!
            float speedLiftFactor = Mathf.Clamp01(horizAirSpeed / 8.5f);
            float headwindPopupBonus = Mathf.Clamp((horizAirSpeed - horizGroundSpeed) * 0.035f, -0.12f, 0.18f);

            float verticalLiftAccel = gravityMag * Mathf.Clamp(
                (baseGlideRatio + aoaLiftBonus + headwindPopupBonus) * speedLiftFactor,
                0.10f,
                1.14f);

            if (vel.y > 0.5f)
            {
                verticalLiftAccel *= Mathf.Clamp01(1.0f - (vel.y / 10.0f));
            }
            Vector3 liftForce = Vector3.up * verticalLiftAccel;

            // Aerodynamic drag acts against relative air velocity (naturally carries the disc with tailwinds & crosswinds)
            float linearDrag = 0.24f + Mathf.Max(0f, clampedAoA) * 0.012f;
            Vector3 dragForce = -airVel * linearDrag;

            // Crosswind exposure against the tilted belly/underside of the disc
            float crosswindBellyPush = Vector3.Dot(windVel, effectiveNormal);
            Vector3 windBellyForce = effectiveNormal * (crosswindBellyPush * 0.32f);

            Vector3 rightHoriz = Vector3.Cross(Vector3.up, horizDir);
            float bankLateralComponent = Vector3.Dot(effectiveNormal, rightHoriz);
            Vector3 bankCurveForce = rightHoriz * (bankLateralComponent * horizAirSpeed * 0.48f);

            if (horizAirSpeed > 0.5f && horizAirSpeed < 8.5f)
            {
                float spinSign = UltimateRulesManager.ActiveFlightSpinSign;
                Vector3 fadeDir = -rightHoriz * spinSign;
                float fadeStrength = Mathf.Clamp01((8.5f - horizAirSpeed) / 8.5f) * 1.45f;
                bankCurveForce += fadeDir * fadeStrength;
            }

            rb.AddForce(liftForce + dragForce + windBellyForce + bankCurveForce, ForceMode.Acceleration);

            if (horizGroundSpeed > 0.4f)
            {
                Vector3 lookHeading = (horizGroundVel + horizAirVel * 0.35f).normalized;
                Quaternion horizonLook = Quaternion.LookRotation(lookHeading, Vector3.up);
                Vector3 localUp = Quaternion.Inverse(horizonLook) * disc.transform.up;

                float currentRollDeg = Mathf.Atan2(-localUp.x, localUp.y) * Mathf.Rad2Deg;
                float currentPitchDeg = Mathf.Atan2(localUp.z, localUp.y) * Mathf.Rad2Deg;

                float targetPitchDeg = Mathf.Lerp(currentPitchDeg, -2.5f, Time.fixedDeltaTime * 1.8f);
                float fadeRollRate = (horizAirSpeed < 7.5f) ? (-UltimateRulesManager.ActiveFlightSpinSign * 4.5f * Time.fixedDeltaTime) : 0f;
                float targetRollDeg = Mathf.Lerp(currentRollDeg + fadeRollRate, 0f, Time.fixedDeltaTime * 0.15f);

                Quaternion targetFlightRot = horizonLook * Quaternion.Euler(targetPitchDeg, 0f, targetRollDeg);
                rb.MoveRotation(Quaternion.Slerp(disc.transform.rotation, targetFlightRot, Time.fixedDeltaTime * 8.0f));
            }
        }
    }
    // =========================================================================
    // GAME MODE HOOKS (Pull Lineups at +/- 32m Goal Lines)
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
            // Usage: "/wind 15 90" (sets 15 KPH wind at 90 degrees) or "/wind auto" (restores natural random weather)
            if (command == "/wind" && args.Length >= 1)
            {
                if (args[0].Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    UltimateWindManager.IsWindLockedByCommand = false;
                    UltimateRulesManager.BroadcastAnnouncement("Wind set to automatic natural weather.");
                    return false;
                }
                if (float.TryParse(args[0], out float kph))
                {
                    float angle = (args.Length >= 2 && float.TryParse(args[1], out float a)) ? a : 0f;
                    UltimateWindManager.ServerSetManualWind(kph, angle);
                    UltimateRulesManager.BroadcastAnnouncement($"Wind locked to {kph:F1} KPH at {angle:F0}°");
                    return false;
                }
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
    // INTERCEPT STANDARD SPAWNS, ROLES & POSITION NAMES (1-6, ALL SKATERS)
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

            // FIX: Only redirect teleports to the Goal Lines during the FaceOff lineup phase!
            // Never hijack mid-play sideline/endzone pivot adjustments!
            if (NetworkBehaviourSingleton<GameManager>.Instance != null &&
                NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.FaceOff)
            {
                return;
            }

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
    // SAFELY BLOCK MID-PLAY RESPAWN / POSITION SELECT
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
}