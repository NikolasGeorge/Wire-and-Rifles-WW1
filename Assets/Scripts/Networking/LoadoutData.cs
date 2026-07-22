using System.Collections.Generic;
using UnityEngine;

public enum GrenadeType : byte
{
    Frag = 0,
    Smoke = 1,
    Stick = 2,
    Flare = 3,
    Incendiary = 4
}

public enum EquipmentType : byte
{
    AmmoPouch = 0,
    Bandages = 1,
    MedicalKit = 2,
    AmmoCrate = 3,
    // Obsolete: digging is now a universal melee-shovel ability (slot 3, see
    // PlayerItemSlots), not a class-selectable equipment. Value kept so old
    // saved/serialized loadouts don't shift other entries.
    TrenchShovel = 4,
    FlareGun = 5,
    Binoculars = 6,
    Wirecutters = 7,
    RepairTool = 8,
    CommandWhistle = 9,
    FieldMap = 10,
    Axe = 11,
    Toolbox = 12
}

// A player's chosen loadout for one class: weapon slot + grenade + two
// equipment slots. Grenade/equipment selection is only open for Assault
// (customizable loadout); every other class is locked to its identity kit.
public struct LoadoutSelection
{
    public int weaponIndex;
    public GrenadeType grenade;
    public EquipmentType equipment1;
    public EquipmentType equipment2;
}

public static class LoadoutData
{
    // Remembered per class for this play session, so re-deploying keeps your
    // choices.
    private static readonly Dictionary<PlayerClass, LoadoutSelection> saved =
        new Dictionary<PlayerClass, LoadoutSelection>();

    // Assault's selectable pools.
    public static readonly GrenadeType[] AssaultGrenadePool =
    {
        GrenadeType.Frag, GrenadeType.Smoke, GrenadeType.Stick, GrenadeType.Incendiary
    };

    public static readonly EquipmentType[] AssaultEquipmentPool =
    {
        EquipmentType.AmmoPouch, EquipmentType.Bandages, EquipmentType.Axe,
        EquipmentType.Binoculars, EquipmentType.Wirecutters
    };

    // The Engineer's kit is otherwise fixed, but its slot-4 tool is a real
    // choice: area support (Toolbox) versus demolition (Axe).
    public static readonly EquipmentType[] EngineerEquipmentPool =
    {
        EquipmentType.Toolbox, EquipmentType.Axe, EquipmentType.Wirecutters
    };

    // The equipment a class may choose from for slot 4, or null when its kit
    // is fully locked.
    public static EquipmentType[] GetEquipmentPool(PlayerClass playerClass)
    {
        switch (playerClass)
        {
            case PlayerClass.Assault: return AssaultEquipmentPool;
            case PlayerClass.Engineer: return EngineerEquipmentPool;
            default: return null;
        }
    }

    public static string GetGrenadeName(GrenadeType grenade)
    {
        switch (grenade)
        {
            case GrenadeType.Smoke: return "Smoke Grenade";
            case GrenadeType.Stick: return "Stick Grenade";
            case GrenadeType.Flare: return "Flare Grenade";
            case GrenadeType.Incendiary: return "Incendiary Grenade";
            default: return "Frag Grenade";
        }
    }

    public static string GetEquipmentName(EquipmentType equipment)
    {
        switch (equipment)
        {
            case EquipmentType.Bandages: return "Bandages";
            case EquipmentType.MedicalKit: return "Medical Kit";
            case EquipmentType.AmmoCrate: return "Ammo Crate";
            case EquipmentType.TrenchShovel: return "Trench Shovel";
            case EquipmentType.FlareGun: return "Flare Gun";
            case EquipmentType.Binoculars: return "Binoculars";
            case EquipmentType.Wirecutters: return "Wirecutters";
            case EquipmentType.RepairTool: return "Repair Tool";
            case EquipmentType.CommandWhistle: return "Command Whistle";
            case EquipmentType.FieldMap: return "Field Map";
            case EquipmentType.Axe: return "Axe";
            case EquipmentType.Toolbox: return "Toolbox";
            default: return "Ammo Pouch";
        }
    }

    // Fixed identity loadout for each class (Assault's is just the default).
    //
    // Equipment1 is the class's slot-4 item (see PlayerItemSlots). Slot 3 is
    // ALWAYS the universal shovel regardless of class, so no kit needs to
    // carry a digging item — equipment2 is otherwise unused in-game.
    //
    // The Axe is deliberately NOT universal: only the Engineer carries one by
    // default, and only Assault can choose one (it is in the pool below).
    public static LoadoutSelection GetDefault(PlayerClass playerClass)
    {
        switch (playerClass)
        {
            case PlayerClass.Medic:
                return Make(GrenadeType.Smoke, EquipmentType.MedicalKit, EquipmentType.Bandages);
            case PlayerClass.Support:
                return Make(GrenadeType.Stick, EquipmentType.AmmoCrate, EquipmentType.Bandages);
            case PlayerClass.Scout:
                return Make(GrenadeType.Flare, EquipmentType.FlareGun, EquipmentType.Binoculars);
            case PlayerClass.Engineer:
                return Make(GrenadeType.Incendiary, EquipmentType.Toolbox, EquipmentType.Wirecutters);
            case PlayerClass.Officer:
                return Make(GrenadeType.Smoke, EquipmentType.CommandWhistle, EquipmentType.FieldMap);
            default:
                return Make(GrenadeType.Frag, EquipmentType.AmmoPouch, EquipmentType.Bandages);
        }
    }

    private static LoadoutSelection Make(GrenadeType grenade, EquipmentType equipment1, EquipmentType equipment2)
    {
        return new LoadoutSelection
        {
            weaponIndex = 0,
            grenade = grenade,
            equipment1 = equipment1,
            equipment2 = equipment2
        };
    }

    public static LoadoutSelection Get(PlayerClass playerClass)
    {
        if (saved.TryGetValue(playerClass, out LoadoutSelection selection))
        {
            return selection;
        }

        return GetDefault(playerClass);
    }

    public static void Set(PlayerClass playerClass, LoadoutSelection selection)
    {
        saved[playerClass] = selection;
    }

    // Server-side check that a requested loadout is legal for the class:
    // non-Assault classes are forced back to their identity kit; Assault
    // choices must come from the pools.
    public static LoadoutSelection Sanitize(PlayerClass playerClass, LoadoutSelection requested)
    {
        EquipmentType[] pool = GetEquipmentPool(playerClass);

        if (!PlayerClasses.Get(playerClass).customizableLoadout)
        {
            LoadoutSelection fixedLoadout = GetDefault(playerClass);
            fixedLoadout.weaponIndex = requested.weaponIndex;

            // A locked class may still swap its slot-4 tool if it has a pool
            // (Engineer: Toolbox / Axe / Wirecutters). Everything else in the
            // kit stays fixed.
            if (pool != null && System.Array.IndexOf(pool, requested.equipment1) >= 0)
            {
                fixedLoadout.equipment1 = requested.equipment1;
            }

            return fixedLoadout;
        }

        LoadoutSelection result = requested;

        if (System.Array.IndexOf(AssaultGrenadePool, result.grenade) < 0)
        {
            result.grenade = GrenadeType.Frag;
        }

        if (System.Array.IndexOf(AssaultEquipmentPool, result.equipment1) < 0)
        {
            result.equipment1 = EquipmentType.AmmoPouch;
        }

        if (System.Array.IndexOf(AssaultEquipmentPool, result.equipment2) < 0
            || result.equipment2 == result.equipment1)
        {
            result.equipment2 = result.equipment1 == EquipmentType.Bandages
                ? EquipmentType.AmmoPouch
                : EquipmentType.Bandages;
        }

        return result;
    }
}
