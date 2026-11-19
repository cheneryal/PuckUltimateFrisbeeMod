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
    // REALISTIC OUTDOOR AUDIO ENGINE & MULTI-VARIATION POOLING
    // =========================================================================
    public enum UltimateSoundType : byte
    {
        Throw = 0,          // Generic fallback / dive whoosh
        Catch = 1,
        Block = 2,
        Pickup = 3,
        GroundHit = 4,
        Footstep = 5,
        Bobble = 6,
        ThrowSoft = 7,      // throw_1.wav: low velocity, low frequency whoosh
        ThrowSnap = 8,      // throw_2.wav: snappier, higher pitched whoosh (flick / crisp snap)
        ThrowPower = 9,     // throw_3.wav: powerful whoosh (huck / pull)
        ThrowFloat = 10,    // throw_4.wav: slow drawn-out low frequency whoosh (floaty lob)
        ThrowWobble = 11,   // throw_5.wav: fluttering effect of a wobbly disc thrown
        BlockTip = Block    // Backward-compatible alias
    }

    public static class UltimateAudioManager
    {
        public const string SoundMessageName = "ULTIMATE/Sfx";
        private static CustomMessagingManager _registeredSfxCmm = null;

        private static readonly Dictionary<UltimateSoundType, List<AudioClip>> _soundPools =
            new Dictionary<UltimateSoundType, List<AudioClip>>();

        public static void Initialize(string pluginFolder)
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;

            foreach (UltimateSoundType type in Enum.GetValues(typeof(UltimateSoundType)))
            {
                _soundPools[type] = new List<AudioClip>();
            }

            // 1. Build Physical Model Procedural Sound Variations
            BuildProceduralAudioPools();

            // 2. Load custom WAV files if present in the plugin directory
            if (!string.IsNullOrEmpty(pluginFolder))
            {
                // Map throw_1..throw_5 to their specific throw physics signatures
                LoadMappedThrowWavs(pluginFolder);

                LoadCustomWavVariants(pluginFolder, "catch", UltimateSoundType.Catch);
                LoadCustomWavVariants(pluginFolder, "block", UltimateSoundType.Block);
                LoadCustomWavVariants(pluginFolder, "bobble", UltimateSoundType.Bobble);
                LoadCustomWavVariants(pluginFolder, "pickup", UltimateSoundType.Pickup);
                LoadCustomWavVariants(pluginFolder, "ground", UltimateSoundType.GroundHit);
                LoadCustomWavVariants(pluginFolder, "step", UltimateSoundType.Footstep);
            }
        }

        /// <summary>
        /// Selects the exact throw_1..throw_5 sound matching the physical throw quality & velocity.
        /// </summary>
        public static UltimateSoundType SelectThrowSoundType(
            UltimateThrowType throwType,
            float normalizedPower,
            float wobble,
            float launchPitchDeg,
            float snapScore)
        {
            // throw_5: Wobbly / off-axis torque flutter takes priority so players immediately hear sloppy form
            if (wobble >= 0.42f)
            {
                return UltimateSoundType.ThrowWobble;
            }

            // throw_3: High-power clean huck, pull, or booming backhand/hammer
            if (normalizedPower >= 0.72f)
            {
                return UltimateSoundType.ThrowPower;
            }

            // throw_2: Snappy, higher-pitched flick or crisp medium whip
            if ((throwType == UltimateThrowType.Forehand && normalizedPower >= 0.28f) ||
                (snapScore >= 0.60f && normalizedPower >= 0.30f))
            {
                return UltimateSoundType.ThrowSnap;
            }

            // throw_4: Slow, drawn-out low-frequency whoosh for floaty lobs & medium glide passes
            if (launchPitchDeg >= 13f ||
                (normalizedPower >= 0.28f && normalizedPower < 0.72f && snapScore < 0.48f))
            {
                return UltimateSoundType.ThrowFloat;
            }

            // throw_1: Low-velocity, low-frequency whoosh for gentle dumps, dishes, and soft scoobers
            return UltimateSoundType.ThrowSoft;
        }

        private static void LoadMappedThrowWavs(string folder)
        {
            bool loaded1 = LoadSpecificWav(folder, new[] { "throw_1.wav", "throw1.wav" }, UltimateSoundType.ThrowSoft);
            bool loaded2 = LoadSpecificWav(folder, new[] { "throw_2.wav", "throw2.wav" }, UltimateSoundType.ThrowSnap);
            bool loaded3 = LoadSpecificWav(folder, new[] { "throw_3.wav", "throw3.wav" }, UltimateSoundType.ThrowPower);
            bool loaded4 = LoadSpecificWav(folder, new[] { "throw_4.wav", "throw4.wav" }, UltimateSoundType.ThrowFloat);
            bool loaded5 = LoadSpecificWav(folder, new[] { "throw_5.wav", "throw5.wav" }, UltimateSoundType.ThrowWobble);

            // Populate generic Throw pool (used by dive whoosh etc.) with throw.wav or throw_1/throw_2
            if (!LoadSpecificWav(folder, new[] { "throw.wav" }, UltimateSoundType.Throw))
            {
                if (loaded1 && _soundPools[UltimateSoundType.ThrowSoft].Count > 0)
                {
                    _soundPools[UltimateSoundType.Throw].Clear();
                    _soundPools[UltimateSoundType.Throw].Add(_soundPools[UltimateSoundType.ThrowSoft][0]);
                }
                else if (loaded2 && _soundPools[UltimateSoundType.ThrowSnap].Count > 0)
                {
                    _soundPools[UltimateSoundType.Throw].Clear();
                    _soundPools[UltimateSoundType.Throw].Add(_soundPools[UltimateSoundType.ThrowSnap][0]);
                }
            }
        }

        private static bool LoadSpecificWav(string folder, string[] candidates, UltimateSoundType targetType)
        {
            try
            {
                foreach (string fileName in candidates)
                {
                    string path = Path.Combine(folder, fileName);
                    if (!File.Exists(path)) continue;

                    AudioClip clip = LoadAndTrimWav(path);
                    if (clip != null)
                    {
                        _soundPools[targetType].Clear();
                        _soundPools[targetType].Add(clip);
                        UltimateFrisbeePlugin.ModLogger.Info($"Loaded mapped SFX [{targetType}]: {fileName}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"Failed loading mapped WAV for {targetType}: {ex.Message}");
            }
            return false;
        }

        private static void BuildProceduralAudioPools()
        {
            System.Random rng = new System.Random(42);

            for (int v = 0; v < 3; v++)
            {
                float varOffset = (float)(rng.NextDouble() * 0.12 - 0.06);

                // --- A. CATCH ---
                _soundPools[UltimateSoundType.Catch].Add(CreateProceduralClip($"Ult_Catch_v{v}", 0.08f, (t, dur) =>
                {
                    float slapEnv = Mathf.Exp(-t * 85f);
                    float slapFreq = Mathf.Lerp(420f, 85f, Mathf.Clamp01(t / 0.025f));
                    float slapImpact = Mathf.Sin(t * 2f * Mathf.PI * slapFreq) * slapEnv * 0.65f;

                    float ringEnv = Mathf.Exp(-t * 55f);
                    float f1 = 185f * (1f + varOffset);
                    float f2 = 390f * (1f + varOffset * 0.8f);

                    float shellResonance = (
                        Mathf.Sin(t * 2f * Mathf.PI * f1) * 0.40f +
                        Mathf.Sin(t * 2f * Mathf.PI * f2) * 0.22f
                    ) * ringEnv;

                    return Mathf.Clamp((slapImpact + shellResonance) * 1.1f, -1f, 1f);
                }));

                // --- B. THROW VARIANTS (Procedural fallbacks matching throw_1..throw_5) ---
                // Generic Throw
                _soundPools[UltimateSoundType.Throw].Add(CreateProceduralClip($"Ult_Throw_v{v}", 0.18f, (t, dur) =>
                {
                    float snapEnv = Mathf.Exp(-t * 220f);
                    float snapFreq = Mathf.Lerp(980f, 320f, Mathf.Clamp01(t / 0.015f));
                    float snap = Mathf.Sin(t * 2f * Mathf.PI * snapFreq) * snapEnv * 0.28f;

                    float center = 0.035f + varOffset * 0.04f;
                    float dt = (t - center) / 0.045f;
                    float whooshEnv = Mathf.Exp(-dt * dt);
                    float airTone = Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(280f, 140f, t / dur));

                    return Mathf.Clamp(snap + airTone * whooshEnv * 0.45f, -1f, 1f);
                }));

                // ThrowSoft (throw_1 fallback: low velocity, low freq whoosh)
                _soundPools[UltimateSoundType.ThrowSoft].Add(CreateProceduralClip($"Ult_ThrowSoft_v{v}", 0.16f, (t, dur) =>
                {
                    float dt = (t - 0.045f) / 0.055f;
                    float env = Mathf.Exp(-dt * dt);
                    return Mathf.Clamp(Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(175f, 95f, t / dur)) * env * 0.45f, -1f, 1f);
                }));

                // ThrowSnap (throw_2 fallback: snappy high pitched whip)
                _soundPools[UltimateSoundType.ThrowSnap].Add(CreateProceduralClip($"Ult_ThrowSnap_v{v}", 0.14f, (t, dur) =>
                {
                    float snap = Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(1250f, 420f, Mathf.Clamp01(t / 0.014f))) * Mathf.Exp(-t * 240f) * 0.42f;
                    float dt = (t - 0.028f) / 0.035f;
                    float whoosh = Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(420f, 210f, t / dur)) * Mathf.Exp(-dt * dt) * 0.48f;
                    return Mathf.Clamp(snap + whoosh, -1f, 1f);
                }));

                // ThrowPower (throw_3 fallback: deep powerful huck whoosh)
                _soundPools[UltimateSoundType.ThrowPower].Add(CreateProceduralClip($"Ult_ThrowPower_v{v}", 0.24f, (t, dur) =>
                {
                    float snap = Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(1050f, 260f, Mathf.Clamp01(t / 0.018f))) * Mathf.Exp(-t * 180f) * 0.40f;
                    float dt = (t - 0.055f) / 0.068f;
                    float whoosh = (Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(320f, 115f, t / dur)) * 0.65f +
                                    Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(160f, 70f, t / dur)) * 0.35f) * Mathf.Exp(-dt * dt);
                    return Mathf.Clamp(snap + whoosh * 0.72f, -1f, 1f);
                }));

                // ThrowFloat (throw_4 fallback: slow drawn-out low freq whoosh)
                _soundPools[UltimateSoundType.ThrowFloat].Add(CreateProceduralClip($"Ult_ThrowFloat_v{v}", 0.30f, (t, dur) =>
                {
                    float dt = (t - 0.11f) / 0.095f;
                    float env = Mathf.Exp(-dt * dt);
                    return Mathf.Clamp(Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(190f, 105f, t / dur)) * env * 0.50f, -1f, 1f);
                }));

                // ThrowWobble (throw_5 fallback: AM-modulated fluttering disc wobble)
                _soundPools[UltimateSoundType.ThrowWobble].Add(CreateProceduralClip($"Ult_ThrowWobble_v{v}", 0.28f, (t, dur) =>
                {
                    float env = Mathf.Exp(-t * 6.5f) * Mathf.Clamp01(t / 0.02f);
                    float flutter = 0.55f + 0.45f * Mathf.Sin(t * 2f * Mathf.PI * (24f + varOffset * 30f));
                    float tone = Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(260f, 130f, t / dur)) * 0.65f +
                                 Mathf.Sin(t * 2f * Mathf.PI * 410f) * 0.20f;
                    return Mathf.Clamp(tone * env * flutter * 0.75f, -1f, 1f);
                }));

                // --- C. HARD DEFENSIVE BLOCK ---
                _soundPools[UltimateSoundType.Block].Add(CreateProceduralClip($"Ult_Block_v{v}", 0.075f, (t, dur) =>
                {
                    float env = Mathf.Exp(-t * 90f);
                    float fRim = 680f * (1f + varOffset);
                    float fSecond = 1120f * (1f + varOffset);

                    float impact = Mathf.Sin(t * 2f * Mathf.PI * Mathf.Lerp(450f, 120f, t / dur)) * Mathf.Exp(-t * 110f) * 0.45f;
                    float tone = (Mathf.Sin(t * 2f * Mathf.PI * fRim) * 0.50f +
                                  Mathf.Sin(t * 2f * Mathf.PI * fSecond) * 0.25f) * env;

                    return Mathf.Clamp((impact + tone) * 1.15f, -1f, 1f);
                }));

                // --- D. BOBBLE / GLANCING TIP ---
                _soundPools[UltimateSoundType.Bobble].Add(CreateProceduralClip($"Ult_Bobble_v{v}", 0.06f, (t, dur) =>
                {
                    float env = Mathf.Exp(-t * 80f);
                    float brushFreq = Mathf.Lerp(520f, 260f, t / dur);
                    float brushTone = Mathf.Sin(t * 2f * Mathf.PI * brushFreq) * env * 0.40f;
                    float rimPlink = Mathf.Sin(t * 2f * Mathf.PI * (880f * (1f + varOffset))) * Mathf.Exp(-t * 140f) * 0.25f;

                    return Mathf.Clamp((brushTone + rimPlink) * 0.95f, -1f, 1f);
                }));

                // --- E. GROUND HIT ---
                _soundPools[UltimateSoundType.GroundHit].Add(CreateProceduralClip($"Ult_Ground_v{v}", 0.08f, (t, dur) =>
                {
                    float thudEnv = Mathf.Exp(-t * 60f);
                    float turfThud = Mathf.Sin(t * 2f * Mathf.PI * 65f) * thudEnv * 0.65f;
                    return Mathf.Clamp(turfThud, -1f, 1f);
                }));

                // --- F. TURF FOOTSTEP ---
                _soundPools[UltimateSoundType.Footstep].Add(CreateProceduralClip($"Ult_Step_v{v}", 0.055f, (t, dur) =>
                {
                    float env = Mathf.Exp(-t * 70f);
                    float heelThud = Mathf.Sin(t * 2f * Mathf.PI * 58f) * env * 0.35f;
                    return Mathf.Clamp(heelThud, -1f, 1f);
                }));

                // --- G. PICKUP ---
                _soundPools[UltimateSoundType.Pickup].Add(CreateProceduralClip($"Ult_Pickup_v{v}", 0.05f, (t, dur) =>
                {
                    float env = Mathf.Exp(-t * 65f);
                    float softSnap = Mathf.Sin(t * 2f * Mathf.PI * 210f) * env * 0.25f;
                    return Mathf.Clamp(softSnap, -1f, 1f);
                }));
            }
        }

        public static void EnsureNetworkRegistered()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null) return;
            if (_registeredSfxCmm == nm.CustomMessagingManager) return;

            nm.CustomMessagingManager.UnregisterNamedMessageHandler(SoundMessageName);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(SoundMessageName, OnSoundMessageReceived);
            _registeredSfxCmm = nm.CustomMessagingManager;
        }

        public static void UnregisterNetwork()
        {
            if (_registeredSfxCmm != null)
            {
                _registeredSfxCmm.UnregisterNamedMessageHandler(SoundMessageName);
            }
            _registeredSfxCmm = null;
        }

        public static void ServerBroadcastSound(UltimateSoundType type, Vector3 position, float volume = 1f, float pitch = 1f)
        {
            EnsureNetworkRegistered();
            PlayLocal3DSound(type, position, volume, pitch);

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
            if (nm.ConnectedClientsIds == null || nm.ConnectedClientsIds.Count == 0) return;

            var writer = new FastBufferWriter(24, Allocator.Temp);
            try
            {
                writer.WriteValueSafe((byte)type);
                writer.WriteValueSafe(position);
                writer.WriteValueSafe(volume);
                writer.WriteValueSafe(pitch);
                nm.CustomMessagingManager.SendNamedMessageToAll(SoundMessageName, writer, NetworkDelivery.Unreliable);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnSoundMessageReceived(ulong senderId, FastBufferReader reader)
        {
            if (NetworkRoles.IsServer) return;
            try
            {
                reader.ReadValueSafe(out byte rawType);
                reader.ReadValueSafe(out Vector3 pos);
                reader.ReadValueSafe(out float volume);
                reader.ReadValueSafe(out float pitch);
                PlayLocal3DSound((UltimateSoundType)rawType, pos, volume, pitch);
            }
            catch { }
        }

        public static void PlayLocal3DSound(UltimateSoundType type, Vector3 position, float volume = 1f, float pitch = 1f)
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;
            if (!_soundPools.TryGetValue(type, out var pool) || pool.Count == 0) return;

            AudioClip clip = pool[UnityEngine.Random.Range(0, pool.Count)];
            if (clip == null) return;

            GameObject sfxObj = new GameObject($"UltSfx_{type}");
            sfxObj.transform.position = position;

            AudioSource src = sfxObj.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 0.85f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 2.0f;
            src.maxDistance = 45.0f;
            src.bypassReverbZones = true;
            src.reverbZoneMix = 0f;

            float pitchJitter = UnityEngine.Random.Range(0.96f, 1.04f);
            float volJitter = UnityEngine.Random.Range(0.95f, 1.05f);

            src.volume = Mathf.Clamp01(volume * volJitter);
            src.pitch = Mathf.Clamp(pitch * pitchJitter, 0.6f, 1.8f);

            src.Play();
            UnityEngine.Object.Destroy(sfxObj, (clip.length / Mathf.Max(0.5f, src.pitch)) + 0.08f);
        }

        private static AudioClip CreateProceduralClip(string name, float durationSec, Func<float, float, float> generator)
        {
            int sampleRate = 44100;
            int sampleCount = Mathf.Max(1, Mathf.RoundToInt(durationSec * sampleRate));
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                samples[i] = generator(t, durationSec);
            }

            AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static void LoadCustomWavVariants(string folder, string baseName, UltimateSoundType type)
        {
            try
            {
                List<string> candidateFiles = new List<string>();

                string single = Path.Combine(folder, $"{baseName}.wav");
                if (File.Exists(single)) candidateFiles.Add(single);

                for (int i = 1; i <= 6; i++)
                {
                    string variantA = Path.Combine(folder, $"{baseName}_{i}.wav");
                    string variantB = Path.Combine(folder, $"{baseName}{i}.wav");
                    if (File.Exists(variantA)) candidateFiles.Add(variantA);
                    else if (File.Exists(variantB)) candidateFiles.Add(variantB);
                }

                if (candidateFiles.Count == 0) return;

                _soundPools[type].Clear();

                foreach (string path in candidateFiles)
                {
                    AudioClip custom = LoadAndTrimWav(path);
                    if (custom != null)
                    {
                        _soundPools[type].Add(custom);
                        UltimateFrisbeePlugin.ModLogger.Info($"Loaded trimmed SFX [{type}]: {Path.GetFileName(path)}");
                    }
                }
            }
            catch (Exception ex)
            {
                UltimateFrisbeePlugin.ModLogger.Error($"Failed loading custom WAVs for {baseName}: {ex.Message}");
            }
        }

        private static AudioClip LoadAndTrimWav(string filePath)
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 44) return null;

            int channels = BitConverter.ToInt16(bytes, 22);
            int sampleRate = BitConverter.ToInt32(bytes, 24);
            int bitsPerSample = BitConverter.ToInt16(bytes, 34);

            int pos = 12;
            while (pos < bytes.Length - 8)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                int chunkSize = BitConverter.ToInt32(bytes, pos + 4);

                if (chunkId == "data")
                {
                    pos += 8;
                    int bytesPerSample = bitsPerSample / 8;
                    int totalRawSamples = chunkSize / bytesPerSample;
                    int frameCount = totalRawSamples / Mathf.Max(1, channels);

                    float[] rawData = new float[totalRawSamples];

                    for (int i = 0; i < totalRawSamples && (pos + i * bytesPerSample + 1) < bytes.Length; i++)
                    {
                        if (bitsPerSample == 16)
                        {
                            short s = BitConverter.ToInt16(bytes, pos + i * 2);
                            rawData[i] = s / 32768f;
                        }
                        else if (bitsPerSample == 8)
                        {
                            rawData[i] = (bytes[pos + i] - 128) / 128f;
                        }
                    }

                    int firstAudibleFrame = 0;
                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        bool hitAudio = false;
                        for (int ch = 0; ch < channels; ch++)
                        {
                            if (Mathf.Abs(rawData[frame * channels + ch]) > 0.015f)
                            {
                                hitAudio = true;
                                break;
                            }
                        }
                        if (hitAudio)
                        {
                            firstAudibleFrame = Mathf.Max(0, frame - 44);
                            break;
                        }
                    }

                    int trimmedFrameCount = frameCount - firstAudibleFrame;
                    int trimmedSampleCount = trimmedFrameCount * channels;
                    float[] trimmedData = new float[trimmedSampleCount];
                    Array.Copy(rawData, firstAudibleFrame * channels, trimmedData, 0, trimmedSampleCount);

                    AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(filePath), trimmedFrameCount, channels, sampleRate, false);
                    clip.SetData(trimmedData, 0);
                    return clip;
                }
                pos += 8 + chunkSize;
            }

            return null;
        }
    }

    [HarmonyPatch]
    public static class StickAndPositioner_MuteAllStickAudio_Patch
    {
        private static readonly FieldInfo _iceHitAudioField = AccessTools.Field(typeof(StickPositioner), "iceHitAudioSource");
        private static readonly FieldInfo _iceDragAudioField = AccessTools.Field(typeof(StickPositioner), "iceDragAudioSource");
        private static readonly FieldInfo _windAudioField = AccessTools.Field(typeof(StickPositioner), "windAudioSource");
        private static readonly FieldInfo _syncAudioSourceField = AccessTools.Field(typeof(SynchronizedAudio), "audioSource");

        private static readonly HashSet<SynchronizedAudio> _mutedStickAudios = new HashSet<SynchronizedAudio>();

        public static void RegisterAndMuteExternalAudio(SynchronizedAudio syncAudio)
        {
            if (syncAudio == null) return;
            _mutedStickAudios.Add(syncAudio);

            AudioSource src = _syncAudioSourceField?.GetValue(syncAudio) as AudioSource ?? syncAudio.GetComponent<AudioSource>();
            if (src != null)
            {
                src.mute = true;
                src.volume = 0f;
                src.Stop();
            }
        }

        private static bool ShouldMuteAudio(SynchronizedAudio syncAudio)
        {
            if (syncAudio == null) return false;
            if (_mutedStickAudios.Contains(syncAudio)) return true;

            string objName = syncAudio.gameObject.name.ToLowerInvariant();
            if (objName.Contains("icehit") || objName.Contains("icedrag") ||
                objName.Contains("ice hit") || objName.Contains("ice drag") ||
                objName.Contains("stickhit") || objName.Contains("stickdrag"))
            {
                RegisterAndMuteExternalAudio(syncAudio);
                return true;
            }

            var sp = syncAudio.GetComponentInParent<StickPositioner>();
            if (sp != null)
            {
                var wind = _windAudioField?.GetValue(sp) as SynchronizedAudio;
                if (syncAudio != wind)
                {
                    RegisterAndMuteExternalAudio(syncAudio);
                    return true;
                }
            }

            if (syncAudio.GetComponentInParent<Stick>() != null)
            {
                RegisterAndMuteExternalAudio(syncAudio);
                return true;
            }

            return false;
        }

        [HarmonyPatch(typeof(StickPositioner), "Awake")]
        [HarmonyPostfix]
        public static void StickPositionerAwakePostfix(StickPositioner __instance)
        {
            RegisterAndMuteExternalAudio(_iceHitAudioField?.GetValue(__instance) as SynchronizedAudio);
            RegisterAndMuteExternalAudio(_iceDragAudioField?.GetValue(__instance) as SynchronizedAudio);
        }

        [HarmonyPatch(typeof(StickPositioner), "OnNetworkPostSpawn")]
        [HarmonyPostfix]
        public static void StickPositionerPostSpawnPostfix(StickPositioner __instance)
        {
            RegisterAndMuteExternalAudio(_iceHitAudioField?.GetValue(__instance) as SynchronizedAudio);
            RegisterAndMuteExternalAudio(_iceDragAudioField?.GetValue(__instance) as SynchronizedAudio);
        }

        [HarmonyPatch(typeof(StickPositioner), "UpdateAudio")]
        [HarmonyPostfix]
        public static void UpdateAudioPostfix(StickPositioner __instance)
        {
            var hitAudio = _iceHitAudioField?.GetValue(__instance) as SynchronizedAudio;
            var dragAudio = _iceDragAudioField?.GetValue(__instance) as SynchronizedAudio;

            RegisterAndMuteExternalAudio(hitAudio);
            RegisterAndMuteExternalAudio(dragAudio);

            if (NetworkRoles.IsServer && dragAudio != null)
            {
                dragAudio.Server_SetVolume(0f);
            }
        }

        [HarmonyPatch(typeof(StickPositioner), "OnGrounded", new Type[] { typeof(GameObject) })]
        [HarmonyPrefix]
        public static bool OnGroundedPrefix(StickPositioner __instance)
        {
            __instance.IsGrounded = true;
            return false;
        }

        [HarmonyPatch(typeof(SynchronizedAudio), "Server_Play")]
        [HarmonyPrefix]
        public static bool PreventStickServerPlay(SynchronizedAudio __instance) => !ShouldMuteAudio(__instance);

        [HarmonyPatch(typeof(SynchronizedAudio), "Server_PlayRpc")]
        [HarmonyPrefix]
        public static bool PreventStickClientPlayRpc(SynchronizedAudio __instance) => !ShouldMuteAudio(__instance);

        [HarmonyPatch(typeof(SynchronizedAudio), "Server_SetVolume")]
        [HarmonyPrefix]
        public static void ForceZeroStickVolumeServer(SynchronizedAudio __instance, ref float volume)
        {
            if (ShouldMuteAudio(__instance)) volume = 0f;
        }

        [HarmonyPatch(typeof(SynchronizedAudio), "OnVolumeChanged")]
        [HarmonyPrefix]
        public static void ForceZeroStickVolumeClient(SynchronizedAudio __instance, ref byte newVolume)
        {
            if (ShouldMuteAudio(__instance)) newVolume = 0;
        }
    }
}