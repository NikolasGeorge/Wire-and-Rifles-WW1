using System;
using System.Linq;
using NUnit.Framework;

namespace WireAndRifles.Tests
{
    /// <summary>
    /// The class table is the game's balance data. Nothing validates it at
    /// runtime — a class with no weapon or a duplicated role ability compiles
    /// and ships, and only turns up when someone spawns as it.
    /// </summary>
    public class PlayerClassesTests
    {
        private static PlayerClass[] AllClasses =>
            (PlayerClass[])Enum.GetValues(typeof(PlayerClass));

        [Test]
        public void EveryEnumMemberHasADefinition()
        {
            // Get() falls back to Definitions[0] when the index is out of
            // range, so a class added to the enum but not to the table would
            // silently spawn everyone as Assault.
            Assert.That(PlayerClasses.Definitions, Has.Length.EqualTo(AllClasses.Length));
        }

        [Test]
        public void GetReturnsTheDefinitionMatchingTheEnumOrder()
        {
            foreach (var pc in AllClasses)
            {
                Assert.That(PlayerClasses.Get(pc).displayName,
                    Is.EqualTo(PlayerClasses.Definitions[(int)pc].displayName),
                    $"{pc} resolves to the wrong definition");
            }
        }

        [Test]
        public void GetFallsBackToTheFirstClassForAnUnknownValue()
        {
            // A malicious or stale client can send any byte; the server must
            // not index past the end of the table.
            Assert.That(PlayerClasses.Get((PlayerClass)200).displayName,
                Is.EqualTo(PlayerClasses.Definitions[0].displayName));
        }

        [Test]
        public void EveryClassHasNameDescriptionAndWeapon()
        {
            foreach (var pc in AllClasses)
            {
                var d = PlayerClasses.Get(pc);
                Assert.Multiple(() =>
                {
                    Assert.That(d.displayName, Is.Not.Null.And.Not.Empty, $"{pc} name");
                    Assert.That(d.description, Is.Not.Null.And.Not.Empty, $"{pc} description");
                    Assert.That(d.weapon, Is.Not.Null.And.Not.Empty, $"{pc} weapon");
                });
            }
        }

        [Test]
        public void EveryClassHasAtLeastOneSelectablePrimary()
        {
            // weaponOptions[0] is the default the spawn code reads. An empty
            // array is an index-out-of-range the moment that class spawns.
            foreach (var pc in AllClasses)
            {
                var d = PlayerClasses.Get(pc);
                Assert.That(d.weaponOptions, Is.Not.Null.And.Not.Empty,
                    $"{pc} has no selectable primary");
            }
        }

        [Test]
        public void WeaponOptionsAreAllRealWeapons()
        {
            foreach (var pc in AllClasses)
            {
                foreach (var w in PlayerClasses.Get(pc).weaponOptions)
                {
                    Assert.That(Enum.IsDefined(typeof(WeaponId), w), Is.True,
                        $"{pc} lists an undefined WeaponId");
                }
            }
        }

        [Test]
        public void NoClassListsTheSamePrimaryTwice()
        {
            foreach (var pc in AllClasses)
            {
                var opts = PlayerClasses.Get(pc).weaponOptions;
                Assert.That(opts.Distinct().Count(), Is.EqualTo(opts.Length),
                    $"{pc} lists a duplicate primary");
            }
        }

        [Test]
        public void EveryClassFillsBothEquipmentSlotsAndAGrenade()
        {
            // The design reserves 1 weapon, 2 equipment slots and 1 grenade
            // per class. A blank slot renders as an empty row in class select.
            foreach (var pc in AllClasses)
            {
                var d = PlayerClasses.Get(pc);
                Assert.Multiple(() =>
                {
                    Assert.That(d.grenade, Is.Not.Null.And.Not.Empty, $"{pc} grenade");
                    Assert.That(d.equipmentSlot1, Is.Not.Null.And.Not.Empty, $"{pc} slot 1");
                    Assert.That(d.equipmentSlot2, Is.Not.Null.And.Not.Empty, $"{pc} slot 2");
                });
            }
        }

        [Test]
        public void MedicIsTheOnlyClassThatCanRevive()
        {
            var revivers = AllClasses.Where(pc => PlayerClasses.Get(pc).canRevive).ToArray();
            Assert.That(revivers, Is.EqualTo(new[] { PlayerClass.Medic }));
        }

        [Test]
        public void ScoutIsTheOnlyClassThatCanSpot()
        {
            var spotters = AllClasses.Where(pc => PlayerClasses.Get(pc).canSpot).ToArray();
            Assert.That(spotters, Is.EqualTo(new[] { PlayerClass.Scout }));
        }

        [Test]
        public void EngineerIsTheOnlyClassThatBuildsFaster()
        {
            var fast = AllClasses
                .Where(pc => PlayerClasses.Get(pc).buildDigMultiplier > 1f)
                .ToArray();
            Assert.That(fast, Is.EqualTo(new[] { PlayerClass.Engineer }));
            Assert.That(PlayerClasses.Get(PlayerClass.Engineer).buildDigMultiplier,
                Is.EqualTo(2f));
        }

        [Test]
        public void AssaultIsTheOnlyClassWithACustomisableLoadout()
        {
            var custom = AllClasses
                .Where(pc => PlayerClasses.Get(pc).customizableLoadout)
                .ToArray();
            Assert.That(custom, Is.EqualTo(new[] { PlayerClass.Assault }));
        }

        [Test]
        public void AssaultIsTheOnlyClassWithAWeaponChoice()
        {
            foreach (var pc in AllClasses)
            {
                var count = PlayerClasses.Get(pc).weaponOptions.Length;
                if (pc == PlayerClass.Assault)
                    Assert.That(count, Is.GreaterThan(1), "Assault should have a choice");
                else
                    Assert.That(count, Is.EqualTo(1), $"{pc} should have exactly one primary");
            }
        }

        [Test]
        public void EveryClassHasPositiveHealthAndAmmo()
        {
            foreach (var pc in AllClasses)
            {
                var d = PlayerClasses.Get(pc);
                Assert.Multiple(() =>
                {
                    Assert.That(d.maxHealth, Is.GreaterThan(0f), $"{pc} health");
                    Assert.That(d.reserveAmmo, Is.GreaterThan(0), $"{pc} ammo");
                });
            }
        }

        [Test]
        public void MultipliersAreNeverZeroOrNegative()
        {
            // These are multiplied into movement, suppression and build
            // speed. A zero reads as "cannot move" or "instant build"
            // rather than as the unset value it usually is.
            foreach (var pc in AllClasses)
            {
                var d = PlayerClasses.Get(pc);
                Assert.Multiple(() =>
                {
                    Assert.That(d.moveSpeedMultiplier, Is.GreaterThan(0f), $"{pc} move speed");
                    Assert.That(d.suppressionMultiplier, Is.GreaterThan(0f), $"{pc} suppression");
                    Assert.That(d.buildDigMultiplier, Is.GreaterThan(0f), $"{pc} build speed");
                });
            }
        }

        [Test]
        public void DisplayNamesAreUnique()
        {
            var names = PlayerClasses.Definitions.Select(d => d.displayName).ToArray();
            Assert.That(names.Distinct().Count(), Is.EqualTo(names.Length));
        }

        [Test]
        public void DisplayNamesMatchTheEnumMemberNames()
        {
            // Class select renders displayName while the netcode sends the
            // enum. If they drift, the UI and the server disagree about what
            // the player picked.
            foreach (var pc in AllClasses)
            {
                Assert.That(PlayerClasses.Get(pc).displayName, Is.EqualTo(pc.ToString()));
            }
        }
    }
}
