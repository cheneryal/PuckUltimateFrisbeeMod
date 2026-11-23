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
                UltimateMatchReporter.Initialize();
                UltimateWindManager.Initialize();
                UltimateDiveMod.EnsureNetworkRegistered();
                UltimateKeybindManager.Initialize(PluginDirectory);
                UltimateAppearanceManager.Initialize(PluginDirectory);
                UltimateChatReminders.Initialize();

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
                UltimateMatchReporter.Dispose();
                UltimateWindManager.Dispose();
                UltimateDiveMod.Dispose();
                UltimateKeybindManager.Dispose();
                UltimateAppearanceManager.Dispose();
                UltimateAudioManager.UnregisterNetwork();
                UltimateSocialGestureManager.Dispose();
                UltimateChatReminders.Dispose();
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
        public static float WristRollMultiplier = 3.25f;

        // Hard-coded from your in-game F8 fine-tuning:
        public static Vector3 HeldDiscHandOffset = new Vector3(-0.068f, 0.125f, 0.061f);
        public static Vector3 HeldDiscPitchOffset = new Vector3(-13.4f, 0f, 2.6f);
    }
}