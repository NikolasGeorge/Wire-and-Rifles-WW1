using UnityEngine;

// Scales IMGUI drawing to a 1080p-reference coordinate space so the
// prototype OnGUI screens render at the same proportions on every
// resolution. Call Begin() first in OnGUI, then lay out using Width/Height
// instead of Screen.width/Screen.height.
public static class GuiScale
{
    public const float ReferenceHeight = 1080f;

    public static float Factor => Mathf.Max(0.1f, Screen.height / ReferenceHeight);

    // Virtual screen size in reference coordinates.
    public static float Width => Screen.width / Factor;
    public static float Height => ReferenceHeight;

    public static void Begin()
    {
        GUI.matrix = Matrix4x4.Scale(new Vector3(Factor, Factor, 1f));
    }
}
