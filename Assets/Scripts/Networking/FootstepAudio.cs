using UnityEngine;

// Distance-based footsteps driven purely from root movement, so remote
// players (moved by NetworkTransform) produce steps with no extra RPCs. The
// step sound is synthesized at runtime — no clip asset needed.
public class FootstepAudio : MonoBehaviour
{
    public float strideLength = 2.4f;
    public float minimumSpeed = 0.8f;
    public float volume = 0.12f;

    private static AudioClip stepClip;

    private AudioSource source;
    private PlayerNetworkHealth health;
    private PlayerController playerController;
    private Vector3 lastPosition;
    private float distanceSinceStep;
    private bool wasMoving;

    private void Start()
    {
        health = GetComponent<PlayerNetworkHealth>();
        playerController = GetComponent<PlayerController>();
        lastPosition = transform.position;

        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 2f;
        source.maxDistance = 25f;

        if (stepClip == null)
        {
            stepClip = GenerateStepClip();
        }
    }

    private void Update()
    {
        Vector3 delta = transform.position - lastPosition;
        lastPosition = transform.position;

        if (health != null && health.State != PlayerLifeState.Alive)
        {
            distanceSinceStep = 0f;
            return;
        }

        // No steps in the air. Owned players know from their controller;
        // remote players (controller disabled) check the ground directly.
        bool grounded = playerController != null && playerController.enabled
            ? playerController.IsGrounded
            : Physics.Raycast(transform.position + Vector3.up * 0.3f, Vector3.down, 1.1f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

        if (!grounded)
        {
            distanceSinceStep = 0f;
            return;
        }

        delta.y = 0f;
        float speed = Time.deltaTime > 0f ? delta.magnitude / Time.deltaTime : 0f;

        if (speed < minimumSpeed)
        {
            distanceSinceStep = 0f;
            wasMoving = false;
            return;
        }

        // Play the first step the instant movement starts, instead of
        // waiting a full stride length to accumulate.
        if (!wasMoving)
        {
            wasMoving = true;
            distanceSinceStep = 0f;
            PlayStep();
            return;
        }

        distanceSinceStep += delta.magnitude;

        if (distanceSinceStep >= strideLength)
        {
            distanceSinceStep = 0f;
            PlayStep();
        }
    }

    private void PlayStep()
    {
        source.pitch = Random.Range(0.88f, 1.12f);
        source.PlayOneShot(stepClip, volume);
    }

    // A short crunchy noise burst with a fast decay — reads as a boot on dirt.
    private static AudioClip GenerateStepClip()
    {
        const int sampleRate = 44100;
        const float duration = 0.09f;
        int sampleCount = (int)(sampleRate * duration);
        float[] samples = new float[sampleCount];

        System.Random random = new System.Random(1857);
        float previous = 0f;

        for (int i = 0; i < sampleCount; i++)
        {
            float progress = (float)i / sampleCount;
            float envelope = Mathf.Exp(-progress * 9f);

            // Low-passed noise: average with the previous sample to soften
            // the hiss into a thud.
            float noise = (float)(random.NextDouble() * 2.0 - 1.0);
            float filtered = (noise + previous * 3f) * 0.25f;
            previous = filtered;

            samples[i] = filtered * envelope;
        }

        AudioClip clip = AudioClip.Create("FootstepStep", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);

        return clip;
    }
}
