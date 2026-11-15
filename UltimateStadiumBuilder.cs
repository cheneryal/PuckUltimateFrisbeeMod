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
    // REMOVE RINK BOARDS/GLASS & SPAWN GRASS STADIUM FIELD (Level)
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
                UltimateWindManager.EnsureNetworkRegistered();
                UltimateAppearanceManager.EnsureNetworkRegistered();
                UltimateWindManager.EnsureClientRuntime();

                if (__instance == null) return;

                // 1. Expand Level.Bounds so cameras/minimap/spectators know the field is 350x350m
                __instance.Bounds = new Bounds(Vector3.zero, new Vector3(350f, 50f, 350f));

                int iceLayer = LayerMask.NameToLayer("Ice");
                if (iceLayer < 0) iceLayer = 0;
                float iceSurfaceY = 0f;

                Transform existingGrassRoot = __instance.transform.Find("UltimateGrassFieldRoot");

                // 2. Sweep ALL objects in the scene and disable ALL vanilla arena/rink colliders & renderers:
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
                    if (obj.GetComponentInParent<StickPositioner>() != null || obj.GetComponentInParent<PlayerPosition>() != null) continue;

                    string nameLower = obj.name.ToLowerInvariant();

                    // A. Capture Ice layer, material, and surface height from vanilla Ice, then disable its small rink collider
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

                            col.enabled = false;
                        }
                        continue;
                    }

                    // B. Capture the physics layer & material from Rink Barriers before disabling them
                    if (nameLower.Contains("barrier") || nameLower.Contains("border") ||
                        nameLower.Contains("glass") || nameLower.Contains("board") || nameLower.Contains("wall"))
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
                    }

                    // C. Unconditionally disable ALL remaining vanilla scene colliders (including Goal triggers,
                    //    StickRaycast boards at Z=+/-30m, offside/icing triggers, etc.) so NOTHING blocks the field lines!
                    foreach (var col in obj.GetComponentsInChildren<Collider>(true))
                    {
                        if (existingGrassRoot != null && col.transform.IsChildOf(existingGrassRoot)) continue;
                        col.enabled = false;
                    }

                    if (!IsCriticalObject(obj))
                    {
                        DisableRenderersAndNonGroundColliders(obj, iceSurfaceY, existingGrassRoot);
                    }
                }

                int barrierLayer = ResolvePlayerBlockingLayer(iceLayer);

                // Disable all indoor hockey rink reverb zones & filters for open outdoor acoustics
                foreach (var rz in UnityEngine.Object.FindObjectsByType<AudioReverbZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    rz.enabled = false;
                }
                foreach (var rf in UnityEngine.Object.FindObjectsByType<AudioReverbFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    rf.enabled = false;
                }

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
            if (cam == null || cam.name == "PreviewCamera") return;

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

            foreach (MeshRenderer rend in customFieldRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (rend == null || !IsFenceTransform(rend.transform)) continue;

                Bounds worldBounds = rend.bounds;
                if (worldBounds.size.sqrMagnitude < 0.05f) continue;

                // Skip any combined parent mesh that spans across the field
                if (worldBounds.size.x > 35f && worldBounds.size.z > 35f)
                {
                    continue;
                }

                // Make sure the closest point of this fence mesh is at least 1.5m outside the playable field lines!
                // (Playable field is X: +/-18.5m, Z: +/-50.0m).
                Bounds playAreaWithMargin = new Bounds(
                    Vector3.zero,
                    new Vector3((UltimateRulesManager.FieldHalfWidth + 1.5f) * 2f, 50f, (UltimateRulesManager.FieldHalfLength + 1.5f) * 2f)
                );
                if (playAreaWithMargin.Intersects(worldBounds))
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
            UltimateWindManager.SpawnFieldWindsocks(fieldRoot.transform, visualGrassTopY);
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
    // HIDE VANILLA MINIMAP & MUTE STICK-TO-ICE TAP/DRAG AUDIO
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
}