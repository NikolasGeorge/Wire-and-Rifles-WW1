using UnityEngine;
using UnityEngine.InputSystem;

// Owner-side item slots for a uniform FPS feel:
//   1 — primary weapon (rifle)
//   2 — grenade (LMB throws)
//   3 — equipment slot 1 (LMB uses it if it has an active use)
//   4 — equipment slot 2
// Switching takes a short raise time during which nothing can be used. The
// rifle only fires, aims, and reloads while slot 1 is active; other slots
// show a simple held placeholder in hand.
public class PlayerItemSlots : MonoBehaviour
{
    public float switchTime = 0.35f;

    private PlayerNetworkSetup setup;
    private PlayerNetworkHealth health;
    private BoltActionRifle rifle;
    private FortificationBuilder builder;
    private Camera playerCamera;

    private int activeSlot = 1;
    private float switchRemaining;
    private GameObject heldPlaceholder;

    public int ActiveSlot => activeSlot;
    public bool RifleActive => activeSlot == 1 && switchRemaining <= 0f;

    private void Start()
    {
        setup = GetComponent<PlayerNetworkSetup>();
        health = GetComponent<PlayerNetworkHealth>();
        builder = GetComponent<FortificationBuilder>();
        rifle = GetComponentInChildren<BoltActionRifle>(true);
        playerCamera = GetComponentInChildren<Camera>(true);
    }

    private void OnDestroy()
    {
        if (heldPlaceholder != null)
        {
            Destroy(heldPlaceholder);
        }
    }

    private void Update()
    {
        if (Keyboard.current == null || Mouse.current == null || PauseMenu.IsOpen)
        {
            return;
        }

        if (health != null && health.State != PlayerLifeState.Alive)
        {
            if (activeSlot != 1)
            {
                SwitchTo(1);
                switchRemaining = 0f;
            }

            return;
        }

        if (switchRemaining > 0f)
        {
            switchRemaining -= Time.deltaTime;
        }

        if (GameSettings.Pressed(GameAction.Slot1)) SwitchTo(1);
        if (GameSettings.Pressed(GameAction.Slot2)) SwitchTo(2);
        if (GameSettings.Pressed(GameAction.Slot3)) SwitchTo(3);
        if (GameSettings.Pressed(GameAction.Slot4)) SwitchTo(4);

        if (switchRemaining <= 0f && Mouse.current.leftButton.wasPressedThisFrame)
        {
            UseActiveItem();
        }

        // Trench shovel held: LMB digs, RMB fills, continuously while held.
        if (switchRemaining <= 0f && builder != null && ActiveEquipment() == EquipmentType.TrenchShovel)
        {
            if (Mouse.current.leftButton.isPressed)
            {
                builder.TryDigScoop(false);
            }
            else if (Mouse.current.rightButton.isPressed)
            {
                builder.TryDigScoop(true);
            }
        }
    }

    // The equipment in the active slot, or null-ish sentinel when a
    // non-equipment slot is active.
    private EquipmentType? ActiveEquipment()
    {
        if (setup == null)
        {
            return null;
        }

        if (activeSlot == 3)
        {
            return setup.AssignedEquipment1;
        }

        if (activeSlot == 4)
        {
            return setup.AssignedEquipment2;
        }

        return null;
    }

    private void SwitchTo(int slot)
    {
        if (slot == activeSlot)
        {
            return;
        }

        activeSlot = slot;
        switchRemaining = switchTime;

        // Leaving the rifle: cleanly drop ADS, then disable the weapon
        // (its OnDisable resets the move-speed penalty too).
        if (rifle != null)
        {
            bool rifleSlot = slot == 1;

            if (!rifleSlot)
            {
                rifle.ForceStopAiming();
            }

            rifle.enabled = rifleSlot;

            // Only the weapon model — scanning from the player root would
            // hide the soldier body too.
            if (rifle.weaponHolder != null)
            {
                foreach (Renderer weaponRenderer in rifle.weaponHolder.GetComponentsInChildren<Renderer>(true))
                {
                    weaponRenderer.enabled = rifleSlot;
                }

                // Re-hide the default rifle if a swapped view model (LMG,
                // pistol) is in use.
                if (rifleSlot && setup != null)
                {
                    setup.RefreshWeaponViewModel();
                }
            }
        }

        UpdateHeldPlaceholder();
    }

    // Simple in-hand stand-in for grenades/gadgets until real view models
    // exist.
    private void UpdateHeldPlaceholder()
    {
        if (heldPlaceholder != null)
        {
            Destroy(heldPlaceholder);
            heldPlaceholder = null;
        }

        if (activeSlot == 1 || playerCamera == null)
        {
            return;
        }

        bool grenade = activeSlot == 2;

        // Real grenade model in hand when available.
        if (grenade && setup != null)
        {
            GrenadeVisuals visuals = GrenadeVisuals.Load();
            GameObject model = visuals != null ? visuals.GetGrenadeModel(setup.AssignedGrenade) : null;

            if (model != null)
            {
                heldPlaceholder = Instantiate(model, playerCamera.transform);
                heldPlaceholder.name = "HeldItemPlaceholder";
                heldPlaceholder.transform.localPosition = new Vector3(0.28f, -0.24f, 0.55f);
                heldPlaceholder.transform.localRotation = Quaternion.Euler(-15f, 30f, 0f);

                foreach (Collider heldCollider in heldPlaceholder.GetComponentsInChildren<Collider>(true))
                {
                    Destroy(heldCollider);
                }

                return;
            }
        }

        heldPlaceholder = GameObject.CreatePrimitive(grenade ? PrimitiveType.Sphere : PrimitiveType.Cube);
        heldPlaceholder.name = "HeldItemPlaceholder";
        Destroy(heldPlaceholder.GetComponent<Collider>());

        heldPlaceholder.transform.SetParent(playerCamera.transform, false);
        heldPlaceholder.transform.localPosition = new Vector3(0.28f, -0.24f, 0.55f);
        heldPlaceholder.transform.localScale = grenade
            ? Vector3.one * 0.14f
            : new Vector3(0.16f, 0.12f, 0.1f);

        heldPlaceholder.GetComponent<Renderer>().material.color = grenade
            ? new Color(0.16f, 0.2f, 0.14f)
            : new Color(0.45f, 0.35f, 0.22f);
    }

    private void UseActiveItem()
    {
        if (setup == null)
        {
            return;
        }

        switch (activeSlot)
        {
            case 2:
                if (setup.GrenadesLeft > 0 && playerCamera != null)
                {
                    Vector3 origin = playerCamera.transform.position + playerCamera.transform.forward * 0.4f;
                    Vector3 velocity = playerCamera.transform.forward * 16f + Vector3.up * 3f;
                    setup.RequestThrowGrenade(origin, velocity);
                }

                break;

            case 3:
                UseEquipment(setup.AssignedEquipment1);
                break;

            case 4:
                UseEquipment(setup.AssignedEquipment2);
                break;
        }
    }

    private void UseEquipment(EquipmentType equipment)
    {
        // Supply crates are thrown like grenades and land as active AOE
        // boxes; other equipment is passive for now.
        bool ammo = equipment == EquipmentType.AmmoCrate;

        if ((ammo || equipment == EquipmentType.MedicalKit) && playerCamera != null)
        {
            Vector3 origin = playerCamera.transform.position + playerCamera.transform.forward * 0.4f;
            Vector3 velocity = playerCamera.transform.forward * 12f + Vector3.up * 2.5f;
            setup.RequestThrowSupplyCrate(origin, velocity, ammo);
        }
    }

    // ---- HUD: slot list bottom-right ----

    private void OnGUI()
    {
        if (setup == null || (health != null && health.State != PlayerLifeState.Alive))
        {
            return;
        }

        string[] labels =
        {
            "1  RIFLE",
            "2  " + LoadoutData.GetGrenadeName(setup.AssignedGrenade).ToUpper() + "  x" + setup.GrenadesLeft,
            "3  " + LoadoutData.GetEquipmentName(setup.AssignedEquipment1).ToUpper(),
            "4  " + LoadoutData.GetEquipmentName(setup.AssignedEquipment2).ToUpper()
        };

        const float rowHeight = 20f;
        float x = Screen.width - 250f;
        float y = Screen.height - 30f - labels.Length * rowHeight;

        for (int i = 0; i < labels.Length; i++)
        {
            bool isActive = activeSlot == i + 1;

            GUIStyle style = new GUIStyle(GUI.skin.label)
            {
                fontSize = isActive ? 14 : 12,
                fontStyle = isActive ? FontStyle.Bold : FontStyle.Normal,
                alignment = TextAnchor.MiddleRight
            };

            style.normal.textColor = isActive
                ? Color.white
                : new Color(1f, 1f, 1f, 0.45f);

            string text = isActive && switchRemaining > 0f ? labels[i] + "  ..." : labels[i];
            GUI.Label(new Rect(x, y + i * rowHeight, 230f, rowHeight), text, style);
        }
    }
}
