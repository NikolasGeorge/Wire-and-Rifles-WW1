using NUnit.Framework;

namespace WireAndRifles.Tests
{
    /// <summary>
    /// These enums are byte-backed on purpose: FishNet serialises them by
    /// their numeric value and sends them between client and server. The
    /// numbers are therefore part of the network protocol, not an
    /// implementation detail.
    ///
    /// Reordering a member or inserting one in the middle renumbers everything
    /// after it. Nothing fails to compile, and a client on the old build keeps
    /// connecting to a server on the new one — it just starts reading the
    /// wrong weapon, the wrong class, or the wrong team. These tests pin the
    /// values so that change is a failing build instead of a bug report.
    /// </summary>
    public class WireProtocolTests
    {
        [Test]
        public void PlayerClassValuesAreStable()
        {
            Assert.Multiple(() =>
            {
                Assert.That((byte)PlayerClass.Assault, Is.EqualTo(0));
                Assert.That((byte)PlayerClass.Medic, Is.EqualTo(1));
                Assert.That((byte)PlayerClass.Support, Is.EqualTo(2));
                Assert.That((byte)PlayerClass.Scout, Is.EqualTo(3));
                Assert.That((byte)PlayerClass.Engineer, Is.EqualTo(4));
                Assert.That((byte)PlayerClass.Officer, Is.EqualTo(5));
            });
        }

        [Test]
        public void WeaponIdValuesAreStable()
        {
            Assert.Multiple(() =>
            {
                Assert.That((byte)WeaponId.BoltAction, Is.EqualTo(0));
                Assert.That((byte)WeaponId.ScopedBoltAction, Is.EqualTo(1));
                Assert.That((byte)WeaponId.SemiAutoRifle, Is.EqualTo(2));
                Assert.That((byte)WeaponId.Shotgun, Is.EqualTo(3));
                Assert.That((byte)WeaponId.Lmg, Is.EqualTo(4));
                Assert.That((byte)WeaponId.Pistol, Is.EqualTo(5));
            });
        }

        [Test]
        public void DamageTypeValuesAreStable()
        {
            Assert.Multiple(() =>
            {
                Assert.That((byte)DamageType.Bullet, Is.EqualTo(0));
                Assert.That((byte)DamageType.Explosive, Is.EqualTo(1));
                Assert.That((byte)DamageType.Axe, Is.EqualTo(2));
                Assert.That((byte)DamageType.Shovel, Is.EqualTo(3));
                Assert.That((byte)DamageType.Fire, Is.EqualTo(4));
            });
        }

        [Test]
        public void WeaponFireModeValuesAreStable()
        {
            Assert.Multiple(() =>
            {
                Assert.That((byte)WeaponFireMode.BoltAction, Is.EqualTo(0));
                Assert.That((byte)WeaponFireMode.SemiAuto, Is.EqualTo(1));
                Assert.That((byte)WeaponFireMode.Automatic, Is.EqualTo(2));
            });
        }

        [Test]
        public void TeamValuesAreStable()
        {
            // Neutral must stay 0: it is the value an uninitialised Team
            // field holds, and an unassigned player has to read as Neutral
            // rather than as a live combatant.
            Assert.Multiple(() =>
            {
                Assert.That((int)Team.Neutral, Is.EqualTo(0));
                Assert.That((int)Team.AlliedPowers, Is.EqualTo(1));
                Assert.That((int)Team.CentralPowers, Is.EqualTo(2));
            });
        }
    }
}
