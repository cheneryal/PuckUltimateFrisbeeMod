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
    // FULL-SHAFT 3D ARM & HAND CONTROLLER (Stick)
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
    // ZERO-JITTER HELD DISC CLIENT LOCK & SELF-HEALING DISC VISUALS (Puck)
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
}