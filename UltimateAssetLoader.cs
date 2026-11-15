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
    // CUSTOM 3D ASSETBUNDLE LOADER
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
}