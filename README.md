# Wire and Rifles WW1

[![CI](https://github.com/NikolasGeorge/Wire-and-Rifles-WW1/actions/workflows/ci.yml/badge.svg)](https://github.com/NikolasGeorge/Wire-and-Rifles-WW1/actions/workflows/ci.yml)

A multiplayer WW1 first-person shooter prototype built in Unity, using
[Fish-Net](https://fish-networking.gitbook.io/docs/) for server-authoritative
networking.

## Overview

Two teams — **Allied Powers** and **Central Powers** — fight over a set of
capturable objectives. Tickets bleed for the losing side as the map tips in
the other team's favor. Bolt-action combat is close-range and deliberate:
missing your shot means a real reload, not a quick correction.

## Core systems

- **Objective control** — tug-of-war capture with signed progress (-100 to
  +100), decay toward the owning team, and ticket bleed scaled by how many
  points a team controls.
- **Classes and loadouts** — Assault, Medic, Support, Scout, Engineer, and
  Officer, each with a fixed or customizable weapon/grenade/equipment kit.
- **Down and revive** — a two-stage health pool (standing HP, then a downed
  bleedout pool), server-validated revives, and a give-up option once the
  bleedout timer allows it.
- **Fortifications** — Engineers place blueprints that any teammate can build
  up with a shovel; sandbags, wire, trench walls, duck boards, ladders, and
  makeshift floors, each with type-specific damage resistances (bullet,
  explosive, axe, shovel, fire).
- **Terrain digging** — shallow, shovel-driven terrain deformation that
  replicates to every client and survives late joins.
- **Grenades** — cook-and-throw with a server-authoritative fuse; holding too
  long detonates in your hand.
- **Melee combat** — a universal shovel and a class-restricted axe, each with
  its own damage profile against players and structures.
- **Suppression** — sustained incoming fire degrades your aim and vision
  (camera shake, blur, vignette, and audio), weighted by weapon damage and
  proximity rather than a flat timer.

## Tech

- Unity 6, Universal Render Pipeline
- [Fish-Net](https://fish-networking.gitbook.io/docs/) for networking
  (server-authoritative movement validation, damage, objectives, and
  fortification state)
- New Input System

## Tests

```bash
dotnet test Tests/WireAndRifles.Tests
```

41 tests over the game's data and rules. They run anywhere the .NET SDK is
installed — no Unity, no editor, no licence.

Unity's own Test Framework needs an activated licence to run in CI, which
means putting personal Unity credentials into a repository secret. The logic
most worth guarding here has no Unity dependency at all, so the test project
compiles those source files directly instead. It lives outside `Assets/`, so
Unity never sees it.

What the tests cover:

| Area | Why it's tested |
|---|---|
| **Wire protocol** | `PlayerClass`, `WeaponId`, `DamageType` and `WeaponFireMode` are byte-backed and serialized by Fish-Net, so their numeric values *are* the network format. Inserting an enum member in the middle renumbers everything after it — nothing fails to compile, and an old client keeps connecting to a new server while reading the wrong weapon. These pin the values. |
| **Class table** | Every enum member has a definition, every class has a weapon and both equipment slots, and the role abilities stay exclusive: Medic is the only class that can revive, Scout the only one that can spot, Engineer the only one that builds faster. `Get()` silently falls back to Assault on a bad index, so a missing definition would otherwise spawn everyone as Assault. |
| **Weapon profiles** | Damage never increases with distance, range bands stay ordered, aiming always improves accuracy and reduces movement speed, and the scoped rifle stays the most zoomed. These numbers drive server-side hit validation, so a zeroed profile changes what the server accepts from a client, not just how the gun feels. |
| **Bad input** | An unrecognised class or weapon id — which any stale or malicious client can send — resolves to a valid fallback rather than indexing past the end of the table. |

CI runs the suite on Ubuntu and Windows.

## Status

Actively in development. Core gameplay loop (spawn, fight, capture, tickets,
down/revive) is playable in a listen-server setup; a dedicated-server build
is planned as a follow-up rather than a rewrite, since gameplay logic was
built server-authoritative from the start.
