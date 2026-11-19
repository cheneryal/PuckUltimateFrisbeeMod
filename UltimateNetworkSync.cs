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
    // NETWORK MESSAGING BRIDGE (Multi-Disc Client Sync for 0-Jitter Lock)
    // =========================================================================
    public static class UltimateNetworkSync
    {
        public const string StateMessageName = "ULTIMATE/State";
        private static CustomMessagingManager _registeredCmm = null;

        public struct DiscSyncEntry
        {
            public ulong HolderClientId;
            public sbyte ThrowStyle;
            public float FlightWobble;
        }

        // Maps Puck.NetworkObjectId -> Holder & Spin state (supports unlimited Warmup discs!)
        public static readonly Dictionary<ulong, DiscSyncEntry> ClientDiscStates = new Dictionary<ulong, DiscSyncEntry>();

        // Tracks the most recently touched/held/targeted disc per player (ClientId -> Disc NetworkObjectId)
        public static readonly Dictionary<ulong, ulong> ClientPreferredDiscId = new Dictionary<ulong, ulong>();
        public static readonly Dictionary<ulong, Puck> ServerPlayerPreferredDisc = new Dictionary<ulong, Puck>();

        // Legacy fallback properties for single-disc checks
        public static ulong ClientSyncedHolderId = ulong.MaxValue;
        public static sbyte ClientSyncedThrowStyle = 0;
        public static float ClientSyncedFlightWobble = 0f;

        public static void RecordPlayerDiscInteraction(Player player, Puck disc)
        {
            if (player == null || disc == null) return;
            ulong clientId = player.OwnerClientId;
            ServerPlayerPreferredDisc[clientId] = disc;
            if (disc.NetworkObject != null)
            {
                ClientPreferredDiscId[clientId] = disc.NetworkObjectId;
            }
        }

        public static Puck GetPreferredDiscForClient(ulong clientId)
        {
            if (MonoBehaviourSingleton<PuckManager>.Instance == null) return null;
            var pucks = MonoBehaviourSingleton<PuckManager>.Instance.GetPucks(false);
            if (pucks == null || pucks.Count == 0) return null;

            // In an actual match (Single Disc), always return the match disc
            if (!UltimateRulesManager.IsWarmup || pucks.Count == 1)
            {
                return pucks[0];
            }

            // 1. If this player is currently holding a disc, always track that disc
            foreach (var p in pucks)
            {
                if (p == null || p.NetworkObject == null) continue;
                if (ClientDiscStates.TryGetValue(p.NetworkObjectId, out var entry) && entry.HolderClientId == clientId)
                {
                    ClientPreferredDiscId[clientId] = p.NetworkObjectId;
                    return p;
                }
            }

            // 2. Server-side direct reference check
            if (NetworkRoles.IsServer && ServerPlayerPreferredDisc.TryGetValue(clientId, out Puck serverPref) && serverPref != null && serverPref.IsSpawned)
            {
                return serverPref;
            }

            // 3. Client-side synced preferred disc ID (from last catch, pickup, throw, or tip)
            if (ClientPreferredDiscId.TryGetValue(clientId, out ulong prefDiscNetId))
            {
                foreach (var p in pucks)
                {
                    if (p != null && p.NetworkObject != null && p.NetworkObjectId == prefDiscNetId)
                    {
                        return p;
                    }
                }
            }

            // 4. Fallback: Find the closest disc to this player
            Player player = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerByClientId(clientId);
            if (player != null && player.PlayerBody != null)
            {
                Vector3 playerPos = player.PlayerBody.transform.position;
                Puck closest = null;
                float minDist = float.MaxValue;
                foreach (var p in pucks)
                {
                    if (p == null) continue;
                    float d = Vector3.Distance(playerPos, p.transform.position);
                    if (d < minDist)
                    {
                        minDist = d;
                        closest = p;
                    }
                }
                if (closest != null) return closest;
            }

            return pucks[0];
        }

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
            ClientPreferredDiscId.Clear();
            ServerPlayerPreferredDisc.Clear();
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

        public static void ServerBroadcastHolderState(
            Player holder,
            sbyte throwStyle,
            Puck disc = null,
            Player focusPlayerOverride = null,
            float flightWobble = 0f)
        {
            if (disc == null && holder == null)
            {
                ServerBroadcastClearAllHolders();
                return;
            }

            ulong discNetId = (disc != null && disc.NetworkObject != null) ? disc.NetworkObjectId : 0UL;
            ulong holderId = holder != null ? holder.OwnerClientId : ulong.MaxValue;
            float clampedWobble = Mathf.Clamp01(flightWobble);
            byte wobbleByte = (byte)Mathf.RoundToInt(clampedWobble * 255f);

            Player focusPlayer = focusPlayerOverride ?? holder;
            if (focusPlayer != null && disc != null)
            {
                RecordPlayerDiscInteraction(focusPlayer, disc);
            }
            ulong focusClientId = focusPlayer != null ? focusPlayer.OwnerClientId : ulong.MaxValue;

            ClientSyncedHolderId = holderId;
            ClientSyncedThrowStyle = throwStyle;
            ClientSyncedFlightWobble = clampedWobble;
            ClientDiscStates[discNetId] = new DiscSyncEntry
            {
                HolderClientId = holderId,
                ThrowStyle = throwStyle,
                FlightWobble = clampedWobble
            };

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
            if (nm.ConnectedClientsIds == null || nm.ConnectedClientsIds.Count == 0) return;

            var writer = new FastBufferWriter(36, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(discNetId);
                writer.WriteValueSafe(holderId);
                writer.WriteValueSafe(throwStyle);
                writer.WriteValueSafe(focusClientId);
                writer.WriteValueSafe(wobbleByte);
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

                ulong focusClientId = ulong.MaxValue;
                if (reader.Position < reader.Length)
                {
                    reader.ReadValueSafe(out focusClientId);
                }

                float wobble = 0f;
                if (reader.Position < reader.Length)
                {
                    reader.ReadValueSafe(out byte wobbleByte);
                    wobble = wobbleByte / 255f;
                }

                if (discNetId == ulong.MaxValue)
                {
                    ClientSyncedHolderId = ulong.MaxValue;
                    ClientSyncedThrowStyle = 0;
                    ClientSyncedFlightWobble = 0f;
                    ClientDiscStates.Clear();
                    return;
                }

                ClientSyncedHolderId = holderId;
                ClientSyncedThrowStyle = throwStyle;
                ClientSyncedFlightWobble = wobble;
                ClientDiscStates[discNetId] = new DiscSyncEntry
                {
                    HolderClientId = holderId,
                    ThrowStyle = throwStyle,
                    FlightWobble = wobble
                };

                if (holderId != ulong.MaxValue)
                {
                    ClientPreferredDiscId[holderId] = discNetId;
                }
                if (focusClientId != ulong.MaxValue)
                {
                    ClientPreferredDiscId[focusClientId] = discNetId;
                }
            }
            catch { }
        }
    }


    // =========================================================================
    // PER-PLAYER MOUSE1 DISC CAMERA TRACKING IN WARMUP
    // =========================================================================
    [HarmonyPatch]
    public static class PuckManager_GetPlayerPuck_Patch
    {
        private static MethodBase _targetMethod;

        [HarmonyPrepare]
        public static bool Prepare()
        {
            // Find any method on PuckManager that returns a single Puck and takes a ulong or Player
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(PuckManager)))
            {
                if (m.ReturnType == typeof(Puck) && m.Name.Contains("PlayerPuck"))
                {
                    _targetMethod = m;
                    return true;
                }
            }
            return false;
        }

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod() => _targetMethod;

        [HarmonyPrefix]
        public static bool Prefix(object[] __args, ref Puck __result)
        {
            if (__args != null && __args.Length > 0)
            {
                ulong clientId = ulong.MaxValue;
                if (__args[0] is ulong id)
                {
                    clientId = id;
                }
                else if (__args[0] is Player p)
                {
                    clientId = p.OwnerClientId;
                }

                if (clientId != ulong.MaxValue)
                {
                    Puck preferred = UltimateNetworkSync.GetPreferredDiscForClient(clientId);
                    if (preferred != null)
                    {
                        __result = preferred;
                        return false;
                    }
                }
            }
            return true;
        }
    }

    [HarmonyPatch]
    public static class PuckManager_GetPuck_Patch
    {
        private static MethodBase _targetMethod;

        [HarmonyPrepare]
        public static bool Prepare()
        {
            // Safely find GetPuck (e.g., GetPuck(bool isReplay = false)) without crashing if signature varies
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(PuckManager)))
            {
                if (m.ReturnType == typeof(Puck) && m.Name == "GetPuck")
                {
                    _targetMethod = m;
                    return true;
                }
            }
            return false;
        }

        [HarmonyTargetMethod]
        public static MethodBase TargetMethod() => _targetMethod;

        [HarmonyPrefix]
        public static bool Prefix(ref Puck __result)
        {
            if (!UltimateRulesManager.IsWarmup) return true;

            // Only override on Client for the LocalPlayer's camera/UI tracking;
            // let Server calls return normally unless it's a Listen Server Host in LateUpdate
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsClient && (!nm.IsServer || !Time.inFixedTimeStep))
            {
                Puck preferred = UltimateNetworkSync.GetPreferredDiscForClient(nm.LocalClientId);
                if (preferred != null)
                {
                    __result = preferred;
                    return false;
                }
            }
            return true;
        }
    }
}