using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Suppression: incoming fire that passes close to you degrades your ability
// to shoot back — camera shake, screen blur and a vignette, plus an accuracy
// penalty.
//
// Suppression is a CONTINUOUS level, never a switch, and every part of it
// eases in and out. Each near-miss adds suppression in proportion to the
// damage that round could have done, into a per-shooter pool that bleeds off
// continuously. That gives each weapon its own character for free:
//   - bolt-action -> one heavy pulse per shot, mostly drained before the next
//   - LMG         -> weak individual rounds arriving faster than the bleed,
//                    so they pile into sustained pressure
//   - a group     -> pools sum, so several rifles pin like one machine gun
// Camera shake and the accuracy penalty are held back until the level clears
// shakeThreshold, which keeps a lone rifle duel feeling clean while still
// showing the near-miss as blur and vignette.
//
// Cover cuts all of it down: the more enclosed you are, the less rounds
// cracking past actually rattle you. A man in a bunker barely notices what
// pins a man crouched behind a single sandbag.
//
// A plain MonoBehaviour, not a NetworkBehaviour: it is added at runtime, and
// FishNet needs its NetworkBehaviours present on the prefab at spawn time.
// The server relays near-misses through PlayerNetworkSetup's TargetRpc, which
// then feeds RegisterNearMiss here on the victim's own client.
public class PlayerSuppression : MonoBehaviour
{
    // Every live player, so the server can scan shot lines without a
    // FindObjectsByType call on every single bullet.
    private static readonly List<PlayerSuppression> all = new List<PlayerSuppression>();

    [Header("Detection")]
    [Tooltip("How close a bullet must pass to count as suppressing fire.")]
    public float nearMissRadius = 3f;

    [Header("Contribution")]
    [Tooltip("Suppression added per point of damage the round could have done. A bolt-action's big single hit lands one strong pulse; an LMG's weaker rounds land many small ones.")]
    public float suppressionPerDamage = 0.005f;

    [Tooltip("How fast one shooter's accumulated suppression bleeds off. This is what makes a slow bolt-action spike fade before the next round.")]
    public float suppressorDecayPerSecond = 1f;

    [Tooltip("Ceiling on a single shooter's accumulated suppression.")]
    public float maxPerSuppressor = 1f;

    [Tooltip("Multiplier when the round actually connects. Being hit is far more alarming than being missed.")]
    public float hitSuppressionMultiplier = 2f;

    [Tooltip("Scales the headshot death effect. 1 = exactly as long as emptying an LMG magazine into someone, which is where the duration is derived from.")]
    public float headshotKillShockScale = 1f;

    [Header("Death Fade")]
    [Tooltip("Seconds for the screen to fade to black after dying. Downed does not fade — only a real death does.")]
    public float deathFadeSeconds = 3f;

    [Tooltip("Seconds to fade after a headshot kill. Faster, so the lights go out abruptly.")]
    public float headshotDeathFadeSeconds = 1.5f;

    [Tooltip("Blur that rides along with the fade to black, so vision goes soft as the lights go out.")]
    [Range(0f, 1f)]
    public float deathFadeBlur = 1f;

    [Tooltip("Constant blur while downed — conscious but not focusing on anything.")]
    [Range(0f, 1f)]
    public float downedBlur = 0.45f;

    [Header("Audio")]
    [Tooltip("Loudness of the ear-ring at full suppression. The visuals alone do not convey being pinned nearly as well as this does.")]
    [Range(0f, 1f)]
    public float maxRingVolume = 0.35f;

    [Header("Blast Shake")]
    [Tooltip("Seconds for a nearby explosion's jolt to settle.")]
    public float blastShakeDecay = 0.8f;

    [Header("Response")]
    [Tooltip("Seconds to ease toward rising suppression. Larger is smoother and slower.")]
    public float rampUpSmoothing = 0.35f;

    [Tooltip("Seconds to ease back down once fire stops.")]
    public float falloffSmoothing = 0.9f;

    [Tooltip("Seconds to ease exposure/cover changes, so ducking or aiming never snaps the effects.")]
    public float multiplierSmoothing = 0.4f;

    [Header("Effect Strength")]
    [Tooltip("Deadzone only: shake starts almost immediately, so every weapon rattles you. Just high enough that trailing-off suppression stops jittering the camera.")]
    [Range(0f, 1f)]
    public float shakeThreshold = 0.03f;

    [Tooltip("How sharply shake and spread ramp in once past the threshold. 2 = full bite at half the suppression it would otherwise take.")]
    public float disruptionRamp = 2f;

    public float maxShakeDegrees = 4.05f;
    public float shakeFrequency = 13f;

    public float maxVignette = 1f;

    [Tooltip("Blur strength. 1.5 is the engine's ceiling; past that, blur can only be deepened by pulling the focus band in (gaussianEnd below).")]
    public float maxBlurRadius = 1.5f;

    public float maxInaccuracyDegrees = 4f;

    [Header("Exposure")]
    [Tooltip("How far to look for cover around the player.")]
    public float coverRayDistance = 2.5f;

    [Tooltip("Exposure when fully enclosed and hunkered down. 1 = cover does nothing.")]
    [Range(0f, 1f)]
    public float minCoverExposure = 0.25f;

    [Tooltip("Exposure while aiming down sights, as a blend toward fully exposed. Lining up a shot means showing yourself.")]
    [Range(0f, 1f)]
    public float aimingExposureWeight = 0.8f;

    [Tooltip("Exposure multiplier while crouched and not aiming — deliberately keeping your head down.")]
    [Range(0f, 1f)]
    public float hunkeredExposureMultiplier = 0.5f;

    public float coverSampleInterval = 0.25f;
    public int coverRayCount = 8;

    [Header("Trench Walls")]
    [Tooltip("Suppression multiplier while inside a friendly trench wall's protection. Does not stack with more walls.")]
    [Range(0f, 1f)]
    public float trenchWallMultiplier = 0.5f;

    public float trenchWallRadius = 5f;

    private class Suppressor
    {
        public float amount;
    }

    private readonly Dictionary<int, Suppressor> suppressors = new Dictionary<int, Suppressor>();
    private readonly List<int> expired = new List<int>();

    private float level;
    private float levelVelocity;
    private float deathShockRemaining;

    private float blastShake;
    private bool deathFadeActive;
    private float deathFadeElapsed;
    private float deathFadeDuration;
    private bool headshotDeath;
    private bool wasDead;
    private float smoothedMultiplier = 1f;
    private float multiplierVelocity;
    private float coverage;
    private float trenchMultiplier = 1f;
    private float coverTimer;

    private PlayerNetworkSetup setup;
    private PlayerNetworkHealth health;
    private PlayerController controller;
    private BoltActionRifle rifle;
    private Camera playerCamera;
    private bool wasAlive = true;

    private Volume volume;
    private Vignette vignette;
    private DepthOfField depthOfField;
    private AudioSource ringSource;

    // 0 = clear, 1 = fully suppressed. Drives every effect.
    public float Level => level;

    // How much of you the incoming fire actually has to work with. Cover
    // lowers it; aiming raises it back up, because you cannot line up a shot
    // from behind a wall without leaning into the open.
    public float Exposure { get; private set; } = 1f;

    // Applied by PlayerController inside HandleLook, where it owns the camera
    // rotation — returning an offset rather than writing the transform here
    // keeps the shake from compounding frame to frame.
    public Vector3 CameraShakeEuler { get; private set; }

    // How much suppression is actually disrupting you, as opposed to merely
    // visible. Held at zero until the level clears shakeThreshold, so the
    // mechanical bite (shake, spread) never lands in a lone rifle duel —
    // only the blur and vignette do.
    private float DisruptionFactor =>
        Mathf.Clamp01(Mathf.InverseLerp(shakeThreshold, 1f, level) * disruptionRamp);

    // Extra spread while suppressed, added by BoltActionRifle.
    public float InaccuracyPenalty => maxInaccuracyDegrees * DisruptionFactor;

    private void Awake()
    {
        setup = GetComponent<PlayerNetworkSetup>();
        health = GetComponent<PlayerNetworkHealth>();
        controller = GetComponent<PlayerController>();
        rifle = GetComponentInChildren<BoltActionRifle>(true);
    }

    private void OnEnable()
    {
        all.Add(this);
    }

    private void OnDisable()
    {
        all.Remove(this);

        suppressors.Clear();
        level = 0f;
        CameraShakeEuler = Vector3.zero;

        if (volume != null)
        {
            volume.gameObject.SetActive(false);
        }

        // Otherwise the ring keeps sounding through death and respawn.
        if (ringSource != null)
        {
            ringSource.volume = 0f;
            ringSource.Stop();
        }
    }

    // ---- Server: scan a shot line and notify anyone it passed close to ----

    public static void ServerApplyShotSuppression(BoltActionRifle weapon, Vector3 start, Vector3 end,
        Team shooterTeam, int attackerClientId, NetworkObject hitTarget = null)
    {
        if (weapon == null)
        {
            return;
        }

        foreach (PlayerSuppression victim in all)
        {
            PlayerNetworkSetup victimSetup = victim.setup;

            if (victimSetup == null || victimSetup.OwnerId == attackerClientId)
            {
                continue;
            }

            // Friendly fire does not suppress — otherwise a teammate firing
            // over your shoulder would pin you as effectively as the enemy.
            if (victimSetup.AssignedTeam == shooterTeam)
            {
                continue;
            }

            PlayerNetworkHealth victimHealth = victim.GetComponent<PlayerNetworkHealth>();

            if (victimHealth != null && victimHealth.State != PlayerLifeState.Alive)
            {
                continue;
            }

            // Measure against the chest rather than the feet.
            Vector3 chest = victim.transform.position + Vector3.up * 1.2f;

            if (DistanceToSegment(chest, start, end) <= victim.nearMissRadius)
            {
                // Weighted by what that round could have done at this range,
                // so a far-off shot with the damage bled out of it is far
                // less alarming than one that could have killed you.
                float potentialDamage = weapon.GetSuppressionWeightAt(Vector3.Distance(start, chest));

                // A round that actually connected counts for more. This
                // upgrades the near-miss the victim already qualified for
                // rather than stacking a second pulse on top of it.
                if (hitTarget != null && victimSetup.NetworkObject == hitTarget)
                {
                    potentialDamage *= victim.hitSuppressionMultiplier;
                }

                victimSetup.ServerNotifySuppression(attackerClientId, potentialDamage);
            }
        }
    }

    // Death effect for the man who just lost his head: his last moments white
    // out into full shake and blur. Deliberately no alive-check — he is
    // already dead when it lands — and it does not care what weapon killed
    // him, only that it was a headshot.
    public static void ServerApplyHeadshotKillEffect(NetworkObject victim)
    {
        if (victim == null)
        {
            return;
        }

        PlayerSuppression suppression = victim.GetComponent<PlayerSuppression>();
        PlayerNetworkSetup victimSetup = victim.GetComponent<PlayerNetworkSetup>();

        if (suppression == null || victimSetup == null)
        {
            return;
        }

        victimSetup.ServerNotifyDeathShock(MagDumpSeconds() * suppression.headshotKillShockScale);
    }

    // How long it takes to empty an LMG magazine into someone. Derived from
    // the LMG's own profile so retuning the gun retunes this with it.
    private static float MagDumpSeconds()
    {
        WeaponProfile lmg = WeaponProfiles.Get(WeaponId.Lmg);
        return Mathf.Max(1f, lmg.clipSize * lmg.fireInterval);
    }

    // Pins the effects at full for a fixed time, ignoring cover and exposure
    // — this is a death effect, not incoming fire to be hidden from.
    public void ApplyDeathShock(float seconds)
    {
        deathShockRemaining = Mathf.Max(deathShockRemaining, seconds);

        // This RPC and the replicated death state can arrive in either order,
        // so flag it for a fade not yet started and shorten one already
        // running.
        headshotDeath = true;

        if (deathFadeActive)
        {
            deathFadeDuration = headshotDeathFadeSeconds;
        }
    }

    // Fullscreen fade, drawn at a high GUI depth so it sits BEHIND the deploy
    // screen — the black becomes that screen's backdrop instead of hiding it.
    private void OnGUI()
    {
        if (!deathFadeActive || setup == null || !setup.IsOwner)
        {
            return;
        }

        float alpha = deathFadeDuration <= 0f ? 1f : Mathf.Clamp01(deathFadeElapsed / deathFadeDuration);

        if (alpha <= 0f)
        {
            return;
        }

        int previousDepth = GUI.depth;
        Color previousColor = GUI.color;

        GUI.depth = 100;
        GUI.color = new Color(0f, 0f, 0f, alpha);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);

        GUI.color = previousColor;
        GUI.depth = previousDepth;
    }

    private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector3 line = end - start;
        float lengthSquared = line.sqrMagnitude;

        if (lengthSquared < 0.0001f)
        {
            return Vector3.Distance(point, start);
        }

        float t = Mathf.Clamp01(Vector3.Dot(point - start, line) / lengthSquared);
        return Vector3.Distance(point, start + line * t);
    }

    // ---- Owner: accumulate and respond ----

    public void RegisterNearMiss(int attackerClientId, float potentialDamage)
    {
        if (!suppressors.TryGetValue(attackerClientId, out Suppressor suppressor))
        {
            suppressor = new Suppressor();
            suppressors[attackerClientId] = suppressor;
        }

        suppressor.amount = Mathf.Min(maxPerSuppressor,
            suppressor.amount + potentialDamage * suppressionPerDamage);
    }

    private void Update()
    {
        // Every effect here is local to the player being shot at, so the
        // server's copies and other players' copies do no work.
        if (setup == null || !setup.IsOwner)
        {
            return;
        }

        // Respawning wipes the slate — otherwise the headshot death spike,
        // which is deliberately allowed to overshoot, would still be draining
        // when the player is back on their feet.
        bool alive = health == null || health.State == PlayerLifeState.Alive;

        if (alive && !wasAlive)
        {
            suppressors.Clear();
            deathShockRemaining = 0f;
            level = 0f;
            levelVelocity = 0f;
            CameraShakeEuler = Vector3.zero;

            deathFadeActive = false;
            deathFadeElapsed = 0f;
            headshotDeath = false;
        }

        wasAlive = alive;

        // Only a real death fades out — being downed keeps the screen clear
        // so you can read the bleedout timer and see a medic coming.
        bool dead = health != null && health.State == PlayerLifeState.Dead;

        if (dead && !wasDead)
        {
            deathFadeActive = true;
            deathFadeElapsed = 0f;
            deathFadeDuration = headshotDeath ? headshotDeathFadeSeconds : deathFadeSeconds;
        }

        wasDead = dead;

        if (deathFadeActive)
        {
            deathFadeElapsed += Time.deltaTime;
        }

        SampleSurroundings();
        UpdateExposure();

        // Every shooter's pool bleeds off continuously. A bolt-action lands
        // one big pulse that has mostly drained before the next round; an
        // LMG's small rounds arrive faster than the bleed and pile up.
        float target = 0f;
        expired.Clear();

        foreach (KeyValuePair<int, Suppressor> entry in suppressors)
        {
            entry.Value.amount = Mathf.MoveTowards(entry.Value.amount, 0f,
                suppressorDecayPerSecond * Time.deltaTime);

            if (entry.Value.amount <= 0.001f)
            {
                expired.Add(entry.Key);
                continue;
            }

            target += entry.Value.amount;
        }

        foreach (int key in expired)
        {
            suppressors.Remove(key);
        }

        // Exposure, trench protection and the class passive all scale the
        // incoming fire down. Trench walls are a flat area bonus that does
        // not stack, so a second wall adds nothing. Smoothed so that ducking
        // or coming out of ADS eases the effects instead of cutting them.
        float classMultiplier = setup != null
            ? Mathf.Max(0f, PlayerClasses.Get(setup.AssignedClass).suppressionMultiplier)
            : 1f;

        smoothedMultiplier = Mathf.SmoothDamp(smoothedMultiplier,
            Exposure * trenchMultiplier * classMultiplier, ref multiplierVelocity,
            multiplierSmoothing);

        target = Mathf.Clamp01(target) * smoothedMultiplier;

        // A headshot death overrides everything: cover, exposure and how much
        // fire was actually coming your way are all irrelevant once your head
        // is gone.
        if (deathShockRemaining > 0f)
        {
            deathShockRemaining -= Time.deltaTime;
            target = 1f;
        }

        // Eases in, and eases out more slowly still, so nothing about
        // suppression ever pops on or off.
        level = Mathf.SmoothDamp(level, target, ref levelVelocity,
            target > level ? rampUpSmoothing : falloffSmoothing);
        level = Mathf.Clamp01(level);

        UpdateShake();
        UpdatePostProcessing();
        UpdateRing();
    }

    // A low rumble and high ring that rise with suppression. Non-spatial and
    // parented to the player: it is what the soldier hears, not a sound in
    // the world.
    private void UpdateRing()
    {
        float target = maxRingVolume * Mathf.Max(level, blastShake);

        if (ringSource == null)
        {
            if (target <= 0.001f)
            {
                return;
            }

            GameObject ringObject = new GameObject("SuppressionRing");
            ringObject.transform.SetParent(transform, false);

            ringSource = ringObject.AddComponent<AudioSource>();
            ringSource.clip = ProceduralAudio.SuppressionRing;
            ringSource.loop = true;
            ringSource.spatialBlend = 0f;
            ringSource.playOnAwake = false;
            ringSource.volume = 0f;
            ringSource.Play();
        }

        // Eased rather than snapped, so it swells and dies away with the
        // rest of the effects instead of clicking on.
        ringSource.volume = Mathf.MoveTowards(ringSource.volume, target, Time.deltaTime * 0.8f);
    }

    // Periodic scan of the player's surroundings: how boxed-in they are, and
    // whether a trench wall covers them. Throttled — geometry does not move
    // fast enough to need this every frame.
    private void SampleSurroundings()
    {
        coverTimer -= Time.deltaTime;

        if (coverTimer > 0f)
        {
            return;
        }

        coverTimer = coverSampleInterval;

        // Rays outward from the chest: the fraction hitting solid geometry
        // is how enclosed the player is. Overhead counts double, since a
        // roof is what separates a bunker from a sandbag line.
        Vector3 origin = transform.position + Vector3.up * 1.2f;
        int rays = Mathf.Max(1, coverRayCount);
        int hits = 0;
        int total = rays + 2;

        for (int i = 0; i < rays; i++)
        {
            Vector3 direction = Quaternion.Euler(0f, 360f / rays * i, 0f) * Vector3.forward;

            if (SampleCoverRay(origin, direction))
            {
                hits++;
            }
        }

        if (SampleCoverRay(origin, Vector3.up))
        {
            hits += 2;
        }

        coverage = (float)hits / total;
        trenchMultiplier = InsideTrenchWallCover() ? trenchWallMultiplier : 1f;
    }

    // Flat, non-stacking: being inside one trench wall's area is the same as
    // being inside three.
    private bool InsideTrenchWallCover()
    {
        foreach (FortificationStructure structure in FortificationStructure.All)
        {
            if (structure == null || !structure.complete || structure.type != FortificationType.TrenchWall)
            {
                continue;
            }

            if (Vector3.Distance(structure.transform.position, transform.position) <= trenchWallRadius)
            {
                return true;
            }
        }

        return false;
    }

    // Cover only helps while you are actually using it. Hunkering down
    // behind a wall makes you nearly immune to suppression; leaning out to
    // aim gives most of that protection straight back, which is why the
    // sniper lining up a shot gets rattled and the man waiting does not.
    private void UpdateExposure()
    {
        float exposure = Mathf.Lerp(1f, minCoverExposure, coverage);

        bool aiming = rifle != null && rifle.isAiming;
        bool crouching = controller != null && controller.IsCrouching;

        if (aiming)
        {
            exposure = Mathf.Lerp(exposure, 1f, aimingExposureWeight);
        }
        else if (crouching)
        {
            exposure *= hunkeredExposureMultiplier;
        }

        Exposure = Mathf.Clamp01(exposure);
    }

    private bool SampleCoverRay(Vector3 origin, Vector3 direction)
    {
        if (!Physics.Raycast(origin, direction, out RaycastHit hit, coverRayDistance,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        // Bodies are not cover.
        return hit.collider.GetComponentInParent<PlayerNetworkSetup>() == null;
    }

    // A blast right next to you rattles the camera regardless of how
    // suppressed you were — it is a physical jolt, not incoming fire.
    public void ApplyBlastShake(float strength)
    {
        blastShake = Mathf.Max(blastShake, Mathf.Clamp01(strength));
    }

    private void UpdateShake()
    {
        blastShake = Mathf.MoveTowards(blastShake, 0f, Time.deltaTime / Mathf.Max(0.05f, blastShakeDecay));

        float shakeFactor = Mathf.Max(DisruptionFactor, blastShake);

        if (shakeFactor <= 0.001f)
        {
            CameraShakeEuler = Vector3.zero;
            return;
        }

        // Perlin rather than Random so the shake drifts instead of buzzing.
        float time = Time.time * shakeFrequency;
        float amplitude = maxShakeDegrees * shakeFactor;

        CameraShakeEuler = new Vector3(
            (Mathf.PerlinNoise(time, 0f) - 0.5f) * 2f * amplitude,
            (Mathf.PerlinNoise(0f, time) - 0.5f) * 2f * amplitude,
            (Mathf.PerlinNoise(time, time) - 0.5f) * amplitude);
    }

    private void UpdatePostProcessing()
    {
        // Blur has three independent sources: incoming fire, lying downed,
        // and dying. The strongest wins rather than stacking.
        float downed = health != null && health.State == PlayerLifeState.Downed ? downedBlur : 0f;
        float fading = deathFadeActive && deathFadeDuration > 0f
            ? Mathf.Clamp01(deathFadeElapsed / deathFadeDuration) * deathFadeBlur
            : 0f;

        float blurLevel = Mathf.Max(level, Mathf.Max(downed, fading));

        if (level <= 0.001f && blurLevel <= 0.001f)
        {
            if (volume != null && volume.gameObject.activeSelf)
            {
                volume.gameObject.SetActive(false);
            }

            return;
        }

        EnsureVolume();

        if (volume == null)
        {
            return;
        }

        if (!volume.gameObject.activeSelf)
        {
            volume.gameObject.SetActive(true);
        }

        if (vignette != null)
        {
            vignette.intensity.value = maxVignette * level;
        }

        if (depthOfField != null)
        {
            // The blur band starts right at the camera and tightens as
            // suppression climbs, so even a low level softens distant
            // targets — exactly where you are trying to aim.
            depthOfField.gaussianEnd.value = Mathf.Lerp(30f, 5f, blurLevel);
            depthOfField.gaussianMaxRadius.value = Mathf.Lerp(maxBlurRadius * 0.1f, maxBlurRadius, blurLevel);
        }
    }

    // Built at runtime so no scene volume or profile asset has to be wired up.
    private void EnsureVolume()
    {
        if (volume != null)
        {
            return;
        }

        if (playerCamera == null)
        {
            playerCamera = GetComponentInChildren<Camera>(true);
        }

        // Post-processing has to be on for the camera or none of this shows.
        if (playerCamera != null)
        {
            UniversalAdditionalCameraData cameraData = playerCamera.GetUniversalAdditionalCameraData();

            if (cameraData != null)
            {
                cameraData.renderPostProcessing = true;
            }
        }

        GameObject volumeObject = new GameObject("SuppressionVolume");
        volumeObject.transform.SetParent(transform, false);

        volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 100f;
        volume.weight = 1f;

        VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
        volume.profile = profile;

        vignette = profile.Add<Vignette>(true);
        vignette.intensity.overrideState = true;
        vignette.smoothness.overrideState = true;
        vignette.smoothness.value = 0.6f;
        vignette.color.overrideState = true;
        vignette.color.value = new Color(0.04f, 0.03f, 0.02f);

        depthOfField = profile.Add<DepthOfField>(true);
        depthOfField.mode.overrideState = true;
        depthOfField.mode.value = DepthOfFieldMode.Gaussian;
        depthOfField.gaussianStart.overrideState = true;
        depthOfField.gaussianStart.value = 0.1f;
        depthOfField.gaussianEnd.overrideState = true;
        depthOfField.gaussianMaxRadius.overrideState = true;
        depthOfField.highQualitySampling.overrideState = true;
        depthOfField.highQualitySampling.value = false;
    }
}
