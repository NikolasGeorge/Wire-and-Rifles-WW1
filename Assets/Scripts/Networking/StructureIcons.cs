using System.Collections.Generic;
using UnityEngine;

// Thumbnails for the build menu, rendered from the real props at runtime.
//
// Authored icon art would mean a texture per buildable that silently goes
// stale the moment a prop is swapped; rendering the actual prefab means the
// icon is always what you will place. Each one is rendered ONCE and cached.
//
// The preview is staged far below the level rather than on a dedicated
// layer: hijacking a layer index risks colliding with whatever the project
// already uses, while empty space is guaranteed to be empty.
public static class StructureIcons
{
    private const int IconSize = 128;
    private static readonly Vector3 StagingPoint = new Vector3(0f, -5000f, 0f);

    private static readonly Dictionary<FortificationType, Texture> cache =
        new Dictionary<FortificationType, Texture>();

    public static readonly Color Background = new Color(0.13f, 0.12f, 0.10f, 1f);

    public static Texture Get(FortificationType type)
    {
        if (cache.TryGetValue(type, out Texture cached) && cached != null)
        {
            return cached;
        }

        Texture rendered = Render(type);
        cache[type] = rendered;
        return rendered;
    }

    private static Texture Render(FortificationType type)
    {
        GameObject preview = FortificationManager.BuildVisual(
            type, StagingPoint, Quaternion.Euler(0f, 35f, 0f), out _);

        if (preview == null)
        {
            return null;
        }

        // Colliders would be pointless here and could wake physics.
        foreach (Collider collider in preview.GetComponentsInChildren<Collider>(true))
        {
            Object.Destroy(collider);
        }

        Bounds bounds = new Bounds(StagingPoint, Vector3.one);
        bool hasBounds = false;

        foreach (Renderer renderer in preview.GetComponentsInChildren<Renderer>(false))
        {
            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        RenderTexture target = new RenderTexture(IconSize, IconSize, 16)
        {
            name = "StructureIcon_" + type
        };

        GameObject cameraObject = new GameObject("StructureIconCamera");
        Camera camera = cameraObject.AddComponent<Camera>();

        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Background;
        camera.orthographic = true;
        camera.orthographicSize = Mathf.Max(0.5f, bounds.extents.magnitude * 0.85f);
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = camera.orthographicSize * 12f;
        camera.targetTexture = target;

        // Three-quarter view, slightly above: reads as a recognisable object
        // rather than a flat elevation.
        Vector3 direction = Quaternion.Euler(22f, -35f, 0f) * Vector3.forward;
        cameraObject.transform.position = bounds.center - direction * (camera.orthographicSize * 4f);
        cameraObject.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);

        camera.Render();

        // Render() is synchronous, so tearing down here is safe.
        camera.targetTexture = null;
        Object.Destroy(cameraObject);
        Object.Destroy(preview);

        return target;
    }
}
