using System;
using NUnit.Framework;

namespace WireAndRifles.Tests
{
    /// <summary>
    /// Weapon stat blocks. These numbers drive server-side damage and fire-rate
    /// validation, so a malformed profile is not just a balance problem — a
    /// zero fireInterval or a negative clip size changes what the server will
    /// accept from a client.
    /// </summary>
    public class WeaponProfilesTests
    {
        private static WeaponId[] AllWeapons => (WeaponId[])Enum.GetValues(typeof(WeaponId));

        [Test]
        public void EveryWeaponResolvesToANamedProfile()
        {
            foreach (var id in AllWeapons)
            {
                Assert.That(WeaponProfiles.Get(id).displayName, Is.Not.Null.And.Not.Empty,
                    $"{id} has no profile");
            }
        }

        [Test]
        public void EveryWeaponHasADistinctName()
        {
            var names = Array.ConvertAll(AllWeapons, id => WeaponProfiles.Get(id).displayName);
            Assert.That(new System.Collections.Generic.HashSet<string>(names),
                Has.Count.EqualTo(names.Length));
        }

        [Test]
        public void AnUnknownWeaponFallsBackToTheBoltAction()
        {
            // BoltAction is the default arm of the switch, so an unrecognised
            // id yields the standard rifle rather than a zeroed struct. A
            // zeroed struct would mean no damage, no clip, and a zero fire
            // interval the server reads as "unlimited rate of fire".
            var fallback = WeaponProfiles.Get((WeaponId)200);
            Assert.That(fallback.displayName,
                Is.EqualTo(WeaponProfiles.Get(WeaponId.BoltAction).displayName));
            Assert.That(fallback.clipSize, Is.GreaterThan(0));
        }

        [Test]
        public void DamageNeverIncreasesWithDistance()
        {
            // Falloff, not falloff-in-reverse. A mid higher than close means a
            // weapon that rewards backing away.
            foreach (var id in AllWeapons)
            {
                var p = WeaponProfiles.Get(id);
                Assert.Multiple(() =>
                {
                    Assert.That(p.damageClose, Is.GreaterThanOrEqualTo(p.damageMid),
                        $"{id}: close < mid");
                    Assert.That(p.damageMid, Is.GreaterThanOrEqualTo(p.damageLong),
                        $"{id}: mid < long");
                });
            }
        }

        [Test]
        public void RangeBandsAreOrdered()
        {
            // close is 0..closeRangeEnd, mid runs to midRangeEnd, long is the
            // rest. Bands out of order make the mid band unreachable.
            foreach (var id in AllWeapons)
            {
                var p = WeaponProfiles.Get(id);
                Assert.Multiple(() =>
                {
                    Assert.That(p.closeRangeEnd, Is.GreaterThan(0f), $"{id}: close band");
                    Assert.That(p.midRangeEnd, Is.GreaterThan(p.closeRangeEnd),
                        $"{id}: mid band does not extend past close");
                    Assert.That(p.range, Is.GreaterThanOrEqualTo(p.midRangeEnd),
                        $"{id}: max range falls inside the mid band");
                });
            }
        }

        [Test]
        public void EveryWeaponDealsDamage()
        {
            foreach (var id in AllWeapons)
            {
                Assert.That(WeaponProfiles.Get(id).damageLong, Is.GreaterThan(0f),
                    $"{id} deals no damage at range");
            }
        }

        [Test]
        public void AmmoAndTimingValuesArePositive()
        {
            foreach (var id in AllWeapons)
            {
                var p = WeaponProfiles.Get(id);
                Assert.Multiple(() =>
                {
                    Assert.That(p.clipSize, Is.GreaterThan(0), $"{id} clip");
                    Assert.That(p.reserveAmmo, Is.GreaterThan(0), $"{id} reserve");
                    Assert.That(p.fireInterval, Is.GreaterThan(0f), $"{id} fire interval");
                    Assert.That(p.reloadTime, Is.GreaterThan(0f), $"{id} reload");
                    Assert.That(p.muzzleVelocity, Is.GreaterThan(0f), $"{id} muzzle velocity");
                });
            }
        }

        [Test]
        public void EveryWeaponFiresAtLeastOneProjectile()
        {
            foreach (var id in AllWeapons)
            {
                Assert.That(WeaponProfiles.Get(id).pelletsPerShot, Is.GreaterThanOrEqualTo(1),
                    $"{id} fires nothing");
            }
        }

        [Test]
        public void OnlyTheShotgunFiresMultiplePellets()
        {
            foreach (var id in AllWeapons)
            {
                var pellets = WeaponProfiles.Get(id).pelletsPerShot;
                if (id == WeaponId.Shotgun)
                    Assert.That(pellets, Is.GreaterThan(1));
                else
                    Assert.That(pellets, Is.EqualTo(1), $"{id} should be single-projectile");
            }
        }

        [Test]
        public void AimingIsMoreAccurateThanFiringFromTheHip()
        {
            foreach (var id in AllWeapons)
            {
                Assert.That(WeaponProfiles.Get(id).aimingInaccuracyMultiplier,
                    Is.LessThan(1f).And.GreaterThan(0f),
                    $"{id}: aiming does not improve accuracy");
            }
        }

        [Test]
        public void AimingZoomsIn()
        {
            foreach (var id in AllWeapons)
            {
                var p = WeaponProfiles.Get(id);
                Assert.That(p.aimFieldOfView, Is.LessThan(p.hipFieldOfView),
                    $"{id}: aiming widens the view instead of narrowing it");
            }
        }

        [Test]
        public void TheScopedRifleIsTheMostZoomed()
        {
            var scoped = WeaponProfiles.Get(WeaponId.ScopedBoltAction).aimFieldOfView;
            foreach (var id in AllWeapons)
            {
                if (id == WeaponId.ScopedBoltAction) continue;
                Assert.That(scoped, Is.LessThan(WeaponProfiles.Get(id).aimFieldOfView),
                    $"{id} zooms in further than the 6x scope");
            }
        }

        [Test]
        public void AimingReducesRecoil()
        {
            foreach (var id in AllWeapons)
            {
                var p = WeaponProfiles.Get(id);
                Assert.That(p.aimRecoilMultiplier, Is.LessThanOrEqualTo(p.hipRecoilMultiplier),
                    $"{id}: aiming increases recoil");
            }
        }

        [Test]
        public void AimingSlowsMovement()
        {
            foreach (var id in AllWeapons)
            {
                Assert.That(WeaponProfiles.Get(id).aimMoveSpeedMultiplier,
                    Is.LessThan(1f).And.GreaterThan(0f),
                    $"{id}: aiming does not slow the player");
            }
        }

        [Test]
        public void TheShotgunReloadsShellByShell()
        {
            Assert.That(WeaponProfiles.Get(WeaponId.Shotgun).shellByShellReload, Is.True);
        }

        [Test]
        public void TheLmgIsTheOnlyDeployedWeapon()
        {
            foreach (var id in AllWeapons)
            {
                var p = WeaponProfiles.Get(id);
                if (id == WeaponId.Lmg)
                {
                    Assert.That(p.requiresDeploySetup, Is.True);
                    Assert.That(p.deploySetupTime, Is.GreaterThan(0f),
                        "a deployed weapon with no setup time deploys instantly");
                }
                else
                {
                    Assert.That(p.requiresDeploySetup, Is.False, $"{id} should not deploy");
                }
            }
        }

        [Test]
        public void FireModesMatchTheWeapon()
        {
            Assert.Multiple(() =>
            {
                Assert.That(WeaponProfiles.Get(WeaponId.BoltAction).fireMode,
                    Is.EqualTo(WeaponFireMode.BoltAction));
                Assert.That(WeaponProfiles.Get(WeaponId.ScopedBoltAction).fireMode,
                    Is.EqualTo(WeaponFireMode.BoltAction));
                Assert.That(WeaponProfiles.Get(WeaponId.SemiAutoRifle).fireMode,
                    Is.EqualTo(WeaponFireMode.SemiAuto));
                Assert.That(WeaponProfiles.Get(WeaponId.Lmg).fireMode,
                    Is.EqualTo(WeaponFireMode.Automatic));
            });
        }

        [Test]
        public void TheLmgIsTheFastestFiringWeapon()
        {
            var lmg = WeaponProfiles.Get(WeaponId.Lmg).fireInterval;
            foreach (var id in AllWeapons)
            {
                if (id == WeaponId.Lmg) continue;
                Assert.That(lmg, Is.LessThan(WeaponProfiles.Get(id).fireInterval),
                    $"{id} fires faster than the automatic weapon");
            }
        }

        [Test]
        public void GetIsDeterministic()
        {
            foreach (var id in AllWeapons)
            {
                Assert.That(WeaponProfiles.Get(id).damageClose,
                    Is.EqualTo(WeaponProfiles.Get(id).damageClose));
            }
        }
    }
}
