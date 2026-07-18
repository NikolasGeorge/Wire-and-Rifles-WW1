# Wire and Warfare WW1 — Project Context

Unity 6.4 WW1 first-person shooter prototype. Bolt-action rifle combat, objective
capture, team tickets, down/revive mechanics.

Current state: fully functional single-player prototype. Next major work is
multiplayer networking with Fish-Net.

---

## WORKING STYLE — READ THIS FIRST

When giving Unity hierarchy instructions, be exact. Use this format:

    Parent Object:
    Right-click:
    Menu path:
    Name it:

Then give exact Inspector values and exact fields to assign. Do not summarize
setup steps or say "add the usual components." Name every component, every
field, and every value.

When editing scripts, prefer targeted edits over full-file rewrites unless the
refactor genuinely touches most of the file.

---

## TEAMS

- Neutral
- AlliedPowers
- CentralPowers

---

## SCRIPT INVENTORY

Located under Assets/Scripts, organized by folder:

**Characters/**
- PlayerController.cs (171 lines)
- PlayerTeam.cs (31)
- HitboxDamageZone.cs (7)
- HelmetPopOff.cs (223)

**Weapons/**
- BoltActionRifle.cs (823)
- WeaponData.cs (70)
- WeaponRecoil.cs (70)

**Health/**
- HealthComponent.cs (920)

**Objectives/**
- ObjectiveCaptureZone.cs (303)

**Teams/**
- Teams.cs (5)
- TeamTicketManager.cs (167)

**UI/**
- ObjectiveUI.cs (158)
- ObjectiveHUDLayout.cs (73)
- ObjectiveMessageUI.cs (185)
- ObjectiveEventType.cs (6)
- TeamTicketUI.cs (98)
- RevivePromptUI.cs (123)
- ReviveInteractor.cs (167)
- DownedWorldMarker.cs (247)
- CrosshairUI.cs (157)
- HitMarkerUI.cs (367)
- AmmoUI.cs (29)

---

## PLAYER / WEAPON SYSTEMS

Player uses CharacterController and PlayerController. Input is read directly
from the new Input System (Keyboard.current / Mouse.current) inside Update.

BoltActionRifle handles firing, ammo, reload, bolt cycling, bullet travel delay,
ADS, sprint-fire lockout, friendly fire toggle, and hit detection.

Details:
- Reloading is disabled while sprinting
- Bullet raycasts ignore trigger colliders so objective zones do not block shots
- ADS hides the crosshair
- ADS has gradual accuracy buildup over 0.2 seconds
- ADS accuracy bonus does not apply while jumping
- ADS move-speed penalty goes through PlayerController.SetWeaponMoveSpeedMultiplier()
- WeaponRecoil supports separate hip and ADS recoil multipliers

Rapid-fire accuracy penalty, configurable in WeaponData:
- second shot +50% inaccuracy
- third shot +40%
- fourth shot +30%
- decreasing to +10% minimum
- resets after 1 second

---

## OBJECTIVE SYSTEM

Objectives use signed control progress:
- -100 = Central Powers control
- 0 = Neutral
- +100 = Allied Powers control

Tug-of-war capture:
- Allied presence moves progress toward +100
- Central presence moves progress toward -100
- equal presence stalls

Decay rules:
- If an owned point is partially reduced but not neutralized, it decays back
  toward the owning team (Allied-owned at 2% decays back to 100%, Central-owned
  at -2% decays back to -100%)
- If progress reaches or crosses 0, the point becomes neutral and stays at or
  decays to 0 until captured again

Capture eligibility:
- Downed or dead units do not capture
- Revived units can capture again without leaving and re-entering the zone

Objective UI is a circular marker:
- letter A/B/C on top of the circle
- radial team-colored fill
- percent below
- local team is blue, enemy is red, neutral is gray
- objective letters must render above the circle fill

ObjectiveHUDLayout handles marker spacing — centered for odd counts, split
left/right for even counts.

---

## TICKET SYSTEM

TeamTicketManager tracks AlliedPowers and CentralPowers tickets. It is a
singleton (TeamTicketManager.Instance) with a static OnTicketsChanged event.

- Default starting tickets: 1000
- Death consumes tickets only on full death, not on down
- If revived before bleedout, no ticket is lost

Ticket bleed:
- controlling a majority of objectives causes enemy ticket bleed
- 1 ticket every 10 seconds for majority
- each additional objective over majority adds +1 ticket per tick

TeamTicketUI displays each team's tickets above the objective UI.

---

## DOWN / REVIVE SYSTEM

HealthComponent supports a downed state.

- Default bleedout timer: 30 seconds
- 0 HP enters downed state if enabled
- Downed units stop capturing
- Downed units can be finished with more damage if allowDamageToFinishDowned is enabled
- Full death consumes a ticket
- Revive restores health and standing pose

Downed body handling:
- Downed rotation is relative to the unit's current rotation, so a unit falls
  based on the way it was facing
- Downed local rotation default is -90, 0, 0. If fall direction is wrong, try 90, 0, 0
- Downed bodies ignore player collisions so players are not blocked by bodies
- Clipping prevention pushes on X/Z mostly or only
- Ground snap is applied after horizontal push to reduce floating
- Player movement and jumping should be disabled while downed; movement
  components can be disabled from HealthComponent

### Downed world marker

DownedWorldMarker is a world-space marker above a downed friendly, with a circle
background, radial draining circle, and + icon. Background, drain, and plus icon
have separate colors. No BleedoutText is used.

Hierarchy:

    Central Target
    ├── Characters
    │   └── Hips
    │       └── Spine
    │           └── Spine1
    │               └── DownedMarkerAnchor
    └── DownedWorldMarker
        └── MarkerVisualRoot
            ├── DownedCircleBackground
            ├── DownedCircleFill
            └── DownedPlusText

DownedWorldMarker stays separate from the spine — only the anchor attaches to
the chest/spine. Tune placement with DownedMarkerAnchor and Anchor Offset, never
by moving DownedWorldMarker directly.

### Revive

ReviveInteractor sits on the Player. Player looks at a downed friendly and holds
E to revive.

Revive prompt UI is Battlefield-style: green plus icon, full radial clock fill
like objectives, status text below such as "GETTING REVIVED BY Player" or
"HOLD E TO REVIVE".

Hierarchy:

    Canvas
    └── RevivePromptUI
        ├── ReviveCircleBackground
        ├── ReviveCircleFill
        ├── RevivePlusText
        └── ReviveStatusText

---

## HITBOXES

Animation-safe units use ~11 hitboxes, each attached to its matching bone so
they move with animations:

- HeadHitbox — damage multiplier 2.0, countsAsHeadshot = true
- UpperTorsoHitbox — 1.0
- LowerTorsoHitbox — 1.0
- LeftUpperArmHitbox — ~0.75-0.8
- LeftForearmHitbox — ~0.75-0.8
- RightUpperArmHitbox — ~0.75-0.8
- RightForearmHitbox — ~0.75-0.8
- LeftThighHitbox — ~0.75-0.8
- LeftCalfHitbox — ~0.75-0.8
- RightThighHitbox — ~0.75-0.8
- RightCalfHitbox — ~0.75-0.8

---

## NETWORKING PLAN (CURRENT MAJOR WORK)

### Decisions made

- **Library:** Fish-Net (Fish-Networking) V4, free tier
- **Hosting:** player-hosted listen server now, dedicated server later. Build
  everything server-authoritative from the start so the dedicated build is a
  small step rather than a rewrite.
- **Target players:** 32+. Note that a listen server at 32+ puts real load on the
  host machine, which is a strong argument for moving to dedicated before scale
  testing.
- **Hit detection:** Fish-Net Pro is required for raycast lag compensation
  (collider rollback). Not using Pro for now, so use client-side raycast with
  server validation. The client raycasts locally and sends the result via
  ServerRpc; the server validates and applies damage. This is cheatable but fine
  for a prototype, and the bolt-action fire rate keeps the traffic low. Structure
  the hit path so rollback can be swapped in later without rewriting callers.

### Known migration obstacles in current code

- PlayerController and BoltActionRifle read input directly from Keyboard.current
  and Mouse.current in Update. These need owner-only gating.
- HealthComponent calls FindObjectsByType<PlayerController>() around line 492 —
  needs revisiting for a multi-player scene.
- TeamTicketManager is a plain MonoBehaviour singleton and needs to become a
  networked singleton.
- ObjectiveCaptureZone uses a static event (OnObjectiveEvent) that UI subscribes
  to. Keep the event, but the server should be the only thing raising it.

### Phase 1 — Foundation

- Install Fish-Net, set up NetworkManager with Tugboat transport
- Make Player a NetworkObject with NetworkTransform
- Gate all PlayerController input behind IsOwner
- Disable camera and AudioListener on non-owned players
- Movement stays client-authoritative for now; CharacterController logic mostly
  unchanged
- Test with Unity 6 Multiplayer Play Mode rather than separate builds

Goal: two players see each other move.

### Phase 2 — Weapons

- Gate BoltActionRifle input to owner only
- Client raycasts, then sends the hit via ServerRpc
- Server validates: max range, rough angle plausibility, fire-rate cooldown,
  friendly fire setting, target is alive/not already dead
- ObserversRpc for muzzle flash, sound, tracer, bolt cycle animation
- TargetRpc back to the shooter for hit markers
- Keep ammo and reload state server-validated

Goal: players can shoot each other with correct hit markers.

### Phase 3 — Health, downed, revive

Largest refactor. HealthComponent is 920 lines.

- Current HP, downed state, and bleedout timer become SyncVars, written only by
  the server
- Damage application is server-only
- Revive becomes a ServerRpc from the reviver, validated server-side (distance,
  line of sight, target actually downed, same team)
- All visual behavior stays client-side, driven by SyncVar change callbacks:
  downed rotation, ground snap, clipping push, DownedWorldMarker, HelmetPopOff
- Ticket consumption on full death moves fully server-side

Goal: down, bleedout, and revive work correctly for all clients.

### Phase 4 — Objectives and tickets

- ObjectiveCaptureZone counts presence and computes progress on the server only
- Replicate progress and owning team as SyncVars
- TeamTicketManager becomes a networked singleton with SyncVar ticket counts
- Ticket bleed timer runs server-side only
- UI scripts read replicated values — these should need minimal changes

Goal: capture, decay, and ticket bleed all consistent across clients.

### Phase 5 — Polish (optional, defer)

- If listen-server movement feels bad for remote players, implement Fish-Net
  client-side prediction and reconciliation for the CharacterController
- This is a significant PlayerController rewrite. Do not start it until phases
  1-4 are stable.

### Version control note

Run the networking migration on its own branch. Keep a working single-player
build to fall back to, since Phase 3 rewrites a large share of HealthComponent.

---

## AFTER NETWORKING

- Tune downed clipping, wall push, and ground snap so bodies do not float and
  heads/limbs do not clip into walls
- Build a reusable animated unit prefab with PlayerTeam, HealthComponent,
  hitboxes, DownedMarkerAnchor, and DownedWorldMarker
- Continue improving revive UI and downed body behavior
- Attack phase timer, overtime, objective spawns, multiple objectives, more
  polished Attack and Secure mode
