// The kinds of damage a buildable structure can take. Each FortificationType
// defines a resistance/vulnerability multiplier per type in
// FortificationManager.GetDamageMultiplier — e.g. sandbags shrug off bullets
// but fold quickly under an axe.
public enum DamageType : byte
{
    Bullet = 0,
    Explosive = 1,
    Axe = 2,
    Shovel = 3,
    Fire = 4
}
