using System.Collections;
using UnityEngine;

// Deterministic grenade ballistics shared by the server (authoritative
// explosion position) and every client (visual grenade). Same math, same
// result — no physics engine involved.
public static class GrenadeArc
{
    public const float Gravity = -20f;
    public const float FuseSeconds = 3f;

    // Advances one step; returns true once the projectile has come to rest.
    // Grenades get one damped bounce; supply boxes (allowBounce false) stick
    // where they land.
    public static bool Step(ref Vector3 position, ref Vector3 velocity, float deltaTime, bool allowBounce = true)
    {
        velocity.y += Gravity * deltaTime;
        Vector3 nextPosition = position + velocity * deltaTime;

        Vector3 travel = nextPosition - position;
        float distance = travel.magnitude;

        if (distance > 0.0001f
            && Physics.Raycast(position, travel / distance, out RaycastHit hit, distance + 0.1f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            position = hit.point + hit.normal * 0.08f;

            if (allowBounce && velocity.magnitude > 4f)
            {
                velocity = Vector3.Reflect(velocity, hit.normal) * 0.3f;
                return false;
            }

            velocity = Vector3.zero;
            return true;
        }

        position = nextPosition;
        return false;
    }
}

// Flare ballistics: fired steeply, then a parachute catches at the top of
// the arc and it drifts down slowly, lighting the ground the whole way.
// Deterministic and shared by server and clients, exactly like GrenadeArc.
public static class FlareArc
{
    // Much weaker than the grenade's -20: a light flare cartridge keeps
    // climbing for a long time before it turns over, which is what gives it
    // the height and the air time. Lighter still than the first pass, so the
    // shot out of the barrel reads as flat rather than lobbed.
    public const float Gravity = -3.5f;

    // Once it starts dropping it never falls faster than this — the whole
    // point of a flare is the long hang time.
    public const float DescentSpeed = 0.9f;

    // Advances one step; returns true once it has settled on the ground.
    public static bool Step(ref Vector3 position, ref Vector3 velocity, float deltaTime)
    {
        velocity.y += Gravity * deltaTime;

        // Parachute: caps descent without affecting the climb.
        if (velocity.y < -DescentSpeed)
        {
            velocity.y = -DescentSpeed;
        }

        // Light air drag. Gentle enough that the flare still carries to
        // where it was aimed before it settles into a hover.
        velocity.x = Mathf.Lerp(velocity.x, 0f, deltaTime * 0.35f);
        velocity.z = Mathf.Lerp(velocity.z, 0f, deltaTime * 0.35f);

        Vector3 nextPosition = position + velocity * deltaTime;
        Vector3 travel = nextPosition - position;
        float distance = travel.magnitude;

        if (distance > 0.0001f
            && Physics.Raycast(position, travel / distance, out RaycastHit hit, distance + 0.1f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            // Burns where it lands rather than stopping dead.
            position = hit.point + hit.normal * 0.1f;
            velocity = Vector3.zero;
            return true;
        }

        position = nextPosition;
        return false;
    }
}

// Client-side flare: a burning light drifting down the same arc the server
// is spotting from, so what players see is where enemies are being revealed.
public class FlareVisual : MonoBehaviour
{
    private Vector3 velocity;
    private float lifeRemaining;
    private bool resting;
    private Light flareLight;
    private float baseIntensity;

    public static void Spawn(Vector3 origin, Vector3 velocity, float burnSeconds)
    {
        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        visual.name = "FlareVisual";
        visual.transform.position = origin;
        visual.transform.localScale = Vector3.one * 0.35f;
        Object.Destroy(visual.GetComponent<Collider>());

        visual.GetComponent<Renderer>().material = new Material(Shader.Find("Sprites/Default"))
        {
            color = new Color(1f, 0.55f, 0.2f, 0.95f)
        };

        Light light = visual.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.5f, 0.2f);
        light.intensity = 6f;
        light.range = 45f;

        FlareVisual flare = visual.AddComponent<FlareVisual>();
        flare.velocity = velocity;
        flare.lifeRemaining = burnSeconds;
        flare.flareLight = light;
        flare.baseIntensity = light.intensity;
    }

    private void Update()
    {
        lifeRemaining -= Time.deltaTime;

        if (lifeRemaining <= 0f)
        {
            Destroy(gameObject);
            return;
        }

        if (!resting)
        {
            Vector3 position = transform.position;
            resting = FlareArc.Step(ref position, ref velocity, Time.deltaTime);
            transform.position = position;
        }

        // Guttering flicker, plus a fade over the last two seconds so it dies
        // out instead of vanishing.
        float flicker = 0.85f + 0.15f * Mathf.PerlinNoise(Time.time * 9f, 0f);
        float fade = Mathf.Clamp01(lifeRemaining / 2f);

        if (flareLight != null)
        {
            flareLight.intensity = baseIntensity * flicker * fade;
        }
    }
}

// Client-side visual grenade: a small dark sphere flying the same arc the
// server simulates. Destroys itself at fuse time (the explosion FX arrives
// via its own RPC).
public class GrenadeVisual : MonoBehaviour
{
    private Vector3 velocity;
    private float lifeRemaining = GrenadeArc.FuseSeconds;
    private bool resting;
    private bool isBox;

    // fuseSeconds below zero means "a full fuse" — cooked grenades pass the
    // shortened time the server worked out.
    public static void Spawn(Vector3 origin, Vector3 velocity, bool box = false,
        GrenadeType grenadeType = GrenadeType.Frag, float fuseSeconds = -1f)
    {
        GameObject visual = null;

        // Real grenade model when available (Mills bomb / stick grenade).
        if (!box)
        {
            GrenadeVisuals visuals = GrenadeVisuals.Load();
            GameObject model = visuals != null ? visuals.GetGrenadeModel(grenadeType) : null;

            if (model != null)
            {
                visual = Object.Instantiate(model, origin, Random.rotation);
                visual.name = "GrenadeVisual";

                foreach (Collider collider in visual.GetComponentsInChildren<Collider>(true))
                {
                    Object.Destroy(collider);
                }
            }
        }

        if (visual == null)
        {
            visual = GameObject.CreatePrimitive(box ? PrimitiveType.Cube : PrimitiveType.Sphere);
            visual.name = box ? "SupplyCrateVisual" : "GrenadeVisual";
            visual.transform.position = origin;
            visual.transform.localScale = box ? Vector3.one * 0.35f : Vector3.one * 0.22f;
            Object.Destroy(visual.GetComponent<Collider>());

            visual.GetComponent<Renderer>().material = new Material(Shader.Find("Sprites/Default"))
            {
                color = box ? new Color(0.45f, 0.35f, 0.22f) : new Color(0.16f, 0.2f, 0.14f)
            };
        }

        GrenadeVisual grenade = visual.AddComponent<GrenadeVisual>();
        grenade.velocity = velocity;
        grenade.isBox = box;

        if (box)
        {
            grenade.lifeRemaining = 4f;
        }
        else if (fuseSeconds >= 0f)
        {
            grenade.lifeRemaining = fuseSeconds;
        }
    }

    private void Update()
    {
        lifeRemaining -= Time.deltaTime;

        if (lifeRemaining <= 0f)
        {
            Destroy(gameObject);
            return;
        }

        if (!resting)
        {
            Vector3 position = transform.position;
            resting = GrenadeArc.Step(ref position, ref velocity, Time.deltaTime, !isBox);
            transform.position = position;

            // A landed supply box hands off to the real crate structure that
            // the server spawns at the same spot.
            if (resting && isBox)
            {
                lifeRemaining = Mathf.Min(lifeRemaining, 0.3f);
            }
        }
    }
}

// Burning ground patch left by an incendiary grenade: a flat translucent
// orange disc with flickering flame blobs. Visual only — damage is
// server-side.
public class FireCreepFx : MonoBehaviour
{
    private float duration;
    private float age;
    private Renderer discRenderer;
    private readonly System.Collections.Generic.List<Transform> flames = new System.Collections.Generic.List<Transform>();

    public static void Spawn(Vector3 position, float radius, float duration)
    {
        GameObject root = new GameObject("FireCreepFx");
        root.transform.position = position;

        FireCreepFx fx = root.AddComponent<FireCreepFx>();
        fx.duration = duration;

        Shader shader = Shader.Find("Sprites/Default");

        GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Object.Destroy(disc.GetComponent<Collider>());
        disc.transform.SetParent(root.transform, false);
        disc.transform.localPosition = new Vector3(0f, 0.04f, 0f);
        disc.transform.localScale = new Vector3(radius * 2f, 0.006f, radius * 2f);
        fx.discRenderer = disc.GetComponent<Renderer>();
        fx.discRenderer.material = new Material(shader) { color = new Color(1f, 0.4f, 0.1f, 0.25f) };

        Random.State state = Random.state;
        Random.InitState(position.GetHashCode());

        for (int i = 0; i < 10; i++)
        {
            GameObject flame = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(flame.GetComponent<Collider>());
            flame.transform.SetParent(root.transform, false);

            Vector2 offset = Random.insideUnitCircle * radius * 0.85f;
            flame.transform.localPosition = new Vector3(offset.x, 0.15f, offset.y);
            flame.transform.localScale = Vector3.one * Random.Range(0.25f, 0.5f);

            flame.GetComponent<Renderer>().material = new Material(shader)
            {
                color = new Color(1f, Random.Range(0.35f, 0.65f), 0.05f, 0.8f)
            };

            fx.flames.Add(flame.transform);
        }

        Random.state = state;
    }

    private void Update()
    {
        age += Time.deltaTime;

        if (age >= duration)
        {
            Destroy(gameObject);
            return;
        }

        // Flicker the flame blobs; fade everything in the final 2 seconds.
        float fade = Mathf.Clamp01((duration - age) / 2f);

        for (int i = 0; i < flames.Count; i++)
        {
            float pulse = 0.8f + 0.35f * Mathf.Sin(age * (7f + i) + i * 1.7f);
            Vector3 baseScale = Vector3.one * 0.35f;
            flames[i].localScale = baseScale * pulse;
        }

        if (discRenderer != null)
        {
            Color color = discRenderer.material.color;
            color.a = 0.25f * fade;
            discRenderer.material.color = color;
        }
    }
}

// Explosion / smoke visual: an expanding transparent sphere plus a short
// synthesized boom. Self-contained so it outlives the player that threw it.
public class ExplosionFx : MonoBehaviour
{
    public static void Spawn(Vector3 position, bool smoke)
    {
        // War FX particle prefabs when available; primitive spheres otherwise.
        GrenadeVisuals visuals = GrenadeVisuals.Load();
        GameObject fxPrefab = visuals != null ? (smoke ? visuals.smokeFx : visuals.explosionFx) : null;

        if (fxPrefab != null)
        {
            GameObject particles = Object.Instantiate(fxPrefab, position, Quaternion.identity);
            Object.Destroy(particles, smoke ? 16f : 6f);

            if (!smoke)
            {
                PlayBoom(position);
            }

            return;
        }

        GameObject fx = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        fx.name = smoke ? "SmokeFx" : "ExplosionFx";
        fx.transform.position = position;
        Object.Destroy(fx.GetComponent<Collider>());

        Renderer fxRenderer = fx.GetComponent<Renderer>();
        fxRenderer.material = new Material(Shader.Find("Sprites/Default"));

        ExplosionFx effect = fx.AddComponent<ExplosionFx>();
        effect.smoke = smoke;

        if (!smoke)
        {
            PlayBoom(position);
        }
    }

    private bool smoke;
    private float age;

    private void Update()
    {
        age += Time.deltaTime;

        Renderer fxRenderer = GetComponent<Renderer>();

        if (smoke)
        {
            // Puff up fast, linger, fade out.
            const float duration = 12f;
            float grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(age / 1.2f));
            transform.localScale = Vector3.one * Mathf.Lerp(1f, 9f, grow);

            float alpha = age < duration - 2f ? 0.82f : Mathf.Lerp(0.82f, 0f, (age - (duration - 2f)) / 2f);
            fxRenderer.material.color = new Color(0.75f, 0.75f, 0.72f, alpha);

            if (age >= duration)
            {
                Destroy(gameObject);
            }

            return;
        }

        const float blastDuration = 0.45f;
        float t = Mathf.Clamp01(age / blastDuration);
        transform.localScale = Vector3.one * Mathf.Lerp(0.5f, 8f, Mathf.Sqrt(t));
        fxRenderer.material.color = new Color(1f, 0.55f, 0.15f, Mathf.Lerp(0.9f, 0f, t));

        if (age >= blastDuration)
        {
            Destroy(gameObject);
        }
    }

    // Short synthesized low boom (same technique as the footstep audio):
    // filtered noise burst with exponential decay.
    public static void PlayBoom(Vector3 position)
    {
        const int sampleRate = 44100;
        const float duration = 0.5f;
        int sampleCount = (int)(sampleRate * duration);

        AudioClip clip = AudioClip.Create("Boom", sampleCount, 1, sampleRate, false);
        float[] samples = new float[sampleCount];

        System.Random random = new System.Random(12345);
        float filtered = 0f;

        for (int i = 0; i < sampleCount; i++)
        {
            float noise = (float)(random.NextDouble() * 2.0 - 1.0);
            filtered = Mathf.Lerp(filtered, noise, 0.08f);

            float envelope = Mathf.Exp(-6f * i / (float)sampleCount);
            samples[i] = filtered * envelope;
        }

        clip.SetData(samples, 0);
        AudioSource.PlayClipAtPoint(clip, position, 0.8f);
    }
}
