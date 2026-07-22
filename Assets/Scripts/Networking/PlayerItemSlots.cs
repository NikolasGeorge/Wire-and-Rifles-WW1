using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

// Owner-side item slots for a uniform FPS feel:
//   1 — primary weapon (rifle)
//   2 — grenade (LMB throws)
//   3 — Shovel: universal melee/dig tool, EVERY class gets this regardless
//       of loadout. LMB swings (melee hit + digs if it lands on open
//       ground); RMB holds to fill dirt back in.
//   4 — class equipment (setup.AssignedEquipment1): Medic's Medical Kit,
//       Support's Ammo Crate, everyone else defaults to the Axe — a
//       melee-only tool with no dig/fill, built to wreck buildables.
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
            // Dying mid-cook has to clear the latch, or the next life would
            // start believing a pin was already pulled.
            cooking = false;

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

        // Clicks belong to the build menu while it is up, and a grenade whose
        // pin is already out cannot be stowed.
        if (FortificationBuilder.MenuOpen)
        {
            return;
        }

        if (!cooking)
        {
            if (GameSettings.Pressed(GameAction.Slot1)) SwitchTo(1);
            if (GameSettings.Pressed(GameAction.Slot2)) SwitchTo(2);
            if (GameSettings.Pressed(GameAction.Slot3)) SwitchTo(3);
            if (GameSettings.Pressed(GameAction.Slot4)) SwitchTo(4);
        }

        if (switchRemaining <= 0f && activeSlot == 2)
        {
            HandleGrenadeInput();
        }
        else if (switchRemaining <= 0f && Mouse.current.leftButton.wasPressedThisFrame)
        {
            UseActiveItem();
        }

        // Shovel (slot 3): RMB holds to fill dirt back in. LMB's swing+dig
        // is handled by UseActiveItem() above, on press rather than hold, so
        // it reads as an attack rather than a continuous hose-down.
        if (switchRemaining <= 0f && activeSlot == 3 && builder != null && Mouse.current.rightButton.isPressed)
        {
            builder.TryDigScoop(true);
        }
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
            case 3:
                PerformMeleeSwing(DamageType.Shovel);
                break;

            case 4:
                if (setup.AssignedEquipment1 == EquipmentType.Axe)
                {
                    PerformMeleeSwing(DamageType.Axe);
                }
                else
                {
                    UseEquipment(setup.AssignedEquipment1);
                }

                break;
        }
    }

    // ---- Grenades: hold to pull the pin, release to throw ----
    // One hold does two jobs at once — it winds up the throw AND burns the
    // fuse. Holding longer throws further but leaves less fuse, and holding
    // too long kills you, which is the whole tension of cooking one.

    [Header("Grenade Throw")]
    [Tooltip("Seconds of hold to reach a full-power throw. The fuse keeps burning well past this.")]
    public float grenadeChargeTime = 0.9f;

    public float minThrowSpeed = 9f;
    public float maxThrowSpeed = 24f;
    public float minThrowLift = 2f;
    public float maxThrowLift = 4.5f;

    private bool cooking;
    private float cookStartTime;

    private float CookedSeconds => cooking ? Time.time - cookStartTime : 0f;
    private float Charge01 => Mathf.Clamp01(CookedSeconds / Mathf.Max(0.05f, grenadeChargeTime));

    private void HandleGrenadeInput()
    {
        if (setup == null)
        {
            return;
        }

        if (!cooking)
        {
            if (Mouse.current.leftButton.wasPressedThisFrame && setup.GrenadesLeft > 0)
            {
                cooking = true;
                cookStartTime = Time.time;
                setup.RequestBeginCook();

                // The pin is the player's only cue the fuse has started.
                ProceduralAudio.PlayAt(ProceduralAudio.PinPull, playerCamera != null
                    ? playerCamera.transform.position
                    : transform.position, 0.7f);
            }

            return;
        }

        // The server owns the fuse and will have detonated it in-hand by now;
        // just stop tracking locally.
        if (CookedSeconds >= GrenadeArc.FuseSeconds)
        {
            cooking = false;
            return;
        }

        if (Mouse.current.leftButton.wasReleasedThisFrame)
        {
            ThrowGrenade(Charge01);
            cooking = false;
        }
    }

    private void ThrowGrenade(float charge01)
    {
        if (playerCamera == null)
        {
            return;
        }

        Transform view = playerCamera.transform;
        Vector3 origin = view.position + view.forward * 0.4f;
        Vector3 velocity = view.forward * Mathf.Lerp(minThrowSpeed, maxThrowSpeed, charge01)
            + Vector3.up * Mathf.Lerp(minThrowLift, maxThrowLift, charge01);

        setup.RequestThrowGrenade(origin, velocity);
    }

    private void UseEquipment(EquipmentType equipment)
    {
        // Scout's flare gun: fires a flare that arcs up and drifts down,
        // revealing enemies underneath it the whole way.
        if (equipment == EquipmentType.FlareGun)
        {
            setup.TryFireFlare();
            return;
        }

        // Deployables (ammo box, med kit, toolbox) are thrown like grenades
        // and land as active AOE boxes; other equipment is passive for now.
        FortificationType? crateType = PlayerNetworkSetup.CrateForEquipment(equipment);

        if (crateType != null && playerCamera != null)
        {
            Vector3 origin = playerCamera.transform.position + playerCamera.transform.forward * 0.4f;
            Vector3 velocity = playerCamera.transform.forward * 12f + Vector3.up * 2.5f;
            setup.RequestThrowSupplyCrate(origin, velocity, crateType.Value);
        }
    }

    // Local raycast for a melee swing: resolves whether it lands on a
    // player, a structure, or open ground, then hands the result to the
    // server for validation. A landing shovel swing on bare terrain also
    // triggers one dig scoop (TryDigScoop paces/validates itself, so this
    // never double-digs or digs through a structure).
    private void PerformMeleeSwing(DamageType damageType)
    {
        if (setup == null || playerCamera == null)
        {
            return;
        }

        float range = damageType == DamageType.Axe ? setup.axeMeleeRange : setup.shovelMeleeRange;
        Vector3 origin = playerCamera.transform.position;
        Ray ray = playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        Vector3 hitPoint = origin + ray.direction * range;

        NetworkObject targetObject = null;
        int structureId = -1;

        // Played locally off the client's own raycast so the swing lands on
        // the ear at the same instant it lands on screen; waiting for the
        // server to confirm would make every hit feel late.
        ProceduralAudio.PlayAt(ProceduralAudio.Swing, origin, 0.45f);

        if (Physics.Raycast(ray, out RaycastHit hit, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            hitPoint = hit.point;

            FortificationStructure structure = hit.collider.GetComponentInParent<FortificationStructure>();

            if (structure != null)
            {
                structureId = structure.id;
                ProceduralAudio.PlayAt(ProceduralAudio.MeleeHard, hitPoint, 0.7f);
            }
            else
            {
                // Same resolution convention as the rifle's hitscan: hitbox
                // colliders sit somewhere under the player's NetworkObject.
                NetworkObject hitNetworkObject = hit.collider.GetComponentInParent<NetworkObject>();

                if (hitNetworkObject != null && hitNetworkObject != setup.NetworkObject)
                {
                    targetObject = hitNetworkObject;
                    ProceduralAudio.PlayAt(ProceduralAudio.MeleeFlesh, hitPoint, 0.8f);
                }
                else if (damageType == DamageType.Shovel && builder != null
                    && hit.collider.GetComponent<TerrainCollider>() != null)
                {
                    builder.TryDigScoop(false);
                }
                else
                {
                    ProceduralAudio.PlayAt(ProceduralAudio.MeleeHard, hitPoint, 0.5f);
                }
            }
        }

        setup.RequestMeleeAttack(origin, hitPoint, targetObject, structureId, damageType);
    }

    // Throw power plus what is left of the fuse. The fuse bar runs red as it
    // empties — cooking blind would just be a coin flip.
    private void DrawGrenadeChargeBar()
    {
        if (!cooking)
        {
            return;
        }

        float barWidth = 220f;
        float barHeight = 10f;
        float x = (Screen.width - barWidth) * 0.5f;
        float y = Screen.height * 0.66f;

        float fuseLeft01 = Mathf.Clamp01(1f - CookedSeconds / GrenadeArc.FuseSeconds);

        GUIStyle labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        labelStyle.normal.textColor = Color.white;

        GUI.Label(new Rect(0f, y - 20f, Screen.width, 18f),
            "THROW POWER   —   FUSE " + (GrenadeArc.FuseSeconds - CookedSeconds).ToString("0.0") + "s", labelStyle);

        // Power.
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, barWidth + 4f, barHeight + 4f), Texture2D.whiteTexture);
        GUI.color = new Color(0.55f, 0.85f, 1f, 0.9f);
        GUI.DrawTexture(new Rect(x, y, barWidth * Charge01, barHeight), Texture2D.whiteTexture);

        // Fuse, directly beneath, draining the other way.
        float fuseY = y + barHeight + 5f;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(x - 2f, fuseY - 2f, barWidth + 4f, barHeight + 4f), Texture2D.whiteTexture);
        GUI.color = Color.Lerp(new Color(1f, 0.2f, 0.15f, 0.95f), new Color(1f, 0.85f, 0.3f, 0.9f), fuseLeft01);
        GUI.DrawTexture(new Rect(x, fuseY, barWidth * fuseLeft01, barHeight), Texture2D.whiteTexture);

        GUI.color = Color.white;
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
            "3  SHOVEL",
            "4  " + LoadoutData.GetEquipmentName(setup.AssignedEquipment1).ToUpper()
        };

        DrawGrenadeChargeBar();

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
