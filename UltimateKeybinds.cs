using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace PuckUltimateFrisbeeMod
{
    public enum UltimateBindAction
    {
        // --- Mod-Native Mechanics ---
        Throw = 0,
        CancelThrow = 1,
        Dive = 2,
        StrafeLeft = 3,
        StrafeRight = 4,
        ToggleHud = 5,
        ToggleMenu = 6,

        // --- Base-Game Overrides (Mod-Only; empty string = Game Default) ---
        DiscTrack = 7,
        Jump = 8,
        Crouch = 9,
        Sprint = 10
    }

    [Serializable]
    public class UltimateKeybindConfig
    {
        // Mod-Native Defaults
        public string Throw       = "<Mouse>/leftButton";
        public string CancelThrow = "<Mouse>/rightButton";
        public string Dive        = "<Keyboard>/f";
        public string StrafeLeft  = "<Keyboard>/q";
        public string StrafeRight = "<Keyboard>/e";
        public string ToggleHud   = "<Keyboard>/f1";
        public string ToggleMenu  = "<Keyboard>/f4";

        // Base-Game Mod-Only Overrides ("" = Use Puck Base Game Bind)
        public string DiscTrack   = "";
        public string Jump        = "";
        public string Crouch      = "";
        public string Sprint      = "";

        public static UltimateKeybindConfig CreateDefaults()
        {
            return new UltimateKeybindConfig();
        }

        public void EnsureModDefaultsPopulated()
        {
            var def = CreateDefaults();
            if (string.IsNullOrWhiteSpace(Throw))       Throw       = def.Throw;
            if (string.IsNullOrWhiteSpace(CancelThrow)) CancelThrow = def.CancelThrow;
            if (string.IsNullOrWhiteSpace(Dive))        Dive        = def.Dive;
            if (string.IsNullOrWhiteSpace(StrafeLeft))  StrafeLeft  = def.StrafeLeft;
            if (string.IsNullOrWhiteSpace(StrafeRight)) StrafeRight = def.StrafeRight;
            if (string.IsNullOrWhiteSpace(ToggleHud))   ToggleHud   = def.ToggleHud;
            if (string.IsNullOrWhiteSpace(ToggleMenu))  ToggleMenu  = def.ToggleMenu;

            if (DiscTrack == null) DiscTrack = "";
            if (Jump      == null) Jump      = "";
            if (Crouch    == null) Crouch    = "";
            if (Sprint    == null) Sprint    = "";
        }
    }

    public static class UltimateKeybindManager
    {
        private struct BindingSnapshot
        {
            public int BindingIndex;
            public string OriginalOverridePath;
            public bool IsPrimaryKbm;
            public bool IsModifierPart;
        }

        private const string SilencedBindingPath = "<Unbound>/none";

        public static UltimateKeybindConfig Config = UltimateKeybindConfig.CreateDefaults();

        private static string _configPath;
        private static bool _initialized;

        // Cached zero-allocation ButtonControls for direct polling
        private static readonly ButtonControl[] _cachedControls = new ButtonControl[11];
        private static Keyboard _cachedKeyboardDevice;
        private static Mouse _cachedMouseDevice;

        // Discovered base-game InputActions per overridable action
        private static readonly Dictionary<UltimateBindAction, List<InputAction>> _baseGameActions =
            new Dictionary<UltimateBindAction, List<InputAction>>
            {
                { UltimateBindAction.DiscTrack, new List<InputAction>() },
                { UltimateBindAction.Jump,      new List<InputAction>() },
                { UltimateBindAction.Crouch,    new List<InputAction>() },
                { UltimateBindAction.Sprint,    new List<InputAction>() }
            };

        // Original base-game binding snapshots (keyed by live InputAction instance)
        private static readonly Dictionary<InputAction, List<BindingSnapshot>> _originalSnapshots =
            new Dictionary<InputAction, List<BindingSnapshot>>();

        private static Type _inputManagerType;
        private static bool _lookedUpInputManagerType;

        private static float _lastRescanTime = -999f;
        private static float _lastDriftCheckTime = -999f;
        private static bool _forceRescan = true;
        private static bool _overridesSuspended = false;
        private static bool _loggedFullInventory = false;

        public static bool IsBaseGameOverrideAction(UltimateBindAction action)
        {
            return action == UltimateBindAction.DiscTrack ||
                   action == UltimateBindAction.Jump ||
                   action == UltimateBindAction.Crouch ||
                   action == UltimateBindAction.Sprint;
        }

        public static void Initialize(string pluginDirectory)
        {
            if (_initialized) return;
            _initialized = true;

            _configPath = Path.Combine(pluginDirectory ?? "", "ultimate_keybinds.json");
            LoadConfig();

            Application.quitting -= OnApplicationQuitting;
            Application.quitting += OnApplicationQuitting;

            _forceRescan = true;
            _loggedFullInventory = false;
        }

        public static void Dispose()
        {
            Application.quitting -= OnApplicationQuitting;
            RestoreAllBaseGameOverrides();
            _originalSnapshots.Clear();
            foreach (var list in _baseGameActions.Values) list.Clear();
            InvalidateControlCache();
            _initialized = false;
        }

        private static void OnApplicationQuitting()
        {
            RestoreAllBaseGameOverrides();
        }

        public static void LoadConfig()
        {
            try
            {
                if (!string.IsNullOrEmpty(_configPath) && File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    var loaded = JsonUtility.FromJson<UltimateKeybindConfig>(json);
                    Config = loaded ?? UltimateKeybindConfig.CreateDefaults();
                    Config.EnsureModDefaultsPopulated();
                }
                else
                {
                    Config = UltimateKeybindConfig.CreateDefaults();
                }
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"[UltimateKeybinds] Failed to load config: {ex}");
                Config = UltimateKeybindConfig.CreateDefaults();
            }

            InvalidateControlCache();
            _forceRescan = true;
        }

        public static void SaveConfig()
        {
            try
            {
                if (!string.IsNullOrEmpty(_configPath))
                {
                    string json = JsonUtility.ToJson(Config, true);
                    File.WriteAllText(_configPath, json);
                }
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"[UltimateKeybinds] Failed to save config: {ex}");
            }
        }

        public static string GetBindingPath(UltimateBindAction action)
        {
            switch (action)
            {
                case UltimateBindAction.Throw:       return Config.Throw;
                case UltimateBindAction.CancelThrow: return Config.CancelThrow;
                case UltimateBindAction.Dive:        return Config.Dive;
                case UltimateBindAction.StrafeLeft:  return Config.StrafeLeft;
                case UltimateBindAction.StrafeRight: return Config.StrafeRight;
                case UltimateBindAction.ToggleHud:   return Config.ToggleHud;
                case UltimateBindAction.ToggleMenu:  return Config.ToggleMenu;
                case UltimateBindAction.DiscTrack:   return Config.DiscTrack;
                case UltimateBindAction.Jump:        return Config.Jump;
                case UltimateBindAction.Crouch:      return Config.Crouch;
                case UltimateBindAction.Sprint:      return Config.Sprint;
                default: return "";
            }
        }

        public static bool HasOverride(UltimateBindAction action)
        {
            return IsBaseGameOverrideAction(action) && !string.IsNullOrWhiteSpace(GetBindingPath(action));
        }

        public static void SetBinding(UltimateBindAction action, string bindingPath)
        {
            string clean = (bindingPath ?? "").Trim();

            if (!IsBaseGameOverrideAction(action) && string.IsNullOrEmpty(clean))
                return;

            // Safety: never allow LMB or RMB to lock out the menu toggle
            if (action == UltimateBindAction.ToggleMenu &&
                (string.Equals(clean, "<Mouse>/leftButton", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(clean, "<Mouse>/rightButton", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            switch (action)
            {
                case UltimateBindAction.Throw:       Config.Throw       = clean; break;
                case UltimateBindAction.CancelThrow: Config.CancelThrow = clean; break;
                case UltimateBindAction.Dive:        Config.Dive        = clean; break;
                case UltimateBindAction.StrafeLeft:  Config.StrafeLeft  = clean; break;
                case UltimateBindAction.StrafeRight: Config.StrafeRight = clean; break;
                case UltimateBindAction.ToggleHud:   Config.ToggleHud   = clean; break;
                case UltimateBindAction.ToggleMenu:  Config.ToggleMenu  = clean; break;
                case UltimateBindAction.DiscTrack:   Config.DiscTrack   = clean; break;
                case UltimateBindAction.Jump:        Config.Jump        = clean; break;
                case UltimateBindAction.Crouch:      Config.Crouch      = clean; break;
                case UltimateBindAction.Sprint:      Config.Sprint      = clean; break;
            }

            InvalidateControlCache();
            SaveConfig();

            if (IsBaseGameOverrideAction(action))
            {
                _forceRescan = true;
                _loggedFullInventory = false;
                // Apply immediately to any already-discovered actions too
                ApplyAllBaseGameOverrides(logChanges: true);
            }
        }

        public static void ResetAllToDefaults()
        {
            Config = UltimateKeybindConfig.CreateDefaults();
            InvalidateControlCache();
            SaveConfig();
            RestoreAllBaseGameOverrides();
        }

        public static void RequestRescan()
        {
            _forceRescan = true;
        }

        // =====================================================================
        // DIRECT POLLING (ZERO ALLOCATION PER FRAME)
        // =====================================================================
        private static void InvalidateControlCache()
        {
            for (int i = 0; i < _cachedControls.Length; i++)
                _cachedControls[i] = null;
            _cachedKeyboardDevice = null;
            _cachedMouseDevice = null;
        }

        private static ButtonControl GetControl(UltimateBindAction action)
        {
            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (kb != _cachedKeyboardDevice || mouse != _cachedMouseDevice)
            {
                InvalidateControlCache();
                _cachedKeyboardDevice = kb;
                _cachedMouseDevice = mouse;
            }

            int idx = (int)action;
            if (idx < 0 || idx >= _cachedControls.Length) return null;

            ButtonControl cached = _cachedControls[idx];
            if (cached != null) return cached;

            string path = GetBindingPath(action);
            if (string.IsNullOrWhiteSpace(path)) return null;

            ButtonControl resolved = ResolvePathToControl(path, kb, mouse);
            _cachedControls[idx] = resolved;
            return resolved;
        }

        private static ButtonControl ResolvePathToControl(string path, Keyboard kb, Mouse mouse)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (path.StartsWith("<Mouse>/", StringComparison.OrdinalIgnoreCase))
            {
                if (mouse == null) return null;
                string btn = path.Substring("<Mouse>/".Length);
                if (btn.Equals("leftButton", StringComparison.OrdinalIgnoreCase))    return mouse.leftButton;
                if (btn.Equals("rightButton", StringComparison.OrdinalIgnoreCase))   return mouse.rightButton;
                if (btn.Equals("middleButton", StringComparison.OrdinalIgnoreCase))  return mouse.middleButton;
                if (btn.Equals("backButton", StringComparison.OrdinalIgnoreCase))    return mouse.backButton;
                if (btn.Equals("forwardButton", StringComparison.OrdinalIgnoreCase)) return mouse.forwardButton;
                return null;
            }

            if (path.StartsWith("<Keyboard>/", StringComparison.OrdinalIgnoreCase))
            {
                if (kb == null) return null;
                string keyName = path.Substring("<Keyboard>/".Length);
                var allKeys = kb.allKeys;
                for (int i = 0; i < allKeys.Count; i++)
                {
                    KeyControl k = allKeys[i];
                    if (k != null && k.keyCode != Key.None &&
                        string.Equals(k.name, keyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return k;
                    }
                }
            }

            return null;
        }

        public static bool IsPressed(UltimateBindAction action)
        {
            ButtonControl ctrl = GetControl(action);
            return ctrl != null && ctrl.isPressed;
        }

        public static bool WasPressedThisFrame(UltimateBindAction action)
        {
            ButtonControl ctrl = GetControl(action);
            return ctrl != null && ctrl.wasPressedThisFrame;
        }

        public static bool TryCapturePressedBinding(bool allowMouseButtons, out string capturedPath)
        {
            capturedPath = null;

            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                var allKeys = kb.allKeys;
                for (int i = 0; i < allKeys.Count; i++)
                {
                    KeyControl k = allKeys[i];
                    if (k == null || k.keyCode == Key.None || k.keyCode == Key.Escape)
                        continue;

                    if (k.wasPressedThisFrame)
                    {
                        capturedPath = "<Keyboard>/" + k.name;
                        return true;
                    }
                }
            }

            Mouse mouse = Mouse.current;
            if (allowMouseButtons && mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame)    { capturedPath = "<Mouse>/leftButton";    return true; }
                if (mouse.rightButton.wasPressedThisFrame)   { capturedPath = "<Mouse>/rightButton";   return true; }
                if (mouse.middleButton.wasPressedThisFrame)  { capturedPath = "<Mouse>/middleButton";  return true; }
                if (mouse.backButton.wasPressedThisFrame)    { capturedPath = "<Mouse>/backButton";    return true; }
                if (mouse.forwardButton.wasPressedThisFrame) { capturedPath = "<Mouse>/forwardButton"; return true; }
            }

            return false;
        }

        // =====================================================================
        // HUMAN-FRIENDLY DISPLAY LABELS (FOR UI & HUD)
        // =====================================================================
        public static string GetDisplayLabel(UltimateBindAction action)
        {
            string path = GetBindingPath(action);
            if (string.IsNullOrWhiteSpace(path))
                return IsBaseGameOverrideAction(action) ? "GAME DEFAULT" : "UNBOUND";
            return FormatBindingPath(path);
        }

        public static string GetEffectiveDisplayLabel(UltimateBindAction action, string fallbackLabel)
        {
            string path = GetBindingPath(action);
            if (!string.IsNullOrWhiteSpace(path))
                return FormatBindingPath(path);

            if (IsBaseGameOverrideAction(action) &&
                _baseGameActions.TryGetValue(action, out var list) &&
                list.Count > 0)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    InputAction ia = list[i];
                    if (ia == null) continue;
                    for (int b = 0; b < ia.bindings.Count; b++)
                    {
                        var binding = ia.bindings[b];
                        if (IsKeyboardOrMouseBinding(binding))
                        {
                            string eff = binding.effectivePath;
                            if (!string.IsNullOrWhiteSpace(eff))
                                return FormatBindingPath(eff);
                        }
                    }
                }
            }

            return fallbackLabel;
        }

        public static string FormatBindingPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "GAME DEFAULT";

            string p = path.Trim();
            if (p.EndsWith("/leftButton", StringComparison.OrdinalIgnoreCase))    return "LMB";
            if (p.EndsWith("/rightButton", StringComparison.OrdinalIgnoreCase))   return "RMB";
            if (p.EndsWith("/middleButton", StringComparison.OrdinalIgnoreCase))  return "MMB";
            if (p.EndsWith("/backButton", StringComparison.OrdinalIgnoreCase))    return "MOUSE 4";
            if (p.EndsWith("/forwardButton", StringComparison.OrdinalIgnoreCase)) return "MOUSE 5";

            if (p.IndexOf("keyboard", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int slashIdx = p.LastIndexOf('/');
                string k = slashIdx >= 0 ? p.Substring(slashIdx + 1) : p;
                if (k.Equals("space", StringComparison.OrdinalIgnoreCase))       return "SPACE";
                if (k.Equals("leftShift", StringComparison.OrdinalIgnoreCase))   return "L-SHIFT";
                if (k.Equals("rightShift", StringComparison.OrdinalIgnoreCase))  return "R-SHIFT";
                if (k.Equals("leftCtrl", StringComparison.OrdinalIgnoreCase))    return "L-CTRL";
                if (k.Equals("rightCtrl", StringComparison.OrdinalIgnoreCase))   return "R-CTRL";
                if (k.Equals("leftAlt", StringComparison.OrdinalIgnoreCase))     return "L-ALT";
                if (k.Equals("rightAlt", StringComparison.OrdinalIgnoreCase))    return "R-ALT";
                if (k.Equals("escape", StringComparison.OrdinalIgnoreCase))      return "ESC";
                if (k.Equals("capsLock", StringComparison.OrdinalIgnoreCase))    return "CAPS";
                if (k.Equals("backspace", StringComparison.OrdinalIgnoreCase))   return "BKSP";
                if (k.Equals("enter", StringComparison.OrdinalIgnoreCase))       return "ENTER";
                if (k.Equals("tab", StringComparison.OrdinalIgnoreCase))         return "TAB";
                if (k.Equals("backquote", StringComparison.OrdinalIgnoreCase))   return "`";

                ButtonControl ctrl = ResolvePathToControl("<Keyboard>/" + k, Keyboard.current, null);
                if (ctrl != null && !string.IsNullOrWhiteSpace(ctrl.displayName) && ctrl.displayName.Length <= 10)
                    return ctrl.displayName.ToUpperInvariant();

                return k.ToUpperInvariant();
            }

            int slash = p.LastIndexOf('/');
            return (slash >= 0 && slash < p.Length - 1)
                ? p.Substring(slash + 1).ToUpperInvariant()
                : p.ToUpperInvariant();
        }

        // =====================================================================
        // IN-MEMORY BASE-GAME INPUTACTION OVERRIDES (SAFE SNAPSHOT & RESTORE)
        // =====================================================================
        public static void TickBaseGameOverrideSync(Player localPlayer)
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer || _overridesSuspended) return;

            // IMPORTANT: Do NOT consume _forceRescan while the F4 menu is open, because
            // Puck disables gameplay InputActionMaps whenever UI mouse-required mode is active!
            if (UltimateCustomizationUI.PanelOpen) return;

            bool anyOverrideConfigured =
                HasOverride(UltimateBindAction.DiscTrack) ||
                HasOverride(UltimateBindAction.Jump) ||
                HasOverride(UltimateBindAction.Crouch) ||
                HasOverride(UltimateBindAction.Sprint);

            bool missingDiscovered =
                (HasOverride(UltimateBindAction.DiscTrack) && _baseGameActions[UltimateBindAction.DiscTrack].Count == 0) ||
                (HasOverride(UltimateBindAction.Jump)      && _baseGameActions[UltimateBindAction.Jump].Count == 0) ||
                (HasOverride(UltimateBindAction.Crouch)    && _baseGameActions[UltimateBindAction.Crouch].Count == 0) ||
                (HasOverride(UltimateBindAction.Sprint)    && _baseGameActions[UltimateBindAction.Sprint].Count == 0);

            bool dueForScan = _forceRescan || (missingDiscovered && (Time.unscaledTime - _lastRescanTime >= 1.5f));
            if (dueForScan)
            {
                _forceRescan = false;
                _lastRescanTime = Time.unscaledTime;

                DiscoverBaseGameActions(localPlayer);

                if (anyOverrideConfigured || _originalSnapshots.Count > 0)
                {
                    ApplyAllBaseGameOverrides(logChanges: true);
                }
                return;
            }

            // Periodic drift check: if Puck reloaded or reset an InputAction override, re-apply cleanly
            if (anyOverrideConfigured && (Time.unscaledTime - _lastDriftCheckTime >= 1.0f))
            {
                _lastDriftCheckTime = Time.unscaledTime;
                if (HasAnyOverrideDrifted())
                {
                    ApplyAllBaseGameOverrides(logChanges: false);
                }
            }
        }

        private static bool HasAnyOverrideDrifted()
        {
            UltimateBindAction[] overridables =
            {
                UltimateBindAction.DiscTrack,
                UltimateBindAction.Jump,
                UltimateBindAction.Crouch,
                UltimateBindAction.Sprint
            };

            for (int a = 0; a < overridables.Length; a++)
            {
                UltimateBindAction bindAction = overridables[a];
                string desired = GetBindingPath(bindAction);
                if (string.IsNullOrWhiteSpace(desired)) continue;

                var list = _baseGameActions[bindAction];
                for (int i = 0; i < list.Count; i++)
                {
                    InputAction ia = list[i];
                    if (ia == null || !_originalSnapshots.TryGetValue(ia, out var snaps)) continue;
                    for (int s = 0; s < snaps.Count; s++)
                    {
                        var snap = snaps[s];
                        if (!snap.IsPrimaryKbm || snap.BindingIndex >= ia.bindings.Count) continue;
                        string currentEff = ia.bindings[snap.BindingIndex].effectivePath ?? "";
                        if (!string.Equals(currentEff, desired, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static object GetInputManagerInstance()
        {
            if (!_lookedUpInputManagerType)
            {
                _lookedUpInputManagerType = true;
                _inputManagerType = AccessTools.TypeByName("InputManager");
            }
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

        private static void DiscoverBaseGameActions(Player localPlayer)
        {
            foreach (var list in _baseGameActions.Values) list.Clear();

            var visitedObjects = new HashSet<object>();
            var allSeenActions = new HashSet<InputAction>();

            // 1. Live enabled InputActions across the entire Unity InputSystem
            try
            {
                var enabledActions = InputSystem.ListEnabledActions();
                for (int i = 0; i < enabledActions.Count; i++)
                {
                    RegisterDiscoveredAction(enabledActions[i], null, allSeenActions);
                }
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"[UltimateKeybinds] ListEnabledActions failed: {ex}");
            }

            // 2. All loaded InputActionAssets (catches maps even while temporarily disabled)
            try
            {
                var assets = Resources.FindObjectsOfTypeAll<InputActionAsset>();
                for (int i = 0; i < assets.Length; i++)
                {
                    CollectActionsRecursive(assets[i], null, 0, visitedObjects, allSeenActions);
                }
            }
            catch { }

            // 3. UnityEngine.InputSystem.PlayerInput components (if Puck uses Unity's PlayerInput)
            try
            {
                var unityPIs = UnityEngine.Object.FindObjectsByType<UnityEngine.InputSystem.PlayerInput>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < unityPIs.Length; i++)
                {
                    if (unityPIs[i] != null && unityPIs[i].actions != null)
                    {
                        CollectActionsRecursive(unityPIs[i].actions, null, 0, visitedObjects, allSeenActions);
                    }
                }
            }
            catch { }

            // 4. Puck's InputManager singleton & any MonoBehaviour with "Input" in its type name
            object inputMgr = GetInputManagerInstance();
            if (inputMgr != null)
            {
                CollectActionsRecursive(inputMgr, null, 0, visitedObjects, allSeenActions);
            }

            // 5. Local Player's Puck PlayerInput / PlayerCamera / PlayerController
            if (localPlayer == null && MonoBehaviourSingleton<PlayerManager>.Instance != null)
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                if (nm != null && nm.IsClient)
                {
                    localPlayer = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(nm.LocalClientId);
                }
            }

            if (localPlayer != null)
            {
                CollectActionsRecursive(localPlayer.PlayerInput, null, 0, visitedObjects, allSeenActions);
                CollectActionsRecursive(localPlayer.PlayerCamera, null, 0, visitedObjects, allSeenActions);
                CollectActionsRecursive(localPlayer.GetComponent(typeof(PlayerController)), null, 0, visitedObjects, allSeenActions);
            }

            if (!_loggedFullInventory)
            {
                _loggedFullInventory = true;
                LogDiscoveryDiagnostics(allSeenActions, inputMgr);
            }
        }

        private static void CollectActionsRecursive(
            object obj,
            string hintName,
            int depth,
            HashSet<object> visited,
            HashSet<InputAction> allSeen)
        {
            if (obj == null || depth > 3) return;
            if (!obj.GetType().IsValueType && !visited.Add(obj)) return;

            if (obj is InputAction direct)
            {
                RegisterDiscoveredAction(direct, hintName, allSeen);
                return;
            }

            if (obj is InputActionReference aref)
            {
                if (aref.action != null) RegisterDiscoveredAction(aref.action, hintName, allSeen);
                return;
            }

            if (obj is InputActionMap map)
            {
                var actions = map.actions;
                for (int i = 0; i < actions.Count; i++) RegisterDiscoveredAction(actions[i], null, allSeen);
                return;
            }

            if (obj is InputActionAsset asset)
            {
                var maps = asset.actionMaps;
                for (int m = 0; m < maps.Count; m++)
                {
                    var actions = maps[m].actions;
                    for (int i = 0; i < actions.Count; i++) RegisterDiscoveredAction(actions[i], null, allSeen);
                }
                return;
            }

            if (obj is IInputActionCollection2 collection)
            {
                foreach (var a in collection) RegisterDiscoveredAction(a, null, allSeen);
                return;
            }

            if (obj is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    CollectActionsRecursive(entry.Value, entry.Key != null ? entry.Key.ToString() : hintName, depth + 1, visited, allSeen);
                }
                return;
            }

            if (obj is IEnumerable enumerable && !(obj is string))
            {
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (++count > 64) break;
                    CollectActionsRecursive(item, hintName, depth + 1, visited, allSeen);
                }
                return;
            }

            Type t = obj.GetType();
            if (t.IsPrimitive || t == typeof(string) || t.Namespace != null &&
                (t.Namespace.StartsWith("System") || t.Namespace.StartsWith("UnityEngine.UI")))
                return;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (FieldInfo f in t.GetFields(flags))
            {
                if (!ShouldDescendIntoType(f.FieldType)) continue;
                try { CollectActionsRecursive(f.GetValue(obj), f.Name, depth + 1, visited, allSeen); }
                catch { }
            }

            foreach (PropertyInfo p in t.GetProperties(flags))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (!ShouldDescendIntoType(p.PropertyType)) continue;
                try { CollectActionsRecursive(p.GetValue(obj), p.Name, depth + 1, visited, allSeen); }
                catch { }
            }
        }

        private static bool ShouldDescendIntoType(Type t)
        {
            if (t == null || t.IsPrimitive || t.IsEnum || t == typeof(string)) return false;
            if (t == typeof(InputAction) ||
                t == typeof(InputActionReference) ||
                t == typeof(InputActionMap) ||
                t == typeof(InputActionAsset) ||
                typeof(IInputActionCollection2).IsAssignableFrom(t))
                return true;

            string tn = t.Name;
            if (tn.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tn.IndexOf("Action", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tn.IndexOf("Control", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tn.IndexOf("Bind", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == typeof(InputAction) || args[i] == typeof(InputActionReference))
                        return true;
                }
            }

            if (t.IsArray)
            {
                Type et = t.GetElementType();
                if (et == typeof(InputAction) || et == typeof(InputActionReference)) return true;
            }

            return false;
        }

        private static bool IsExcludedActionMap(InputAction action)
        {
            string mapName = action?.actionMap?.name;
            if (string.IsNullOrEmpty(mapName)) return false;
            string lower = mapName.ToLowerInvariant();
            return lower == "ui" ||
                   lower.StartsWith("ui_") || lower.EndsWith("_ui") ||
                   lower.Contains("menu") || lower.Contains("chat") || lower.Contains("debug");
        }

        private static void RegisterDiscoveredAction(InputAction action, string hintName, HashSet<InputAction> allSeen)
        {
            if (action == null) return;
            allSeen.Add(action);

            if (IsExcludedActionMap(action)) return;

            string n = (action.name ?? "").ToLowerInvariant();
            string h = (hintName ?? "").ToLowerInvariant();

            if (MatchesJump(n) || MatchesJump(h))
            {
                AddUnique(_baseGameActions[UltimateBindAction.Jump], action);
            }
            else if (MatchesCrouch(n) || MatchesCrouch(h))
            {
                AddUnique(_baseGameActions[UltimateBindAction.Crouch], action);
            }
            else if (MatchesSprint(n) || MatchesSprint(h))
            {
                AddUnique(_baseGameActions[UltimateBindAction.Sprint], action);
            }
            else if (MatchesDiscTrack(n) || MatchesDiscTrack(h))
            {
                AddUnique(_baseGameActions[UltimateBindAction.DiscTrack], action);
            }
        }

        private static bool MatchesJump(string lower)
        {
            if (string.IsNullOrEmpty(lower)) return false;
            return lower.Contains("jump") && !lower.Contains("sky");
        }

        private static bool MatchesCrouch(string lower)
        {
            if (string.IsNullOrEmpty(lower) || lower.Contains("slider")) return false;
            return lower.Contains("slide") || lower.Contains("crouch") || lower.Contains("duck");
        }

        private static bool MatchesSprint(string lower)
        {
            if (string.IsNullOrEmpty(lower)) return false;
            if (lower.Contains("camera") || lower.Contains("stamina")) return false;
            return lower == "sprint" || lower.Contains("sprint");
        }

        private static bool MatchesDiscTrack(string lower)
        {
            if (string.IsNullOrEmpty(lower)) return false;
            if (lower.Contains("chat") || lower.Contains("audio") || lower.Contains("sound"))
                return false;
            return lower.Contains("track") || lower.Contains("lookatpuck") ||
                   lower.Contains("puckcam") || lower.Contains("puckfocus") || lower.Contains("focus");
        }

        private static void AddUnique(List<InputAction> list, InputAction action)
        {
            if (!list.Contains(action)) list.Add(action);
        }

        private static bool IsKeyboardOrMouseBinding(InputBinding binding)
        {
            if (binding.isComposite) return false;

            // Puck wraps single actions in ButtonWithOneOrNoneModifier composites:
            // index 0 = composite header, index 1 = "modifier", index 2 = "button".
            if (binding.isPartOfComposite &&
                !string.Equals(binding.name, "button", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string p = binding.path ?? "";
            string ep = binding.effectivePath ?? "";
            string g = binding.groups ?? "";

            return p.IndexOf("keyboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ep.IndexOf("keyboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ep.IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   g.IndexOf("keyboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   g.IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<BindingSnapshot> EnsureActionSnapshotted(InputAction action)
        {
            if (_originalSnapshots.TryGetValue(action, out var existing))
                return existing;

            var snapshots = new List<BindingSnapshot>();
            bool primaryAssigned = false;

            for (int i = 0; i < action.bindings.Count; i++)
            {
                InputBinding b = action.bindings[i];
                if (b.isComposite) continue;

                // Snapshot KBM composite "modifier" parts so we can clear any base-game modifier
                // while a single-button mod override is active, and restore it cleanly later.
                if (b.isPartOfComposite &&
                    string.Equals(b.name, "modifier", StringComparison.OrdinalIgnoreCase))
                {
                    string g = b.groups ?? "";
                    if (g.IndexOf("keyboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        g.IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        string.IsNullOrEmpty(g))
                    {
                        snapshots.Add(new BindingSnapshot
                        {
                            BindingIndex = i,
                            OriginalOverridePath = b.overridePath,
                            IsPrimaryKbm = false,
                            IsModifierPart = true
                        });
                    }
                    continue;
                }

                if (!IsKeyboardOrMouseBinding(b)) continue;

                snapshots.Add(new BindingSnapshot
                {
                    BindingIndex = i,
                    OriginalOverridePath = b.overridePath,
                    IsPrimaryKbm = !primaryAssigned,
                    IsModifierPart = false
                });
                primaryAssigned = true;
            }

            if (!primaryAssigned)
            {
                for (int i = 0; i < action.bindings.Count; i++)
                {
                    InputBinding b = action.bindings[i];
                    if (b.isComposite) continue;
                    if (b.isPartOfComposite &&
                        !string.Equals(b.name, "button", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string p = b.path ?? "";
                    if (p.IndexOf("gamepad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        p.IndexOf("joystick", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    snapshots.Add(new BindingSnapshot
                    {
                        BindingIndex = i,
                        OriginalOverridePath = b.overridePath,
                        IsPrimaryKbm = true,
                        IsModifierPart = false
                    });
                    break;
                }
            }

            _originalSnapshots[action] = snapshots;
            return snapshots;
        }

        private static void ApplyAllBaseGameOverrides(bool logChanges)
        {
            if (_overridesSuspended) return;

            ApplyOverrideForAction(UltimateBindAction.DiscTrack, logChanges);
            ApplyOverrideForAction(UltimateBindAction.Jump, logChanges);
            ApplyOverrideForAction(UltimateBindAction.Crouch, logChanges);
            ApplyOverrideForAction(UltimateBindAction.Sprint, logChanges);
        }

        private static void SafeApplyOverride(InputAction action, int bindingIndex, string overridePath)
        {
            try
            {
                action.ApplyBindingOverride(bindingIndex, overridePath);
            }
            catch (InvalidOperationException)
            {
                bool wasEnabled = action.enabled;
                if (wasEnabled) action.Disable();
                try { action.ApplyBindingOverride(bindingIndex, overridePath); }
                finally { if (wasEnabled) action.Enable(); }
            }
        }

        private static void SafeRemoveOverride(InputAction action, int bindingIndex)
        {
            try
            {
                action.RemoveBindingOverride(bindingIndex);
            }
            catch (InvalidOperationException)
            {
                bool wasEnabled = action.enabled;
                if (wasEnabled) action.Disable();
                try { action.RemoveBindingOverride(bindingIndex); }
                finally { if (wasEnabled) action.Enable(); }
            }
        }

        private static void ApplyOverrideForAction(UltimateBindAction bindAction, bool logChanges)
        {
            if (!_baseGameActions.TryGetValue(bindAction, out var actions)) return;
            string overridePath = GetBindingPath(bindAction);
            bool hasOverride = !string.IsNullOrWhiteSpace(overridePath);

            if (hasOverride && actions.Count == 0 && logChanges)
            {
                UltimateFrisbeePlugin.ModLogger.Info(
                    $"[UltimateKeybinds] Override {bindAction} -> '{overridePath}' pending: 0 base-game InputActions discovered yet.");
                return;
            }

            for (int i = 0; i < actions.Count; i++)
            {
                InputAction action = actions[i];
                if (action == null) continue;

                if (!hasOverride)
                {
                    if (_originalSnapshots.ContainsKey(action))
                    {
                        RestoreSingleAction(action);
                        if (logChanges)
                        {
                            UltimateFrisbeePlugin.ModLogger.Info(
                                $"[UltimateKeybinds] Restored {bindAction} ('{action.actionMap?.name}/{action.name}') back to Game Default.");
                        }
                    }
                    continue;
                }

                var snapshots = EnsureActionSnapshotted(action);
                if (snapshots.Count == 0) continue;

                try
                {
                    int primaryIdx = -1;
                    for (int s = 0; s < snapshots.Count; s++)
                    {
                        var snap = snapshots[s];
                        if (snap.BindingIndex < 0 || snap.BindingIndex >= action.bindings.Count) continue;

                        if (snap.IsModifierPart)
                        {
                            // Clear any base-game modifier so the single-button mod override fires directly
                            SafeRemoveOverride(action, snap.BindingIndex);
                        }
                        else if (snap.IsPrimaryKbm)
                        {
                            primaryIdx = snap.BindingIndex;
                            SafeApplyOverride(action, snap.BindingIndex, overridePath);
                        }
                        else
                        {
                            SafeApplyOverride(action, snap.BindingIndex, SilencedBindingPath);
                        }
                    }

                    if (logChanges)
                    {
                        string controlsStr = string.Join(", ", action.controls.Select(c => c.path));
                        string effStr = (primaryIdx >= 0 && primaryIdx < action.bindings.Count)
                            ? action.bindings[primaryIdx].effectivePath
                            : "?";
                        UltimateFrisbeePlugin.ModLogger.Info(
                            $"[UltimateKeybinds] Applied {bindAction} -> '{overridePath}' on '{action.actionMap?.name}/{action.name}' (enabled={action.enabled}, binding #{primaryIdx} effective='{effStr}', controls=[{controlsStr}]).");
                    }
                }
                catch (Exception ex)
                {
                    UltimateFrisbeePlugin.ModLogger.Error(
                        $"[UltimateKeybinds] Failed to apply {bindAction} override on '{action.actionMap?.name}/{action.name}': {ex}");
                }
            }
        }

        private static void RestoreSingleAction(InputAction action)
        {
            if (action == null || !_originalSnapshots.TryGetValue(action, out var snapshots)) return;

            try
            {
                for (int s = 0; s < snapshots.Count; s++)
                {
                    var snap = snapshots[s];
                    if (snap.BindingIndex < 0 || snap.BindingIndex >= action.bindings.Count) continue;

                    if (string.IsNullOrEmpty(snap.OriginalOverridePath))
                    {
                        SafeRemoveOverride(action, snap.BindingIndex);
                    }
                    else
                    {
                        SafeApplyOverride(action, snap.BindingIndex, snap.OriginalOverridePath);
                    }
                }
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"[UltimateKeybinds] RestoreSingleAction error: {ex}");
            }
        }

        public static void RestoreAllBaseGameOverrides()
        {
            foreach (var action in _originalSnapshots.Keys.ToList())
            {
                RestoreSingleAction(action);
            }
        }

        internal static bool SuspendBaseGameOverridesTemporarily()
        {
            if (_overridesSuspended || _originalSnapshots.Count == 0) return false;
            _overridesSuspended = true;
            RestoreAllBaseGameOverrides();
            return true;
        }

        internal static void ResumeBaseGameOverrides()
        {
            if (!_overridesSuspended) return;
            _overridesSuspended = false;
            ApplyAllBaseGameOverrides(logChanges: false);
        }

        private static void LogDiscoveryDiagnostics(HashSet<InputAction> allSeen, object inputMgr)
        {
            try
            {
                string FormatList(UltimateBindAction key)
                {
                    var list = _baseGameActions[key];
                    if (list.Count == 0) return "NONE";
                    return string.Join(", ", list.Select(a =>
                    {
                        var bPaths = new List<string>();
                        for (int i = 0; i < a.bindings.Count; i++)
                            bPaths.Add($"{i}:{a.bindings[i].effectivePath}(grp={a.bindings[i].groups})");
                        return $"{a.actionMap?.name}/{a.name}[{string.Join("|", bPaths)}]";
                    }));
                }

                UltimateFrisbeePlugin.ModLogger.Info(
                    $"[UltimateKeybinds] Matched Actions -> Jump={FormatList(UltimateBindAction.Jump)} | Crouch={FormatList(UltimateBindAction.Crouch)} | Sprint={FormatList(UltimateBindAction.Sprint)} | DiscTrack={FormatList(UltimateBindAction.DiscTrack)}");

                string allNames = allSeen.Count == 0
                    ? "NONE"
                    : string.Join(", ", allSeen.Select(a => $"{a.actionMap?.name}/{a.name}(en={a.enabled})"));
                UltimateFrisbeePlugin.ModLogger.Info($"[UltimateKeybinds] All Seen InputActions ({allSeen.Count}): {allNames}");

                if (allSeen.Count == 0 && inputMgr != null)
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    string fields = string.Join(", ", inputMgr.GetType().GetFields(flags).Select(f => $"{f.FieldType.Name} {f.Name}"));
                    UltimateFrisbeePlugin.ModLogger.Info($"[UltimateKeybinds] InputManager fields: {fields}");
                }
            }
            catch { }
        }
    }

    // Guarantees Puck's own settings saver never captures our in-memory mod keybind overrides
    [HarmonyPatch]
    internal static class InputAction_ProtectBaseGameSave_Patch
    {
        private static readonly List<MethodBase> _targets = new List<MethodBase>();

        [HarmonyPrepare]
        public static bool Prepare()
        {
            _targets.Clear();
            Type extType = typeof(InputActionRebindingExtensions);
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(extType))
            {
                if (m != null && m.Name == "SaveBindingOverridesAsJson" && !m.ContainsGenericParameters)
                {
                    _targets.Add(m);
                }
            }
            return _targets.Count > 0;
        }

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods() => _targets;

        [HarmonyPrefix]
        public static void Prefix(out bool __state)
        {
            __state = UltimateKeybindManager.SuspendBaseGameOverridesTemporarily();
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            if (__state)
            {
                UltimateKeybindManager.ResumeBaseGameOverrides();
            }
        }
    }
}