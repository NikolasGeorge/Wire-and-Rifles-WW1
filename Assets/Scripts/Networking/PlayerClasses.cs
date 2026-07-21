using System;

public enum PlayerClass : byte
{
    Assault = 0,
    Medic = 1,
    Support = 2,
    Scout = 3,
    Engineer = 4,
    Officer = 5
}

// Loadout entries are named placeholders until grenade/equipment systems
// exist; the strings drive the class-select UI and reserve the design:
// 1 weapon slot, 2 equipment slots, 1 grenade per class.
[Serializable]
public struct PlayerClassDefinition
{
    public string displayName;
    public string description;
    public float maxHealth;
    public int reserveAmmo;
    public float moveSpeedMultiplier;

    // Role abilities. canRevive: Medic only — revives restore full health.
    // canSpot: Scout only — flare gun spotting (system not built yet).
    // buildDigMultiplier: Engineer works fortifications faster (system not
    // built yet).
    public bool canRevive;
    public bool canSpot;
    public float buildDigMultiplier;

    public string weapon;

    // Selectable primaries; index 0 is the default. Only Assault has more
    // than one.
    public WeaponId[] weaponOptions;

    public string grenade;
    public string equipmentSlot1;
    public string equipmentSlot2;

    // Assault only: may swap its grenade and equipment for any other's.
    public bool customizableLoadout;
}

public static class PlayerClasses
{
    public static readonly PlayerClassDefinition[] Definitions =
    {
        new PlayerClassDefinition
        {
            displayName = "Assault",
            description = "Frontline fighter. Customizable loadout.",
            maxHealth = 100f,
            reserveAmmo = 300,
            moveSpeedMultiplier = 1f,
            canRevive = false,
            canSpot = false,
            buildDigMultiplier = 1f,
            weapon = "Semi-Auto / Bolt-Action / Shotgun",
            weaponOptions = new[] { WeaponId.SemiAutoRifle, WeaponId.BoltAction, WeaponId.Shotgun },
            grenade = "Frag Grenade",
            equipmentSlot1 = "Ammo Pouch",
            equipmentSlot2 = "Bandages",
            customizableLoadout = true
        },
        new PlayerClassDefinition
        {
            displayName = "Medic",
            description = "The only class that can revive. Restores full health.",
            maxHealth = 100f,
            reserveAmmo = 300,
            moveSpeedMultiplier = 1f,
            canRevive = true,
            canSpot = false,
            buildDigMultiplier = 1f,
            weapon = "Service Pistol",
            weaponOptions = new[] { WeaponId.Pistol },
            grenade = "Smoke Grenade",
            equipmentSlot1 = "Medical Kit",
            equipmentSlot2 = "Bandages",
            customizableLoadout = false
        },
        new PlayerClassDefinition
        {
            displayName = "Support",
            description = "Ammunition bearer.",
            maxHealth = 100f,
            reserveAmmo = 300,
            moveSpeedMultiplier = 1f,
            canRevive = false,
            canSpot = false,
            buildDigMultiplier = 1f,
            weapon = "LMG (deployed fire)",
            weaponOptions = new[] { WeaponId.Lmg },
            grenade = "Stick Grenade",
            equipmentSlot1 = "Ammo Crate",
            equipmentSlot2 = "Trench Shovel",
            customizableLoadout = false
        },
        new PlayerClassDefinition
        {
            displayName = "Scout",
            description = "Recon. The only class that can spot enemies.",
            maxHealth = 100f,
            reserveAmmo = 300,
            moveSpeedMultiplier = 1f,
            canRevive = false,
            canSpot = true,
            buildDigMultiplier = 1f,
            weapon = "Scoped Bolt-Action (6x)",
            weaponOptions = new[] { WeaponId.ScopedBoltAction },
            grenade = "Flare Grenade",
            equipmentSlot1 = "Flare Gun (spots enemies)",
            equipmentSlot2 = "Binoculars",
            customizableLoadout = false
        },
        new PlayerClassDefinition
        {
            displayName = "Engineer",
            description = "Places blueprints and builds at 2x speed.",
            maxHealth = 100f,
            reserveAmmo = 300,
            moveSpeedMultiplier = 1f,
            canRevive = false,
            canSpot = false,
            buildDigMultiplier = 2f,
            weapon = "Bolt-Action Rifle",
            weaponOptions = new[] { WeaponId.BoltAction },
            grenade = "Incendiary Grenade",
            equipmentSlot1 = "Wirecutters",
            equipmentSlot2 = "Repair Tool",
            customizableLoadout = false
        },
        new PlayerClassDefinition
        {
            displayName = "Officer",
            description = "Leads the charge.",
            maxHealth = 100f,
            reserveAmmo = 300,
            moveSpeedMultiplier = 1f,
            canRevive = false,
            canSpot = false,
            buildDigMultiplier = 1f,
            weapon = "Service Pistol",
            weaponOptions = new[] { WeaponId.Pistol },
            grenade = "Smoke Grenade",
            equipmentSlot1 = "Command Whistle",
            equipmentSlot2 = "Field Map",
            customizableLoadout = false
        }
    };

    public static PlayerClassDefinition Get(PlayerClass playerClass)
    {
        int index = (int)playerClass;

        if (index < 0 || index >= Definitions.Length)
        {
            return Definitions[0];
        }

        return Definitions[index];
    }
}
