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
    // REALISTIC WIND SYSTEM, HUD COMPASS, WINDSOCKS & AMBIENT BREEZE AUDIO
    // =========================================================================
    public static class UltimateWindManager
    {
        public const string WindMessageName = "ULTIMATE/Wind";
        private static CustomMessagingManager _registeredWindCmm = null;

        // Current interpolated wind vector in world space (m/s, XZ plane)
        public static Vector3 CurrentWindVelocity = new Vector3(1.5f, 0f, 1.0f);
        public static float CurrentWindSpeedMs => CurrentWindVelocity.magnitude;
        public static float CurrentWindSpeedKph => CurrentWindSpeedMs * 3.6f;
        public static Vector3 CurrentWindDirection => CurrentWindSpeedMs > 0.01f ? CurrentWindVelocity.normalized : Vector3.forward;

        // Server-side smooth weather state
        private static Vector3 _baseTargetWind = new Vector3(1.8f, 0f, 0.8f);
        private static Vector3 _smoothedBaseWind = new Vector3(1.8f, 0f, 0.8f);
        private static Vector3 _windSmoothVel = Vector3.zero;
        private static float _nextWeatherShiftTime = 0f;
        private static float _weatherTransitionTime = 18.0f; // Takes ~18s to smoothly shift to a new prevailing wind
        private static float _gustSeed = 133.7f;
        private static float _lastNetworkBroadcastTime = 0f;
        public static bool IsWindLockedByCommand = false;

        // Client-side interpolation target
        private static Vector3 _clientTargetWind = new Vector3(1.5f, 0f, 1.0f);
        private static GameObject _clientRuntimeObj;

        // Tunable realism limits (1.0 m/s = 3.6 KPH; 5.5 m/s = ~20 KPH)
        public static float MinWindSpeedMs = 0.4f;  // ~1.5 KPH light air
        public static float MaxWindSpeedMs = 5.6f;  // ~20.0 KPH stiff breeze
        public static float AerodynamicWindScale = 0.72f; // Scales how strongly wind affects the disc

        public static void Initialize()
        {
            _gustSeed = UnityEngine.Random.Range(10f, 999f);
            float initAngle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float initSpeed = UnityEngine.Random.Range(1.2f, 3.2f);
            _baseTargetWind = new Vector3(Mathf.Cos(initAngle) * initSpeed, 0f, Mathf.Sin(initAngle) * initSpeed);
            _smoothedBaseWind = _baseTargetWind;
            CurrentWindVelocity = _baseTargetWind;
            _clientTargetWind = _baseTargetWind;
            _nextWeatherShiftTime = Time.time + UnityEngine.Random.Range(45f, 75f);

            EnsureClientRuntime();
        }

        public static void Dispose()
        {
            if (_registeredWindCmm != null)
            {
                _registeredWindCmm.UnregisterNamedMessageHandler(WindMessageName);
                _registeredWindCmm = null;
            }
            if (_clientRuntimeObj != null)
            {
                UnityEngine.Object.Destroy(_clientRuntimeObj);
                _clientRuntimeObj = null;
            }
        }

        public static void EnsureNetworkRegistered()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null) return;
            if (_registeredWindCmm == nm.CustomMessagingManager) return;

            nm.CustomMessagingManager.UnregisterNamedMessageHandler(WindMessageName);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(WindMessageName, OnWindMessageReceived);
            _registeredWindCmm = nm.CustomMessagingManager;
        }

        public static void EnsureClientRuntime()
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer) return;
            if (_clientRuntimeObj == null)
            {
                _clientRuntimeObj = new GameObject("UltimateWindClientRuntime");
                UnityEngine.Object.DontDestroyOnLoad(_clientRuntimeObj);
                _clientRuntimeObj.AddComponent<UltimateWindClientController>();
            }
        }

        public static void ServerSetManualWind(float speedKph, float angleDeg)
        {
            IsWindLockedByCommand = true;
            float speedMs = Mathf.Clamp(speedKph / 3.6f, 0f, 12f);
            float rad = angleDeg * Mathf.Deg2Rad;
            _baseTargetWind = new Vector3(Mathf.Sin(rad) * speedMs, 0f, Mathf.Cos(rad) * speedMs);
            _weatherTransitionTime = 3.0f;
        }

        public static void ServerFixedUpdate()
        {
            if (!NetworkRoles.IsServer) return;
            EnsureNetworkRegistered();

            // 1. Pick a new prevailing wind target every 45 - 90 seconds (never jarring!)
            if (!IsWindLockedByCommand && Time.time >= _nextWeatherShiftTime)
            {
                _nextWeatherShiftTime = Time.time + UnityEngine.Random.Range(45f, 90f);
                _weatherTransitionTime = UnityEngine.Random.Range(15f, 25f);

                // Shift angle gradually by at most +/- 55 degrees from current direction
                float currentAngle = Mathf.Atan2(_baseTargetWind.z, _baseTargetWind.x) * Mathf.Rad2Deg;
                float newAngle = (currentAngle + UnityEngine.Random.Range(-55f, 55f)) * Mathf.Deg2Rad;

                // Bias toward pleasant playable breezes (6 to 15 KPH), with occasional calm or gusty spells
                float newSpeed = Mathf.Lerp(MinWindSpeedMs, MaxWindSpeedMs, Mathf.Pow(UnityEngine.Random.value, 1.25f));
                _baseTargetWind = new Vector3(Mathf.Cos(newAngle) * newSpeed, 0f, Mathf.Sin(newAngle) * newSpeed);
            }

            // 2. SmoothDamp the prevailing wind vector over 15-25 seconds
            _smoothedBaseWind = Vector3.SmoothDamp(
                _smoothedBaseWind,
                _baseTargetWind,
                ref _windSmoothVel,
                _weatherTransitionTime,
                10f,
                Time.fixedDeltaTime);

            // 3. Layer slow, organic Perlin micro-gusts on top (period ~12-16 seconds)
            float t = Time.time * 0.08f;
            float gustNoise = (Mathf.PerlinNoise(_gustSeed, t) - 0.45f) * 2f; // -0.9 to +1.1
            float swirlNoise = (Mathf.PerlinNoise(_gustSeed + 50f, t * 0.7f) - 0.5f) * 2f; // -1 to +1

            Vector3 baseDir = _smoothedBaseWind.sqrMagnitude > 0.001f ? _smoothedBaseWind.normalized : Vector3.forward;
            Vector3 perpDir = new Vector3(-baseDir.z, 0f, baseDir.x);

            float baseMag = _smoothedBaseWind.magnitude;
            float gustMag = IsWindLockedByCommand ? 0f : Mathf.Clamp(baseMag * 0.25f * gustNoise, -0.8f, 1.3f);
            float swirlMag = IsWindLockedByCommand ? 0f : baseMag * 0.12f * swirlNoise;

            CurrentWindVelocity = baseDir * Mathf.Max(0f, baseMag + gustMag) + perpDir * swirlMag;
            _clientTargetWind = CurrentWindVelocity;

            // 4. Broadcast to clients twice per second (clients smoothly interpolate in between)
            if (Time.time - _lastNetworkBroadcastTime >= 0.5f)
            {
                _lastNetworkBroadcastTime = Time.time;
                ServerBroadcastWind();
            }
        }

        private static void ServerBroadcastWind()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;
            if (nm.ConnectedClientsIds == null || nm.ConnectedClientsIds.Count == 0) return;

            var writer = new FastBufferWriter(16, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(CurrentWindVelocity.x);
                writer.WriteValueSafe(CurrentWindVelocity.z);
                nm.CustomMessagingManager.SendNamedMessageToAll(WindMessageName, writer, NetworkDelivery.Unreliable);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void OnWindMessageReceived(ulong senderId, FastBufferReader reader)
        {
            if (NetworkRoles.IsServer) return;
            try
            {
                reader.ReadValueSafe(out float wx);
                reader.ReadValueSafe(out float wz);
                _clientTargetWind = new Vector3(wx, 0f, wz);
            }
            catch { }
        }

        public static void ClientUpdate(float dt)
        {
            if (!NetworkRoles.IsServer)
            {
                CurrentWindVelocity = Vector3.Lerp(CurrentWindVelocity, _clientTargetWind, Mathf.Clamp01(dt * 3.0f));
            }
        }

        public static void SpawnFieldWindsocks(Transform fieldRoot, float groundY)
        {
            if (UltimateFrisbeePlugin.IsHeadlessServer || fieldRoot == null) return;

            Transform existing = fieldRoot.Find("UltimateWindsocksRoot");
            if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);

            GameObject root = new GameObject("UltimateWindsocksRoot");
            root.transform.SetParent(fieldRoot, false);

            float xOut = UltimateRulesManager.FieldHalfWidth + 1.2f;  // 19.7m (just outside sideline)
            float zOut = UltimateRulesManager.FieldHalfLength + 0.8f; // 50.8m (back of endzones)

            Vector3[] corners = new[]
            {
                new Vector3(-xOut, groundY,  zOut),
                new Vector3( xOut, groundY,  zOut),
                new Vector3(-xOut, groundY, -zOut),
                new Vector3( xOut, groundY, -zOut)
            };

            for (int i = 0; i < corners.Length; i++)
            {
                CreateCornerWindsock(root.transform, $"CornerWindsock_{i}", corners[i]);
            }
        }

        private static void CreateCornerWindsock(Transform parent, string name, Vector3 basePos)
        {
            GameObject sockRoot = new GameObject(name);
            sockRoot.transform.SetParent(parent, false);
            sockRoot.transform.position = basePos;

            Material template = UltimateAssetLoader.GetNativeTemplateMaterial();

            // 1. Metallic Pole (3.4m tall)
            GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Pole";
            UnityEngine.Object.Destroy(pole.GetComponent<Collider>());
            pole.transform.SetParent(sockRoot.transform, false);
            pole.transform.localPosition = new Vector3(0f, 1.7f, 0f);
            pole.transform.localScale = new Vector3(0.06f, 1.7f, 0.06f);
            if (template != null)
            {
                Material poleMat = new Material(template) { color = new Color(0.75f, 0.78f, 0.82f) };
                if (poleMat.HasProperty("_BaseColor")) poleMat.SetColor("_BaseColor", poleMat.color);
                pole.GetComponent<MeshRenderer>().material = poleMat;
            }

            // 2. Swivel Pivot at top of pole
            GameObject swivel = new GameObject("SwivelPivot");
            swivel.transform.SetParent(sockRoot.transform, false);
            swivel.transform.localPosition = new Vector3(0f, 3.35f, 0f);

            var animator = sockRoot.AddComponent<UltimateWindsockAnimator>();
            animator.SwivelPivot = swivel.transform;

            // 3. 5 Alternating Orange & White Windsock Rings
            Color orange = new Color(1.0f, 0.38f, 0.08f);
            Color white = new Color(0.95f, 0.96f, 0.96f);

            Transform prevParent = swivel.transform;
            for (int seg = 0; seg < 5; seg++)
            {
                GameObject segPivot = new GameObject($"SegPivot_{seg}");
                segPivot.transform.SetParent(prevParent, false);
                segPivot.transform.localPosition = (seg == 0) ? Vector3.zero : new Vector3(0f, 0f, 0.24f);

                GameObject cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                UnityEngine.Object.Destroy(cyl.GetComponent<Collider>());
                cyl.transform.SetParent(segPivot.transform, false);
                float radius = Mathf.Lerp(0.24f, 0.11f, seg / 4f);
                cyl.transform.localPosition = new Vector3(0f, 0f, 0.12f);
                cyl.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                cyl.transform.localScale = new Vector3(radius, 0.125f, radius);

                if (template != null)
                {
                    Color c = (seg % 2 == 0) ? orange : white;
                    Material segMat = new Material(template) { color = c };
                    if (segMat.HasProperty("_BaseColor")) segMat.SetColor("_BaseColor", c);
                    cyl.GetComponent<MeshRenderer>().material = segMat;
                }

                animator.Segments.Add(segPivot.transform);
                prevParent = segPivot.transform;
            }
        }
    }

    // Animates the 4 corner windsocks so players can visually read the wind on the field
    public class UltimateWindsockAnimator : MonoBehaviour
    {
        public Transform SwivelPivot;
        public List<Transform> Segments = new List<Transform>();
        private float _phaseOffset;

        private void Awake()
        {
            _phaseOffset = UnityEngine.Random.Range(0f, 100f);
        }

        private void LateUpdate()
        {
            if (SwivelPivot == null) return;

            Vector3 wind = UltimateWindManager.CurrentWindVelocity;
            float speedMs = wind.magnitude;

            // 1. Rotate swivel to point downstream with the wind
            if (speedMs > 0.05f)
            {
                Quaternion targetYaw = Quaternion.LookRotation(wind.normalized, Vector3.up);
                SwivelPivot.rotation = Quaternion.Slerp(SwivelPivot.rotation, targetYaw, Time.deltaTime * 4.5f);
            }

            // 2. Droop when calm, inflate horizontally + flutter when windy
            float inflateT = Mathf.Clamp01(speedMs / 5.0f); // 0 = hanging down, 1 = flying nearly horizontal
            float baseDroopDeg = Mathf.Lerp(68f, 8f, inflateT);
            float flutterFreq = Mathf.Lerp(2.0f, 11.0f, inflateT);
            float flutterAmp = Mathf.Lerp(1.5f, 7.5f, inflateT);

            for (int i = 0; i < Segments.Count; i++)
            {
                if (Segments[i] == null) continue;
                float segWave = Mathf.Sin(Time.time * flutterFreq + _phaseOffset - i * 0.8f) * flutterAmp;
                float sideWave = Mathf.Cos(Time.time * (flutterFreq * 0.7f) + _phaseOffset - i * 0.6f) * (flutterAmp * 0.6f);

                float segPitch = (i == 0) ? baseDroopDeg : (baseDroopDeg * 0.12f) + segWave;
                Segments[i].localRotation = Quaternion.Euler(segPitch, sideWave, 0f);
            }
        }
    }

    // Handles HUD Wind Compass, Ambient Outdoor Wind Audio, and Dual-Layer Wind Particles
    public class UltimateWindClientController : MonoBehaviour
    {
        private AudioSource _windAudioSource;
        private Texture2D _circleTex;
        private Texture2D _whiteTex;
        private ParticleSystem _windRibbonParticles;
        private ParticleSystem _windLeafParticles;
        private Texture2D _softGlowTex;
        private Texture2D _leafTex;

        // --- MINIMALIST CONTROLS HUD STATE & STYLES ---
        private bool _showControlsHud = true;
        private GUIStyle _hudTitleStyle;
        private GUIStyle _hudHintStyle;
        private GUIStyle _hudActionStyle;
        private GUIStyle _hudKeyTextStyle;
        private GUIStyle _hudPillTextStyle;

        private static readonly (string Action, string Key)[] _runningControls = new[]
        {
            ("Sprint / Charge", "Hold Shift"),
            ("Layout Dive",     "F"),
            ("Sky Jump",        "Ctrl + Space"),
            ("Strafe Run",      "Q / E"),
            ("Wrist Angle",     "Scroll Wheel"),
            ("Cancel Charge",   "RMB / Space"),
            ("Track Disc",      "Hold LMB"),
            ("Customize Gear", "F4")
        };

        private static readonly (string Action, string Key)[] _holdingDiscControls = new[]
        {
            ("Power + Whip",     "Hold + Release Shift"),
            ("Pump Fake",        "RMB / Space / R"),
            ("Back / Forehand",  "Mouse L / R"),
            ("Hyzer / Blade",    "Scroll Wheel"),
            ("Hammer / Scoober", "Aim High / Flip"),
            ("Pivot Step",       "W / A / S / D"),
            ("Track Disc",       "Hold LMB"),
            ("Customize Gear", "F4")
        };

        private void Start()
        {
            SetupAmbientWindAudio();
            SetupHUDTextures();
            SetupWindStreakParticles();
        }

        private void SetupAmbientWindAudio()
        {
            _windAudioSource = gameObject.AddComponent<AudioSource>();
            _windAudioSource.loop = true;
            _windAudioSource.spatialBlend = 0f; // 2D stereo ambient bed modulated by camera ear angle
            _windAudioSource.bypassReverbZones = true;
            _windAudioSource.reverbZoneMix = 0f;
            _windAudioSource.volume = 0.04f;
            _windAudioSource.clip = CreateProceduralWindLoopClip();
            _windAudioSource.Play();
        }

        // Generates a seamless 6-second warm outdoor breeze loop (Pink-filtered air rush + soft tree/grass rustle)
        private static AudioClip CreateProceduralWindLoopClip()
        {
            int sampleRate = 44100;
            float duration = 6.0f;
            int totalSamples = Mathf.RoundToInt(sampleRate * duration);
            int crossfadeSamples = sampleRate; // 1-second crossfade for a 100% click-free loop

            float[] raw = new float[totalSamples + crossfadeSamples];
            System.Random rng = new System.Random(101);

            // Paul Kellet's pink noise filter state + lowpass resonator for warm air rush
            float b0 = 0f, b1 = 0f, b2 = 0f, b3 = 0f, b4 = 0f, b5 = 0f, b6 = 0f;
            float lp1 = 0f, lp2 = 0f;

            for (int i = 0; i < raw.Length; i++)
            {
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                b0 = 0.99886f * b0 + white * 0.0555179f;
                b1 = 0.99332f * b1 + white * 0.0750759f;
                b2 = 0.96900f * b2 + white * 0.1538520f;
                b3 = 0.86650f * b3 + white * 0.3104856f;
                b4 = 0.55000f * b4 + white * 0.5329522f;
                b5 = -0.7616f * b5 - white * 0.0168980f;
                float pink = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362f) * 0.04f;
                b6 = white * 0.115926f;

                // Two-pole warm low-pass filter (~340Hz cutoff) so it sounds like open-air wind, not static hiss
                lp1 += 0.045f * (pink - lp1);
                lp2 += 0.045f * (lp1 - lp2);

                float t = (float)i / sampleRate;
                float slowSwell = 0.82f + 0.18f * Mathf.Sin(t * Mathf.PI * 2f / duration);
                raw[i] = Mathf.Clamp(lp2 * 4.2f * slowSwell, -0.9f, 0.9f);
            }

            // Crossfade tail into head for a seamless loop
            float[] finalSamples = new float[totalSamples];
            for (int i = 0; i < totalSamples; i++)
            {
                if (i < crossfadeSamples)
                {
                    float a = (float)i / crossfadeSamples;
                    finalSamples[i] = raw[i] * a + raw[totalSamples + i] * (1f - a);
                }
                else
                {
                    finalSamples[i] = raw[i];
                }
            }

            AudioClip clip = AudioClip.Create("Ultimate_OutdoorBreezeLoop", totalSamples, 1, sampleRate, false);
            clip.SetData(finalSamples, 0);
            return clip;
        }

        private void SetupHUDTextures()
        {
            _whiteTex = new Texture2D(1, 1);
            _whiteTex.SetPixel(0, 0, Color.white);
            _whiteTex.Apply();

            int size = 64;
            _circleTex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            float maxR = size * 0.48f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), center);
                    if (d > maxR)
                    {
                        _circleTex.SetPixel(x, y, Color.clear);
                    }
                    else if (d > maxR - 2.2f)
                    {
                        _circleTex.SetPixel(x, y, new Color(1f, 1f, 1f, 0.65f));
                    }
                    else
                    {
                        _circleTex.SetPixel(x, y, new Color(0.06f, 0.10f, 0.14f, 0.62f));
                    }
                }
            }
            _circleTex.Apply();
        }

        private void SetupWindStreakParticles()
        {
            CreateProceduralParticleTextures();

            Shader particleShader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                                 ?? Shader.Find("Particles/Standard Unlit")
                                 ?? Shader.Find("Sprites/Default");

            // =================================================================
            // LAYER 1: CURVING, TAPERED WIND RIBBONS (Smooth Fade-In & Fade-Out)
            // =================================================================
            GameObject ribbonObj = new GameObject("UltimateWindRibbons");
            ribbonObj.transform.SetParent(transform, false);

            _windRibbonParticles = ribbonObj.AddComponent<ParticleSystem>();

            var main = _windRibbonParticles.main;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.2f, 3.4f);
            main.startSpeed = 0f;
            main.startSize = 0.01f; // Invisible lead point; the Trail renders the ribbon!
            main.startColor = new Color(1f, 1f, 1f, 0.22f);
            main.maxParticles = 35;

            var emission = _windRibbonParticles.emission;
            emission.rateOverTime = 5f;

            var shape = _windRibbonParticles.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(34f, 5.5f, 34f);

            var vel = _windRibbonParticles.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;

            // 3D Curl Noise makes the wind ribbons weave and swirl organically in the air
            var noise = _windRibbonParticles.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(0.45f, 0.95f);
            noise.frequency = 0.35f;
            noise.scrollSpeed = 0.4f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.High;

            // Smooth bell-curve alpha fade so ribbons NEVER pop in or abruptly disappear
            var col = _windRibbonParticles.colorOverLifetime;
            col.enabled = true;
            Gradient fadeGrad = new Gradient();
            fadeGrad.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(0.95f, 0.98f, 1f), 0f),
                    new GradientColorKey(new Color(0.95f, 0.98f, 1f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.85f, 0.25f),
                    new GradientAlphaKey(0.85f, 0.70f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            col.color = new ParticleSystem.MinMaxGradient(fadeGrad);

            // Enable Trails to draw sleek, tapered aerodynamic ribbons
            var trails = _windRibbonParticles.trails;
            trails.enabled = true;
            trails.mode = ParticleSystemTrailMode.PerParticle;
            trails.ratio = 1.0f;
            trails.lifetime = new ParticleSystem.MinMaxCurve(0.45f);
            trails.minVertexDistance = 0.15f;
            trails.worldSpace = true;
            trails.dieWithParticles = true;
            trails.sizeAffectsWidth = false;

            // Taper width: thin at the nose, thickest in the middle, tapering to a point at the tail
            AnimationCurve widthCurve = new AnimationCurve(
                new Keyframe(0.0f, 0.0f),
                new Keyframe(0.15f, 0.045f),
                new Keyframe(0.6f, 0.025f),
                new Keyframe(1.0f, 0.0f)
            );
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(1.0f, widthCurve);
            trails.colorOverTrail = new ParticleSystem.MinMaxGradient(fadeGrad);

            var ribbonRenderer = ribbonObj.GetComponent<ParticleSystemRenderer>();
            ribbonRenderer.renderMode = ParticleSystemRenderMode.None; // Only render the smooth trail!
            if (particleShader != null)
            {
                Material ribbonMat = new Material(particleShader);
                ribbonMat.mainTexture = _softGlowTex;
                ribbonMat.color = Color.white;
                SetupTransparentParticleMaterial(ribbonMat);
                ribbonRenderer.trailMaterial = ribbonMat;
                ribbonRenderer.material = ribbonMat;
            }

            // =================================================================
            // LAYER 2: TUMBLING GRASS BLADES & PINE NEEDLES
            // =================================================================
            GameObject leafObj = new GameObject("UltimateWindLeaves");
            leafObj.transform.SetParent(transform, false);

            _windLeafParticles = leafObj.AddComponent<ParticleSystem>();

            var lMain = _windLeafParticles.main;
            lMain.loop = true;
            lMain.simulationSpace = ParticleSystemSimulationSpace.World;
            lMain.startLifetime = new ParticleSystem.MinMaxCurve(3.0f, 4.8f);
            lMain.startSpeed = 0f;
            lMain.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.085f);
            lMain.startRotation3D = true;
            lMain.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            lMain.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            lMain.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            // Mix of fresh turf green and sunlit dry grass/pine tones
            Gradient leafColors = new Gradient();
            leafColors.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(0.28f, 0.58f, 0.24f), 0f),
                    new GradientColorKey(new Color(0.46f, 0.68f, 0.28f), 0.5f),
                    new GradientColorKey(new Color(0.62f, 0.58f, 0.32f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(0.85f, 0f),
                    new GradientAlphaKey(0.85f, 1f)
                }
            );
            lMain.startColor = new ParticleSystem.MinMaxGradient(leafColors);
            lMain.maxParticles = 80;

            var lEmission = _windLeafParticles.emission;
            lEmission.rateOverTime = 10f;

            var lShape = _windLeafParticles.shape;
            lShape.shapeType = ParticleSystemShapeType.Box;
            lShape.scale = new Vector3(30f, 3.5f, 30f);

            var lVel = _windLeafParticles.velocityOverLifetime;
            lVel.enabled = true;
            lVel.space = ParticleSystemSimulationSpace.World;

            // Turbulence makes grass clippings bob up and down in eddies
            var lNoise = _windLeafParticles.noise;
            lNoise.enabled = true;
            lNoise.strength = new ParticleSystem.MinMaxCurve(0.55f, 1.15f);
            lNoise.frequency = 0.65f;
            lNoise.scrollSpeed = 0.8f;

            // 3D tumbling spin while airborne
            var lRot = _windLeafParticles.rotationOverLifetime;
            lRot.enabled = true;
            lRot.separateAxes = true;
            lRot.x = new ParticleSystem.MinMaxCurve(-4.5f, 4.5f);
            lRot.y = new ParticleSystem.MinMaxCurve(-4.5f, 4.5f);
            lRot.z = new ParticleSystem.MinMaxCurve(-4.5f, 4.5f);

            // Smoothly shrink/fade in and out at birth and death
            var lCol = _windLeafParticles.colorOverLifetime;
            lCol.enabled = true;
            lCol.color = new ParticleSystem.MinMaxGradient(fadeGrad);

            var lSize = _windLeafParticles.sizeOverLifetime;
            lSize.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(0.15f, 1f),
                new Keyframe(0.85f, 1f),
                new Keyframe(1f, 0f)
            );
            lSize.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            var leafRenderer = leafObj.GetComponent<ParticleSystemRenderer>();
            leafRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            if (particleShader != null)
            {
                Material leafMat = new Material(particleShader);
                leafMat.mainTexture = _leafTex;
                leafMat.color = Color.white;
                SetupTransparentParticleMaterial(leafMat);
                leafRenderer.material = leafMat;
            }
        }

        private void CreateProceduralParticleTextures()
        {
            // 1. Soft feathered gradient texture for wind ribbons (soft edges across width & length)
            int w = 64, h = 64;
            _softGlowTex = new Texture2D(w, h, TextureFormat.ARGB32, false);
            _softGlowTex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < h; y++)
            {
                float ny = (y / (float)(h - 1)) * 2f - 1f; // -1 to 1 across ribbon width
                float widthFalloff = Mathf.Exp(-ny * ny * 4.5f);
                for (int x = 0; x < w; x++)
                {
                    float nx = x / (float)(w - 1); // 0 to 1 along ribbon length
                    float lenFalloff = Mathf.Sin(nx * Mathf.PI);
                    float alpha = Mathf.Clamp01(widthFalloff * lenFalloff);
                    _softGlowTex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            _softGlowTex.Apply();

            // 2. Crisp tapered grass-blade / leaf silhouette texture
            int lw = 32, lh = 32;
            _leafTex = new Texture2D(lw, lh, TextureFormat.ARGB32, false);
            _leafTex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < lh; y++)
            {
                float ny = (y / (float)(lh - 1)) * 2f - 1f;
                for (int x = 0; x < lw; x++)
                {
                    float nx = (x / (float)(lw - 1)) * 2f - 1f;
                    // Elongated diamond/leaf shape
                    float maxHalfWidth = (1f - nx * nx) * 0.42f;
                    float dist = Mathf.Abs(ny);
                    float alpha = Mathf.Clamp01((maxHalfWidth - dist) * 6f);
                    _leafTex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            _leafTex.Apply();
        }

        private static void SetupTransparentParticleMaterial(Material mat)
        {
            if (mat == null) return;
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f); // URP Transparent
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);     // Alpha blend
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            mat.renderQueue = 3000;
        }

        private void Update()
        {
            // Toggle Minimalist Controls HUD with F1
            if (UnityEngine.InputSystem.Keyboard.current != null &&
                UnityEngine.InputSystem.Keyboard.current.f1Key.wasPressedThisFrame)
            {
                _showControlsHud = !_showControlsHud;
            }

            UltimateWindManager.ClientUpdate(Time.deltaTime);

            Vector3 wind = UltimateWindManager.CurrentWindVelocity;
            float speedMs = wind.magnitude;
            float speedNorm = Mathf.Clamp01(speedMs / UltimateWindManager.MaxWindSpeedMs);

            Camera cam = Camera.main;

            // 1. Update Ambient Wind Audio (Speed + Ear-Crosswind Acoustic Modulation)
            if (_windAudioSource != null)
            {
                float earFactor = 1.0f;
                if (cam != null && speedMs > 0.1f)
                {
                    Vector3 camForward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                    float crossDot = Mathf.Abs(Vector3.Dot(cam.transform.right, wind.normalized));
                    float headwindDot = Mathf.Max(0f, Vector3.Dot(camForward, -wind.normalized));
                    earFactor = 0.80f + 0.25f * Mathf.Max(crossDot, headwindDot * 0.85f);
                }

                float targetVol = Mathf.Lerp(0.015f, 0.22f, Mathf.Pow(speedNorm, 1.15f)) * earFactor;
                float targetPitch = Mathf.Lerp(0.78f, 1.26f, speedNorm);

                _windAudioSource.volume = Mathf.Lerp(_windAudioSource.volume, targetVol, Time.deltaTime * 4f);
                _windAudioSource.pitch = Mathf.Lerp(_windAudioSource.pitch, targetPitch, Time.deltaTime * 4f);
            }

            // 2. Update Curving Wind Ribbons & Tumbling Grass Leaves around the active camera
            if (cam != null)
            {
                Vector3 upwindOffset = (speedMs > 0.2f) ? (-wind.normalized * 6.0f) : Vector3.zero;
                Vector3 spawnCenter = cam.transform.position + upwindOffset + Vector3.up * 1.2f;
                spawnCenter.y = Mathf.Max(1.8f, spawnCenter.y);

                if (_windRibbonParticles != null)
                {
                    _windRibbonParticles.transform.position = spawnCenter;

                    var vel = _windRibbonParticles.velocityOverLifetime;
                    vel.x = wind.x * 1.65f;
                    vel.y = 0.05f;
                    vel.z = wind.z * 1.65f;

                    var emission = _windRibbonParticles.emission;
                    emission.rateOverTime = (speedMs < 1.25f) ? 0f : Mathf.Lerp(2f, 9f, speedNorm);
                }

                if (_windLeafParticles != null)
                {
                    _windLeafParticles.transform.position = spawnCenter;

                    var lVel = _windLeafParticles.velocityOverLifetime;
                    lVel.x = wind.x * 1.15f;
                    lVel.y = -0.22f;
                    lVel.z = wind.z * 1.15f;

                    var lEmission = _windLeafParticles.emission;
                    lEmission.rateOverTime = Mathf.Lerp(2f, 16f, speedNorm);
                }
            }
        }

        private void OnGUI()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            // Top-right HUD Wind Indicator Compass
            float boxW = 136f;
            float boxH = 148f;
            float margin = 22f;
            Rect panelRect = new Rect(Screen.width - boxW - margin, margin, boxW, boxH);

            Vector2 dialCenter = new Vector2(panelRect.x + boxW * 0.5f, panelRect.y + 56f);
            float dialRadius = 40f;

            if (_circleTex != null)
            {
                GUI.DrawTexture(new Rect(dialCenter.x - dialRadius, dialCenter.y - dialRadius, dialRadius * 2f, dialRadius * 2f), _circleTex);
            }

            Vector3 camForward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (camForward.sqrMagnitude < 0.001f) camForward = Vector3.forward;
            camForward.Normalize();

            Vector3 windDir = UltimateWindManager.CurrentWindDirection;
            float relAngleDeg = Vector3.SignedAngle(camForward, windDir, Vector3.up);

            float kph = UltimateWindManager.CurrentWindSpeedKph;
            Color arrowColor = kph < 7.0f
                ? new Color(0.35f, 0.92f, 0.55f, 0.95f)  // Calm/Light (Green)
                : (kph < 14.5f
                    ? new Color(1.0f, 0.84f, 0.28f, 0.95f) // Moderate Breeze (Gold)
                    : new Color(1.0f, 0.42f, 0.32f, 0.95f)); // Stiff Wind (Orange-Red)

            // Draw rotated Wind Arrow inside the compass dial
            Matrix4x4 savedMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(relAngleDeg, dialCenter);

            Color prevGuiColor = GUI.color;
            GUI.color = arrowColor;
            if (_whiteTex != null)
            {
                GUI.DrawTexture(new Rect(dialCenter.x - 2.5f, dialCenter.y - 18f, 5f, 38f), _whiteTex);
                GUI.DrawTexture(new Rect(dialCenter.x - 4.5f, dialCenter.y - 22f, 9f, 4f), _whiteTex);
                GUI.DrawTexture(new Rect(dialCenter.x - 8.5f, dialCenter.y - 17f, 17f, 4f), _whiteTex);
                GUI.DrawTexture(new Rect(dialCenter.x - 11.5f, dialCenter.y - 13f, 23f, 3.5f), _whiteTex);
                GUI.DrawTexture(new Rect(dialCenter.x - 5.5f, dialCenter.y + 17f, 11f, 3f), _whiteTex);
            }
            GUI.color = prevGuiColor;
            GUI.matrix = savedMatrix;

            GUIStyle miniStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                fontStyle = FontStyle.Bold
            };
            miniStyle.normal.textColor = new Color(0.8f, 0.85f, 0.9f, 0.65f);
            GUI.Label(new Rect(dialCenter.x - 12f, dialCenter.y - dialRadius - 12f, 24f, 16f), "WIND", miniStyle);

            GUIStyle speedStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold
            };
            speedStyle.normal.textColor = arrowColor;
            GUI.Label(new Rect(panelRect.x, dialCenter.y + dialRadius + 2f, boxW, 20f), $"{kph:F1} KPH", speedStyle);

            string relLabel = GetRelativeWindDescription(relAngleDeg, kph);
            GUIStyle subStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11
            };
            subStyle.normal.textColor = new Color(0.9f, 0.92f, 0.95f, 0.85f);
            GUI.Label(new Rect(panelRect.x, dialCenter.y + dialRadius + 20f, boxW, 18f), relLabel, subStyle);
            // Draw the Bottom-Right Context-Aware Minimalist Controls HUD
            DrawMinimalistControlsHUD();
        }

        private void EnsureControlsHudStyles()
        {
            if (_hudTitleStyle != null) return;

            _hudTitleStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };

            _hudHintStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleRight,
                fontSize = 10,
                fontStyle = FontStyle.Normal
            };
            _hudHintStyle.normal.textColor = new Color(0.68f, 0.74f, 0.80f, 0.75f);

            _hudActionStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 11,
                fontStyle = FontStyle.Normal
            };
            _hudActionStyle.normal.textColor = new Color(0.90f, 0.93f, 0.96f, 0.92f);

            _hudKeyTextStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                fontStyle = FontStyle.Bold
            };

            _hudPillTextStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };
            _hudPillTextStyle.normal.textColor = new Color(0.90f, 0.94f, 0.98f, 0.90f);
        }

        private void DrawSolidRect(Rect rect, Color color)
        {
            if (_whiteTex == null) return;
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _whiteTex);
            GUI.color = prev;
        }

        private void DrawMinimalistControlsHUD()
        {
            if (_whiteTex == null) return;
            EnsureControlsHudStyles();

            // Check if the local player is currently holding any disc
            bool isHoldingDisc = false;
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsClient)
            {
                isHoldingDisc = UltimateNetworkSync.IsPlayerHoldingAnyDisc(nm.LocalClientId);
            }

            // Blue Accent when Running / Defending, Green Accent when Holding Disc
            Color accentColor = isHoldingDisc
                ? new Color(0.18f, 0.85f, 0.48f, 0.95f)  // Green Accent
                : new Color(0.28f, 0.68f, 1.00f, 0.95f); // Blue Accent

            float margin = 22f;

            // -----------------------------------------------------------------
            // 1. COLLAPSED PILL STATE: "[F1] Show Controls" in bottom-right
            // -----------------------------------------------------------------
            if (!_showControlsHud)
            {
                float pillW = 148f;
                float pillH = 26f;
                Rect pillRect = new Rect(Screen.width - pillW - margin, Screen.height - pillH - margin, pillW, pillH);

                // Dark translucent pill background + subtle border + left accent bar
                DrawSolidRect(pillRect, new Color(0.06f, 0.09f, 0.13f, 0.72f));
                DrawSolidRect(new Rect(pillRect.x, pillRect.y, 3f, pillRect.height), accentColor);
                DrawSolidRect(new Rect(pillRect.x, pillRect.y, pillRect.width, 1f), new Color(1f, 1f, 1f, 0.12f));
                DrawSolidRect(new Rect(pillRect.x, pillRect.yMax - 1f, pillRect.width, 1f), new Color(1f, 1f, 1f, 0.12f));

                GUI.Label(pillRect, "[F1] Show Controls", _hudPillTextStyle);
                return;
            }

            // -----------------------------------------------------------------
            // 2. EXPANDED CONTEXT-AWARE CONTROLS HUD
            // -----------------------------------------------------------------
            var controls = isHoldingDisc ? _holdingDiscControls : _runningControls;
            string modeTitle = isHoldingDisc ? "HOLDING DISC" : "RUNNING / DEFENSE";

            float panelW = 262f;
            float headerH = 26f;
            float rowH = 21f;
            float padBottom = 8f;
            float panelH = headerH + (controls.Length * rowH) + padBottom;

            Rect panelRect = new Rect(
                Screen.width - panelW - margin,
                Screen.height - panelH - margin,
                panelW,
                panelH
            );

            // Outer translucent dark slate backdrop
            DrawSolidRect(panelRect, new Color(0.05f, 0.08f, 0.12f, 0.72f));

            // Subtle top/bottom/right 1px border
            Color borderCol = new Color(1f, 1f, 1f, 0.10f);
            DrawSolidRect(new Rect(panelRect.x, panelRect.y, panelRect.width, 1f), borderCol);
            DrawSolidRect(new Rect(panelRect.x, panelRect.yMax - 1f, panelRect.width, 1f), borderCol);
            DrawSolidRect(new Rect(panelRect.xMax - 1f, panelRect.y, 1f, panelRect.height), borderCol);

            // Left context-colored accent stripe (3.5px wide)
            DrawSolidRect(new Rect(panelRect.x, panelRect.y, 3.5f, panelRect.height), accentColor);

            // Header bar tint & divider line
            DrawSolidRect(new Rect(panelRect.x + 3.5f, panelRect.y, panelRect.width - 3.5f, headerH), new Color(accentColor.r, accentColor.g, accentColor.b, 0.12f));
            DrawSolidRect(new Rect(panelRect.x + 10f, panelRect.y + headerH, panelRect.width - 20f, 1f), new Color(1f, 1f, 1f, 0.08f));

            // Header Title & "[F1] Hide" Hint
            _hudTitleStyle.normal.textColor = accentColor;
            GUI.Label(new Rect(panelRect.x + 12f, panelRect.y + 3f, 165f, 20f), modeTitle, _hudTitleStyle);
            GUI.Label(new Rect(panelRect.xMax - 80f, panelRect.y + 3f, 70f, 20f), "[F1] Hide", _hudHintStyle);

            // Rows
            float y = panelRect.y + headerH + 4f;
            _hudKeyTextStyle.normal.textColor = new Color(
                Mathf.Lerp(accentColor.r, 1f, 0.45f),
                Mathf.Lerp(accentColor.g, 1f, 0.45f),
                Mathf.Lerp(accentColor.b, 1f, 0.45f),
                0.98f
            );

            for (int i = 0; i < controls.Length; i++)
            {
                var item = controls[i];

                // Action Name on the left
                GUI.Label(new Rect(panelRect.x + 12f, y, 122f, rowH - 2f), item.Action, _hudActionStyle);

                // Keybind Badge on the right
                float badgeW = Mathf.Max(46f, item.Key.Length * 6.2f + 14f);
                float badgeH = 16f;
                Rect badgeRect = new Rect(panelRect.xMax - badgeW - 10f, y + 2f, badgeW, badgeH);

                DrawSolidRect(badgeRect, new Color(1f, 1f, 1f, 0.08f));
                DrawSolidRect(new Rect(badgeRect.x, badgeRect.y, badgeRect.width, 1f), new Color(accentColor.r, accentColor.g, accentColor.b, 0.28f));

                GUI.Label(badgeRect, item.Key, _hudKeyTextStyle);

                y += rowH;
            }
        }

        private static string GetRelativeWindDescription(float relAngleDeg, float kph)
        {
            if (kph < 1.5f) return "Calm";
            float absAngle = Mathf.Abs(relAngleDeg);
            if (absAngle < 35f) return "Tailwind";
            if (absAngle > 145f) return "Headwind";
            return relAngleDeg > 0f ? "Cross (L -> R)" : "Cross (R -> L)";
        }
    }
}