using System.Collections.Generic;
using UnityEngine;

// Synthesized sound effects, in the same spirit as ExplosionFx's boom: no
// audio assets to import or wire up, everything generated from noise and
// tones at first use.
//
// Clips are CACHED. The existing boom rebuilt a 22,050-sample clip on every
// single explosion, which is exactly the kind of per-event allocation that
// stutters a firefight; each sound here is built once and reused.
public static class ProceduralAudio
{
    private const int SampleRate = 44100;

    private static readonly Dictionary<string, AudioClip> cache = new Dictionary<string, AudioClip>();

    // ---- Playback ----

    public static void PlayAt(AudioClip clip, Vector3 position, float volume = 1f)
    {
        if (clip != null)
        {
            AudioSource.PlayClipAtPoint(clip, position, Mathf.Clamp01(volume));
        }
    }

    // ---- Sounds ----

    // Air moving round a swung tool.
    public static AudioClip Swing => Build("swing", 0.28f, (t, i) =>
    {
        // Noise pushed through a filter that opens then shuts: a whoosh
        // rather than a hiss.
        float envelope = Mathf.Sin(t * Mathf.PI);
        return Noise(i) * envelope * envelope * 0.5f;
    }, lowPass: 0.25f);

    // Tool into a body: dull, no ring.
    public static AudioClip MeleeFlesh => Build("meleeFlesh", 0.22f, (t, i) =>
    {
        float envelope = Mathf.Exp(-18f * t);
        float body = Mathf.Sin(2f * Mathf.PI * 90f * t * 0.22f);
        return (Noise(i) * 0.6f + body * 0.4f) * envelope;
    }, lowPass: 0.09f);

    // Tool into timber or sandbags: harder, with a short knock.
    public static AudioClip MeleeHard => Build("meleeHard", 0.26f, (t, i) =>
    {
        float envelope = Mathf.Exp(-14f * t);
        float knock = Mathf.Sin(2f * Mathf.PI * 160f * t * 0.26f);
        return (Noise(i) * 0.45f + knock * 0.55f) * envelope;
    }, lowPass: 0.2f);

    // Spade through soil: grit, no tone at all.
    public static AudioClip Dig => Build("dig", 0.42f, (t, i) =>
    {
        float envelope = Mathf.Sin(t * Mathf.PI) * Mathf.Exp(-2.2f * t);
        return Noise(i) * envelope * 0.75f;
    }, lowPass: 0.35f);

    // Pin leaving a grenade: small, bright, metallic.
    public static AudioClip PinPull => Build("pinPull", 0.16f, (t, i) =>
    {
        float envelope = Mathf.Exp(-30f * t);
        float ring = Mathf.Sin(2f * Mathf.PI * 2100f * t * 0.16f)
            + 0.5f * Mathf.Sin(2f * Mathf.PI * 3300f * t * 0.16f);
        return (ring * 0.5f + Noise(i) * 0.25f) * envelope;
    });

    // Grenade landing on something hard.
    public static AudioClip Bounce => Build("bounce", 0.18f, (t, i) =>
    {
        float envelope = Mathf.Exp(-26f * t);
        float clink = Mathf.Sin(2f * Mathf.PI * 1400f * t * 0.18f);
        return (clink * 0.6f + Noise(i) * 0.3f) * envelope;
    });

    // One beat of shovel work on a fortification.
    public static AudioClip BuildTick => Build("buildTick", 0.2f, (t, i) =>
    {
        float envelope = Mathf.Exp(-16f * t);
        float thud = Mathf.Sin(2f * Mathf.PI * 210f * t * 0.2f);
        return (thud * 0.5f + Noise(i) * 0.5f) * envelope;
    }, lowPass: 0.18f);

    // Relief: soft, warm, no attack.
    public static AudioClip HealTick => Build("healTick", 0.5f, (t, i) =>
    {
        float envelope = Mathf.Sin(t * Mathf.PI) * 0.5f;
        return (Mathf.Sin(2f * Mathf.PI * 520f * t * 0.5f)
            + 0.5f * Mathf.Sin(2f * Mathf.PI * 780f * t * 0.5f)) * envelope * 0.35f;
    });

    // Ammunition changing hands: mechanical, curt.
    public static AudioClip Resupply => Build("resupply", 0.22f, (t, i) =>
    {
        float envelope = Mathf.Exp(-22f * t);
        float clack = Mathf.Sin(2f * Mathf.PI * 900f * t * 0.22f);
        return (clack * 0.45f + Noise(i) * 0.55f) * envelope;
    }, lowPass: 0.3f);

    // Seamless loop for the ear-ring under heavy fire: a high tone over a
    // low rumble. Built from whole cycles so the loop point is inaudible.
    public static AudioClip SuppressionRing => BuildLoop("suppressionRing", 1f, t =>
    {
        float ring = Mathf.Sin(2f * Mathf.PI * 1000f * t) * 0.35f;
        float rumble = Mathf.Sin(2f * Mathf.PI * 60f * t) * 0.5f;
        return ring + rumble;
    });

    // ---- Builders ----

    private static float Noise(int index)
    {
        // Deterministic hash noise: same clip every run, no Random state
        // touched (which would perturb gameplay rolls).
        int hashed = index * 1103515245 + 12345;
        hashed = (hashed >> 16) ^ hashed;
        return (hashed % 20001) / 10000f - 1f;
    }

    private static AudioClip Build(string key, float duration, System.Func<float, int, float> sample,
        float lowPass = 1f)
    {
        if (cache.TryGetValue(key, out AudioClip cached) && cached != null)
        {
            return cached;
        }

        int sampleCount = Mathf.Max(1, (int)(SampleRate * duration));
        float[] samples = new float[sampleCount];
        float filtered = 0f;

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)sampleCount;
            float raw = sample(t, i);

            // A one-pole low pass; 1 leaves the sound untouched.
            filtered = lowPass >= 1f ? raw : Mathf.Lerp(filtered, raw, lowPass);
            samples[i] = Mathf.Clamp(filtered, -1f, 1f);
        }

        AudioClip clip = AudioClip.Create(key, sampleCount, 1, SampleRate, false);
        clip.SetData(samples, 0);
        cache[key] = clip;
        return clip;
    }

    private static AudioClip BuildLoop(string key, float duration, System.Func<float, float> sample)
    {
        if (cache.TryGetValue(key, out AudioClip cached) && cached != null)
        {
            return cached;
        }

        int sampleCount = Mathf.Max(1, (int)(SampleRate * duration));
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = Mathf.Clamp(sample(i / (float)SampleRate), -1f, 1f);
        }

        AudioClip clip = AudioClip.Create(key, sampleCount, 1, SampleRate, false);
        clip.SetData(samples, 0);
        cache[key] = clip;
        return clip;
    }
}
