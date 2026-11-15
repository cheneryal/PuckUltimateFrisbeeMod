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
    // FULL-BODY SCULPTED ULTIMATE FRISBEE PLAYER (NO HOCKEY HELMET!)
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
        private Player _player;
        private PlayerHead _vanillaHead;
        private PlayerAppearanceData _lastAppliedData;
        private bool _hasAppliedOnce = false;

        public void Initialize(PlayerHead vanillaHead)
        {
            _vanillaHead = vanillaHead;
            _player = GetComponentInParent<PlayerBody>()?.Player;
            ApplyCustomization(force: true);
        }

        private void LateUpdate()
        {
            ApplyCustomization(force: false);
        }

        private static readonly HashSet<Mesh> _fixedMustacheMeshes = new HashSet<Mesh>();

        private static readonly HashSet<Mesh> _checkedBeardMeshes = new HashSet<Mesh>();

        private static void EnsureOutwardNormalsAndDoubleSided(Transform partTf)
        {
            if (partTf == null) return;

            // 1. Disable Backface Culling (_Cull = 0) on the material so it renders from all angles
            var rend = partTf.GetComponent<Renderer>();
            if (rend != null)
            {
                foreach (var mat in rend.materials)
                {
                    if (mat == null) continue;
                    if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f); // 0 = Render Both Sides
                    if (mat.HasProperty("_CullMode")) mat.SetFloat("_CullMode", 0f);
                }
            }

            // 2. If the mesh is inside-out (from axis='X' left-handed winding in Blender), flip its triangles & normals outward!
            var smr = partTf.GetComponent<SkinnedMeshRenderer>();
            Mesh m = smr != null ? smr.sharedMesh : partTf.GetComponent<MeshFilter>()?.sharedMesh;
            if (m == null || !m.isReadable || _checkedBeardMeshes.Contains(m)) return;
            _checkedBeardMeshes.Add(m);

            Vector3[] verts = m.vertices;
            Vector3[] norms = m.normals;
            if (verts == null || norms == null || verts.Length != norms.Length || verts.Length == 0) return;

            Vector3 center = m.bounds.center;
            float dotSum = 0f;
            for (int i = 0; i < verts.Length; i++)
            {
                dotSum += Vector3.Dot(norms[i], verts[i] - center);
            }

            // If average normal points toward the center of the mesh (< 0), it is inside-out!
            if (dotSum < 0f)
            {
                for (int s = 0; s < m.subMeshCount; s++)
                {
                    int[] tris = m.GetTriangles(s);
                    for (int i = 0; i < tris.Length; i += 3)
                    {
                        int tmp = tris[i + 1];
                        tris[i + 1] = tris[i + 2];
                        tris[i + 2] = tmp;
                    }
                    m.SetTriangles(tris, s);
                }
                m.RecalculateNormals();
                m.RecalculateTangents();
            }
        }

        public void ApplyCustomization(bool force)
        {
            if (_player == null) _player = GetComponentInParent<PlayerBody>()?.Player;
            ulong clientId = _player != null ? _player.OwnerClientId : ulong.MaxValue;
            PlayerAppearanceData data = UltimateAppearanceManager.GetAppearance(clientId);

            bool dataChanged = !_hasAppliedOnce || !data.Equals(_lastAppliedData);
            if (!force && !dataChanged) return;

            _hasAppliedOnce = true;
            _lastAppliedData = data;

            // 1. Apply Modular Meshes (Body Build, Hair, Beard, Headgear) + Colors to 3rd-Person Body
            Color resolvedSkinCol = ApplyToTarget(transform, data, _vanillaHead);

            // 2. Re-apply Team Colors in case a newly enabled Torso/Sleeve/Headgear mesh needs team tinting
            var marker = GetComponent<UltimatePlayerMarker>();
            if (marker != null && _player != null)
            {
                PlayerTeam team = UltimateRulesManager.GetTeam(_player);
                if (team == PlayerTeam.Blue || team == PlayerTeam.Red)
                {
                    marker.LastAppliedTeam = null;
                    UltimatePlayerRigManager.ApplyTeamColors(marker, team);
                }
            }

            // 3. Also sync Skin Color to the 1st-Person Floating Arm (Stick)
            if (_player != null && _player.Stick != null)
            {
                ApplySkinAndHairColorsToHierarchy(_player.Stick.transform, resolvedSkinCol, Color.black);
            }
        }

        /// <summary>
        /// Applies modular meshes (5 Body Builds, 8 Hairstyles, 6 Beards, 6 Headgears) and Skin/Hair colors
        /// to ANY character instance (both live players and the 3D UI Preview Mannequin).
        /// </summary>
        public static Color ApplyToTarget(Transform targetRoot, PlayerAppearanceData data, PlayerHead fallbackVanillaHead = null)
        {
            Color skinCol = UltimateAppearanceManager.SkinTonePalette[0];
            Color hairCol = UltimateAppearanceManager.HairColorPalette[0];
            if (targetRoot == null) return skinCol;

            if (fallbackVanillaHead != null && (data.SkinToneIndex == 0 || data.HairColorIndex == 0))
            {
                bool foundSkin = false, foundHair = false;
                foreach (var r in fallbackVanillaHead.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.sharedMaterial == null) continue;
                    string n = (r.gameObject.name + "_" + r.sharedMaterial.name).ToLowerInvariant();
                    Material m = r.sharedMaterial;
                    Color c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor")
                            : m.HasProperty("_Color") ? m.GetColor("_Color") : m.color;

                    if (!foundHair && (n.Contains("hair") || n.Contains("beard") || n.Contains("mustache") || n.Contains("brow")))
                    {
                        if (data.HairColorIndex == 0) hairCol = c;
                        foundHair = true;
                    }
                    else if (!foundSkin && (n.Contains("head") || n.Contains("skin") || n.Contains("face")))
                    {
                        if (data.SkinToneIndex == 0) skinCol = c;
                        foundSkin = true;
                    }
                }
            }

            if (data.SkinToneIndex > 0 && data.SkinToneIndex < UltimateAppearanceManager.SkinTonePalette.Length)
                skinCol = UltimateAppearanceManager.SkinTonePalette[data.SkinToneIndex];

            if (data.HairColorIndex > 0 && data.HairColorIndex < UltimateAppearanceManager.HairColorPalette.Length)
                hairCol = UltimateAppearanceManager.HairColorPalette[data.HairColorIndex];

            int buildIdx = Mathf.Clamp(data.BodyBuild, 0, UltimateAppearanceManager.BuildNames.Length - 1);
            string buildSuffix = $"_B{buildIdx}";
            bool isFemFallback = (buildIdx == (int)UltimateBodyBuild.AthleticFeminine);

            UltimateHairStyle hair   = (UltimateHairStyle)data.HairStyle;
            UltimateFacialHair beard = (UltimateFacialHair)data.FacialHair;
            UltimateHeadGear gear    = (UltimateHeadGear)data.HeadGear;

            // Closed-top hats use Hair_HatFringe instead of the upper crown so nothing clips through the hat dome!
            bool isClosedTopHat = (gear == UltimateHeadGear.BackwardCap ||
                                   gear == UltimateHeadGear.ForwardCap ||
                                   gear == UltimateHeadGear.Beanie ||
                                   gear == UltimateHeadGear.BucketHat);

            bool hasB0Builds    = targetRoot.Find("Torso_B0") != null;
            bool hasMFBuilds    = !hasB0Builds && (targetRoot.Find("Torso_M") != null || targetRoot.Find("Torso_F") != null);
            bool hasHatFringe   = targetRoot.Find("Hair_HatFringe") != null;
            bool hasModularHair = targetRoot.Find("Hair_1_Short") != null;
            bool hasModularGear = targetRoot.Find("Gear_1_BackCap_Band_Dome") != null;

            foreach (Transform child in targetRoot)
            {
                string n = child.name;

                // 1. 5 Body Builds (_B0 .. _B4) or Legacy (_M / _F)
                if (hasB0Builds && (n.Contains("_B0") || n.Contains("_B1") || n.Contains("_B2") || n.Contains("_B3") || n.Contains("_B4")))
                {
                    child.gameObject.SetActive(n.EndsWith(buildSuffix, StringComparison.Ordinal));
                    continue;
                }
                else if (hasMFBuilds)
                {
                    if (n.EndsWith("_M", StringComparison.Ordinal)) { child.gameObject.SetActive(!isFemFallback); continue; }
                    if (n.EndsWith("_F", StringComparison.Ordinal)) { child.gameObject.SetActive(isFemFallback); continue; }
                }

                // 2. Hat Fringe vs Upper Hair Crowns (Eliminates Cap/Beanie/BucketHat Clipping!)
                if (n == "Hair_HatFringe")
                {
                    child.gameObject.SetActive(isClosedTopHat && hair != UltimateHairStyle.None);
                    continue;
                }

                if (n.StartsWith("Hair_", StringComparison.Ordinal))
                {
                    bool show = false;
                    if (hair != UltimateHairStyle.None)
                    {
                        string prefix = $"Hair_{(int)hair}_";
                        if (n.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            if (!isClosedTopHat || !hasHatFringe)
                            {
                                show = true;
                            }
                            else
                            {
                                // When wearing a closed-top hat, hide the upper crown/bun/mohawk,
                                // but KEEP lower hanging parts (PonytailTail, LongDrape, Braid_L/R)!
                                if (n.Contains("PonytailTail") || n.Contains("LongDrape") || n.Contains("Braid_"))
                                {
                                    show = true;
                                }
                            }
                        }
                    }
                    child.gameObject.SetActive(show);
                    continue;
                }
                else if (n == "Custom_Hair" && !hasModularHair)
                {
                    child.gameObject.SetActive(hair != UltimateHairStyle.None);
                    continue;
                }

                // 3. Facial Hair (Beard_1 .. Beard_5)
                if (n.StartsWith("Beard_", StringComparison.Ordinal))
                {
                    string prefix = $"Beard_{(int)beard}_";
                    bool active = beard != UltimateFacialHair.None && n.StartsWith(prefix, StringComparison.Ordinal);
                    child.gameObject.SetActive(active);

                    if (active)
                    {
                        EnsureOutwardNormalsAndDoubleSided(child);
                    }
                    continue;
                }

                // 4. Headgear (Gear_1 .. Gear_6)
                if (n.StartsWith("Gear_", StringComparison.Ordinal))
                {
                    string prefix = $"Gear_{(int)gear}_";
                    child.gameObject.SetActive(gear != UltimateHeadGear.None && n.StartsWith(prefix, StringComparison.Ordinal));
                    continue;
                }
                else if ((n == "Cap_Band_Dome" || n == "Cap_Band_Brim") && !hasModularGear)
                {
                    child.gameObject.SetActive(gear != UltimateHeadGear.None);
                    continue;
                }
            }

            ApplySkinAndHairColorsToHierarchy(targetRoot, skinCol, hairCol);
            return skinCol;
        }

        public static void ApplySkinAndHairColorsToHierarchy(Transform root, Color skinCol, Color hairCol)
        {
            if (root == null) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                string objName = r.gameObject.name;
                bool isSkinObj = objName.Contains("Custom_Head") || objName.Contains("Arm") ||
                                 objName.Contains("Glove") || objName.Contains("Leg");
                bool isHairObj = objName.Contains("Hair") || objName.Contains("Beard");

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
        private float _smoothedStrafe;     // -1 (Left) to +1 (Right) for 2D Blend Trees
        private float _smoothedForward;    // Signed forward speed for 2D Blend Trees
        private float _strafeYawOffset;    // Angles hips into the strafe if using 1D Blend Tree
        private bool _wasHoldingDisc;
        private UltimatePlayerMarker _marker;

        // Footstep Animation Sync
        private Transform _leftFoot;
        private Transform _rightFoot;
        private float _prevLeftY = 1f;
        private float _prevRightY = 1f;
        private bool _leftPlanted = true;
        private bool _rightPlanted = true;
        private float _lastStepTime = -999f;
        private int _lastNormalizedHalfCycle = -1;

        public void Initialize(Player player, PlayerBody body, Movement movement)
        {
            _animator = GetComponent<Animator>();
            if (_animator != null)
            {
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                if (_animator.isHuman)
                {
                    _leftFoot = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                    _rightFoot = _animator.GetBoneTransform(HumanBodyBones.RightFoot);
                }
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

            if (_marker != null)
            {
                PlayerTeam currentTeam = UltimateRulesManager.GetTeam(_player);
                if ((currentTeam == PlayerTeam.Blue || currentTeam == PlayerTeam.Red) &&
                    _marker.LastAppliedTeam != currentTeam)
                {
                    UltimatePlayerRigManager.ApplyTeamColors(_marker, currentTeam);
                }
            }

            // 1. Calculate World & Local Velocity Vectors (works for both Local and Remote players!)
            Vector3 worldVel = Vector3.zero;
            if (Time.deltaTime > 0.0001f)
            {
                Vector3 posDelta = _body.transform.position - _lastPos;
                posDelta.y = 0f;
                worldVel = posDelta / Time.deltaTime;
                _lastPos = _body.transform.position;
            }

            if (_body.Rigidbody != null)
            {
                Vector3 rbVel = _body.Rigidbody.linearVelocity;
                rbVel.y = 0f;
                if (rbVel.sqrMagnitude > worldVel.sqrMagnitude)
                {
                    worldVel = rbVel;
                }
            }

            if (worldVel.magnitude > 28f) worldVel = Vector3.zero; // Ignore teleport spikes

            // Convert world velocity into the player's local space (X = Strafe Left/Right, Z = Forward/Back)
            Vector3 localVel = _body.transform.InverseTransformDirection(worldVel);
            float rawSpeed = new Vector2(localVel.x, localVel.z).magnitude;

            bool isHoldingDisc = UltimateNetworkSync.IsPlayerHoldingAnyDisc(_player.OwnerClientId);
            bool isSprinting = _body.IsSprinting != null && _body.IsSprinting.Value && !isHoldingDisc && rawSpeed > 1.5f;

            // 2. Determine Strafe Direction (-1f = Left/Q, +1f = Right/E)
            float targetStrafe = 0f;
            if (!isHoldingDisc)
            {
                // Local player: also check immediate Q/E or Server dictionary for instant responsiveness
                if (_player.IsLocalPlayer && UnityEngine.InputSystem.Keyboard.current != null)
                {
                    var kb = UnityEngine.InputSystem.Keyboard.current;
                    if (kb.qKey.isPressed) targetStrafe -= 1f;
                    if (kb.eKey.isPressed) targetStrafe += 1f;
                }
                else if (UltimateDiveMod.ClientStrafeState.TryGetValue(_player.OwnerClientId, out sbyte syncedStrafe) && syncedStrafe != 0)
                {
                    targetStrafe = syncedStrafe;
                }

                // Fallback / Remote client detection via local X velocity
                if (Mathf.Abs(targetStrafe) < 0.01f && Mathf.Abs(localVel.x) > 0.45f)
                {
                    float maxStrafeSpd = isSprinting ? UltimateDiveMod.StrafeSprintSpeed : UltimateDiveMod.StrafeRunSpeed;
                    targetStrafe = Mathf.Clamp(localVel.x / Mathf.Max(1f, maxStrafeSpd), -1f, 1f);
                }
            }

            // 3. Map overall speed to your Blend Tree thresholds (0 = Idle, 2.2 = Jogging, 6.5 = Sprint)
            float targetBlendSpeed = 0f;
            if (isHoldingDisc || rawSpeed < 0.25f)
            {
                targetBlendSpeed = 0f;
                targetStrafe = 0f;
            }
            else if (isSprinting)
            {
                float sprintT = Mathf.Clamp01((rawSpeed - 4.0f) / 3.5f);
                targetBlendSpeed = Mathf.Lerp(4.2f, 6.5f, sprintT);
            }
            else
            {
                float jogT = Mathf.Clamp01((rawSpeed - 0.25f) / 4.5f);
                targetBlendSpeed = Mathf.Lerp(0.8f, 3.2f, jogT);
            }

            _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, targetBlendSpeed, Time.deltaTime * 14f);
            _smoothedStrafe = Mathf.Lerp(_smoothedStrafe, targetStrafe, Time.deltaTime * 12f);

            float forwardSign = localVel.z < -0.5f ? -1f : 1f;
            _smoothedForward = Mathf.Lerp(_smoothedForward, targetBlendSpeed * forwardSign, Time.deltaTime * 14f);

            if (_wasHoldingDisc && !isHoldingDisc)
            {
                _animator.CrossFadeInFixedTime("Locomotion", 0.12f, 0);
            }
            else if (!isHoldingDisc && _smoothedSpeed > 0.8f)
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

            bool isCrouchingIdle = _body.IsSliding != null && _body.IsSliding.Value && _smoothedSpeed < 1.2f;
            bool isFallenOrDiving = (_body.HasFallen != null && _body.HasFallen.Value) || UltimateDiveMod.IsPlayerDiving(_player);
            bool isStrafing = Mathf.Abs(_smoothedStrafe) > 0.15f && _smoothedSpeed > 0.5f;

            // 4. Send Parameters to Animator
            _animator.SetFloat("fSpeed", _smoothedSpeed);
            _animator.SetFloat("fForward", _smoothedForward); // Optional: Signed forward speed (-6.5 to +6.5)
            _animator.SetFloat("fStrafe", _smoothedStrafe);   // -1.0 (Left) to +1.0 (Right)
            _animator.SetBool("bIsStrafing", isStrafing);
            _animator.SetFloat("fTurn", currentTurn);
            _animator.SetBool("bIsSprinting", isSprinting);
            _animator.SetBool("bIsSliding", isCrouchingIdle);
            _animator.SetBool("bIsJumping", _body.IsJumping);
            _animator.SetBool("bHasFallen", isFallenOrDiving);
            _animator.SetBool("bIsGrounded", _body.IsGrounded);
            _animator.SetBool("bIsHoldingDisc", isHoldingDisc);

            // 5. Body Orientation (with subtle hip twist when strafing)
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
                // Slightly angle the lower body into the strafe direction (up to 25 degrees) for natural footwork
                float targetYaw = isStrafing ? (_smoothedStrafe * 25f) : 0f;
                _strafeYawOffset = Mathf.Lerp(_strafeYawOffset, targetYaw, Time.deltaTime * 12f);
                Quaternion targetLocalRot = Quaternion.Euler(0f, _strafeYawOffset, 0f);
                transform.localRotation = Quaternion.Slerp(transform.localRotation, targetLocalRot, Time.deltaTime * 18f);
            }

            UpdateAnimationSyncedFootsteps(isHoldingDisc, isFallenOrDiving);
        }

        private void UpdateAnimationSyncedFootsteps(bool isHoldingDisc, bool isFallenOrDiving)
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;

            bool isGrounded = _body.IsGrounded && _body.transform.position.y < 0.65f;
            if (!isGrounded || isHoldingDisc || isFallenOrDiving || _smoothedSpeed < 1.0f)
            {
                _leftPlanted = true;
                _rightPlanted = true;
                return;
            }

            // Normalized speed factor between Jogging (2.2) and Sprint (6.5)
            float speedFactor = Mathf.Clamp01((_smoothedSpeed - 1.0f) / 5.5f);
            float stepVol = Mathf.Lerp(0.06f, 0.18f, speedFactor);
            float stepPitch = Mathf.Lerp(0.95f, 1.08f, speedFactor);

            // Minimum time between consecutive footsteps to prevent double-triggers
            float minStepInterval = Mathf.Lerp(0.28f, 0.16f, speedFactor);

            // Method 1: Exact 3D Foot Bone Plant Detection
            if (_leftFoot != null && _rightFoot != null)
            {
                float leftLocalY = _body.transform.InverseTransformPoint(_leftFoot.position).y;
                float rightLocalY = _body.transform.InverseTransformPoint(_rightFoot.position).y;

                // Unplant foot when it lifts above 0.16m, trigger step when it descends below 0.11m
                if (leftLocalY > 0.16f) _leftPlanted = false;
                if (rightLocalY > 0.16f) _rightPlanted = false;

                if (!_leftPlanted && leftLocalY <= 0.11f && leftLocalY <= _prevLeftY)
                {
                    _leftPlanted = true;
                    if (Time.time - _lastStepTime >= minStepInterval)
                    {
                        _lastStepTime = Time.time;
                        UltimateAudioManager.PlayLocal3DSound(UltimateSoundType.Footstep, _leftFoot.position, stepVol, stepPitch);
                    }
                }

                if (!_rightPlanted && rightLocalY <= 0.11f && rightLocalY <= _prevRightY)
                {
                    _rightPlanted = true;
                    if (Time.time - _lastStepTime >= minStepInterval)
                    {
                        _lastStepTime = Time.time;
                        UltimateAudioManager.PlayLocal3DSound(UltimateSoundType.Footstep, _rightFoot.position, stepVol, stepPitch);
                    }
                }

                _prevLeftY = leftLocalY;
                _prevRightY = rightLocalY;
                return;
            }

            // Method 2: Fallback to Blend Tree Normalized Time (2 steps per full 1.0 animation loop)
            AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(0);
            if (state.IsName("Locomotion") || state.IsName("Blend Tree"))
            {
                // Offset by 0.15 so footsteps align with the visual foot plant frames of each half-cycle
                int currentHalfCycle = Mathf.FloorToInt((state.normalizedTime + 0.15f) * 2f);
                if (currentHalfCycle != _lastNormalizedHalfCycle)
                {
                    _lastNormalizedHalfCycle = currentHalfCycle;
                    if (Time.time - _lastStepTime >= minStepInterval)
                    {
                        _lastStepTime = Time.time;
                        UltimateAudioManager.PlayLocal3DSound(UltimateSoundType.Footstep, _body.transform.position, stepVol, stepPitch);
                    }
                }
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