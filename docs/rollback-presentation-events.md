# Rollback-safe sound and VFX triggers — plan

> Question: how do we play one-shot sounds and VFX from a rolled-back simulation without playing
> them twice, and without cutting off a sound that already started?
> Status: plan, not implemented. Claims about Space, static-ecs, static-rollback and Klotho are
> checked against local source. §8 lists earlier ideas and why they were dropped.

## 1. Problem

The client predicts ahead of the server. When a correction arrives, `static-rollback` restores a
world snapshot and re-runs the ticks (`Session.Data.FastForwardToTick`,
`static-rollback/Runtime/Static/Session.Data.cs:314`). A tick that fired an effect can therefore
run two or more times, and an effect predicted on the first run may not happen at all on the
second. Rollback restores the last *saved* frame (every `SaveEachNthTick` = 5 ticks), so even
ticks whose inputs did not change get re-simulated routinely.

Today the client does not see ticks. `EntityViewUpdater.Reconcile`
(`Client/synchronizer/EntityViewUpdater.cs`) runs once per rendered frame and compares the
current world state against the views. It cannot tell a re-simulated tick from a new one, so both
current sounds infer their trigger from state:

| Sound | Current trigger | Failure mode |
|---|---|---|
| `huntress_launch.wav` | `PlayerPresentationBehavior`: attack input is fresh on the rendered tick and `_lastAttackTick != S.CurrentTick` | Corrections to a remote player's input can play it at the wrong moment or twice |
| `huntress_hit.wav` | `ProjectilePresentationBehavior`: view removed while `Lifetime` > 2 ticks | **Bug:** a rollback that re-creates the arrow under a new entity ID removes the old view, so a hit sound plays, and a second arrow view appears |

Required behavior:

1. An effect plays **once** per event, however often its tick is re-simulated.
2. An effect that already started is **never stopped**, even if a correction shows the event
   never happened.
3. Future VFX spawns use the same mechanism.

### 1.1 Simulation bug found while checking this: predicted remote attacks repeat

`ShootSystem` reads the attack with `input.LastFresh()`, which simply returns `input.Data`
(`static-rollback/Runtime/Static/Utils/InputExtensions.cs:6`). On a predicted tick the input is
`Aged()`, which copies the last data forward (`Session.Inputs.cs:35`). So once a remote player's
attack arrives, **every predicted tick until their next input also carries the attack**, and each
queues a `PendingShot`. The next correction removes them again.

Consequences:

- Ghost arrows appear and disappear for remote players. This is a likely cause of the "second
  arrow view" in the table above.
- Any effect keyed by tick would fire once per ghost tick. No dedup can catch that, because the
  ticks really differ.

Fix: read edge-triggered inputs with `input.FreshOrDefault()`. A predicted (aged) input then
carries no attack. `PlayerPresentationBehavior.cs:72` already checks `IsFresh`, so only the
simulation is wrong. Check `Jump` for the same problem.

## 2. How rollback games usually handle this

- **Predict cosmetic effects immediately.** Waiting for server confirmation would add the full
  network delay to every sound.
- **Fire from simulation events that carry a stable ID:** event type + a stable tick + source
  (e.g. "attack started, tick 1234, player 2"). A re-simulation produces the same ID, so
  a set of recently played IDs covering the rollback window catches the duplicate.
- **Don't retract.** A mispredicted one-shot plays out. An extra "pew" is barely noticeable; a
  sound that cuts off or plays twice is.
- **Skip late arrivals for cosmetic effects.** If a correction reveals an event from, say,
  150 ms ago, a short cosmetic one-shot is skipped. Effects that carry gameplay information
  (an enemy shooting) are still worth playing late.
- **Drive looping effects from state.** Trails, auras and charge-up glows are rebuilt from the
  current state every frame, so rollbacks correct them automatically. Only one-shots need the
  dedup.
- **Critical events wait for confirmation.** Kills and score changes fire only once the tick is
  verified.

## 3. What Klotho does

Sources: `Klotho/Docs/SynchronizationDesign.md` §10,
`Klotho/com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.EventHelpers.cs`,
`Klotho/com.xpturn.klotho/Unity/View/EngineEventOneShot.cs`,
`Klotho/Samples/Brawler/Assets/Brawler/Scripts/View/CharacterActionVfxViewComponent.cs`.

- **Two event kinds** (`SimulationEvent.Mode`):
  - **Regular** (cosmetic) fires immediately on predicted ticks as `OnEventPredicted`.
  - **Synced** (critical) is buffered and fires exactly once, as `OnSyncedEvent`, when its tick is
    verified. A high-water mark (`_syncedDispatchHighWaterMark`) stops it firing twice after a
    rollback.
- **After a rollback it diffs events** (`DiffRollbackEvents`). It collects the events from the
  re-simulated ticks and matches them against the previous set by tick + type +
  `GetContentHash()`. Only the differences are dispatched:
  - `OnEventCanceled` for events that disappeared.
  - `OnEventConfirmed` / `OnEventPredicted` for events that are new.
  - Nothing for unchanged events. This is the dedup.
- **View helper `EngineEventOneShot.Subscribe`**: Predicted and Confirmed both call `onPlay`,
  Canceled calls an optional `onCancel`, and an optional `lateGuard` skips stale plays. The
  Brawler sample passes `onCancel` to stop VFX. Our requirement (never stop) is the same helper
  without `onCancel`.

We don't need Klotho's full diff. It exists to report cancellations, and we never act on those,
so a "played IDs" set gives the same result for our case. Klotho's events live outside the
simulated state, and our sink below does the same (see §8.1 for why that matters).

## 4. Design for Space

### 4.1 Overview

```
GameCore systems (every tick, incl. re-sim)             Client
──────────────────────────────────────────              ─────────────────────────────────────
ShootSystem         ── FxSink?.Record(fx, tick) ──►     FxLog.Record  (pending list)
ProjectileHitSystem ── FxSink?.Record(fx, tick) ──►
                                                        ClientGame._Process, after CLNT.Update:
                                                          └─ FxLog.Flush(S.CurrentTick) ─► FxPlayer
                                                               new key, not late → play
                                                               key already played → skip
```

### 4.2 GameCore: the sink

A static hook on `Core<TWorld>`, outside the ECS world:

```csharp
public interface IFxSink { void Record(in FxEvent fx, int tick); }
public static IFxSink? FxSink;   // null on the server; set by ClientSetup
```

- It is static **per closed generic** `Core<TWorld>`. The in-process offline server uses its own
  world type, so its copy stays null and the server never records anything.
- It adds nothing to the ECS world: no registration in `GameTypes`, no snapshot data, no receiver
  state, no effect on full sync or checksums.
- Systems call it with `S.CurrentTick`, which inside a system is the tick being simulated
  (it is incremented after `UpdateRoot.Update`, `Session.Data.cs:354`).

`FxEvent` is one small struct with a kind enum. It holds only what the client needs to key
and place the effect; no gameplay state flows back into the simulation.

```csharp
public enum FxKind : byte { AttackStarted, ShotReleased, ProjectileHit }
public struct FxEvent {
    public FxKind Kind;
    public ushort Channel;       // shooter's PlayerInfo.InputChannel
    public int    KeyTick;       // stable tick for the key (see 4.4)
    public FVector3 Position;
    public FVector3 Direction;   // ShotReleased only
}
```

- `ShootSystem` records `AttackStarted` when it queues a `PendingShot` (`KeyTick` = current tick)
  and `ShotReleased` when it spawns the projectile, for a future muzzle flash.
- The projectile gets `SpawnTick` and the shooter's `Channel` (a small component; this is
  simulation state, so it is snapshotted with everything else). `ProjectileHitSystem.HandleHit`
  records `ProjectileHit` with `KeyTick = SpawnTick`, at the projectile's position.

### 4.3 Client: collect during simulation, play after

- **`FxLog`** (plain C# class in GameCore, no Godot dependency, one per client session) implements
  `IFxSink`:
  - `Record` adds the event to a pending list unless its key is already played or pending.
  - `Flush(headTick)` is called from `ClientGame._Process` right after `CLNT.Update`. For each
    pending event:
    - If `headTick - eventTick > LateTicks(kind, isLocal)`, drop it and mark it played.
    - Otherwise dispatch it to `FxPlayer` and mark it played.
  - The played set is pruned to entries with
    `tick >= headTick - (S.FramesCapacity - 1) * S.SaveEachNthTick - margin`
    (= 125 ticks with the defaults, see `tests/GameCore.TestSupport/Phase6Tests.cs:27`).
    No rollback can reach further back, so no older key can come back.
  - Invariant: every `LateTicks` is smaller than the prune window. Otherwise a pruned key could be
    re-played.
  - `Clear()` on full sync (`GameWorldFullSyncHandler.ReadFullSync`). A full sync is rare and
    starts a new timeline, so there's nothing worth keeping.
- **`LateTicks`** per kind and source:
  - Local cosmetic effects: ~9 ticks (150 ms at 60 Hz).
  - Remote `AttackStarted`: large, up to the prune window. A remote attack only reaches us through
    a correction, after the remote player's send delay, the server relay and our prediction lead,
    which is often more than 150 ms. A short guard would drop most enemy shot sounds, and those
    carry gameplay information. Better: play it with a start offset of
    `(headTick - eventTick) / TickRate` seconds.
- **`FxPlayer`** (Godot side) maps kinds to presentation:
  - `AttackStarted` → `Audio.PlaySfx(huntress_launch)`
  - `ProjectileHit` → `Audio.PlaySfx(huntress_hit)` and, later, a hit VFX at `Position`
  - `ShotReleased` → future muzzle VFX
  - VFX spawn detached into the world, like `PlayerPresentationBehavior.SpawnParticles` does
    today, so pooled views that leave the tree can't cut them off.

The collect/flush split matters: during a fast-forward the client may re-simulate many ticks in
one frame, and only after `CLNT.Update` returns do we know the head tick for the late check.
`FastForwardToTick` can stop early when it hits its time budget, so the head is the simulated
tick, not the target. That is the right reference for the late check.

### 4.4 Keys

| Kind | Key | Why |
|---|---|---|
| `AttackStarted` | `(kind, Channel, attack tick)` | One attack per input tick per player (with §1.1 fixed) |
| `ShotReleased` | `(kind, Channel, attack tick)` | Same shot as the attack; release tick follows from it |
| `ProjectileHit` | `(kind, Channel, SpawnTick)` | An arrow hits once. A correction that moves the hit by a tick is still the same hit |

Position is payload, not key, so a correction that moves a hit by a centimetre is still the same
event. Entity IDs are never part of the key; rollback can re-create entities under new IDs.

### 4.5 What gets removed

- `PlayerPresentationBehavior`: `_attackSound` and the `Audio.PlaySfx` call. The attack
  *animation* can stay input-driven for now; it's visual state, and a restart on correction is not
  a duplicate sound. A later step can move it to `AttackStarted` too.
- `ProjectilePresentationBehavior`: the whole lifetime heuristic. Delete the behavior if nothing
  else is left in it, and remove it from `entity/arrow.tscn`.

### 4.6 What stays state-driven

The arrow's trail and flares (`entity/arrow.tscn`), the player's run/idle/jump state and tilt, and
footsteps. Footsteps come from animation method tracks on the rendered animation, not from
simulation ticks, so re-simulation doesn't replay them.

## 5. Questions answered while checking the plan

1. **Does rollback restore the event ring?** Yes. `CreateWorldSnapshot` has `writeEvents = true`
   by default (`static-ecs/Src/World.Serializer.cs:1001`). Events get a GUID by default, and each
   pool writes its receiver count and read positions (`World.Events.cs:893-901`). This is why
   the design uses a sink instead of ECS events (§8.1).
2. **`S.CurrentTick` after a hard reset or full sync:** `HardReset` sets `CurrentTick = startTick`
   (`Session.Data.cs:290`), so the first simulated tick is recorded with the right tick.
   Still covered by a test.
3. **Full-state resync:** `FxLog.Clear()` on every full sync (§4.3).
4. **Registration hook for a client-only system:** not needed anymore. The client sets
   `FxSink` in `ClientSetup`.

## 6. Tests

In `tests/GameCore.TestSupport` (existing `RollbackScenarios` style):

- **Remote attack not repeated (§1.1):** a remote attack input followed by several predicted
  ticks queues one `PendingShot` and records one `AttackStarted`. Write this one first; it fails
  today.
- **Dedup:** fire an attack, force a rollback over its tick with identical input, and check that
  `FxLog` dispatched `AttackStarted` once.
- **No stop on mispredict:** predict an attack, then correct the input so it never happens. Check
  that the event was dispatched once and nothing retracts it.
- **Late guard:** a local cosmetic event older than its `LateTicks` is not dispatched; a remote
  `AttackStarted` of the same age is.
- **Hit keyed by arrow:** a rollback that re-creates the projectile (new entity ID) and moves the
  hit by one tick dispatches one `ProjectileHit`.
- **Pruning:** after `(FramesCapacity - 1) * SaveEachNthTick + margin` ticks the played set no
  longer holds old keys.
- **Full sync:** after a full sync `FxLog` is empty and new events still dispatch.
- **Tick after hard reset:** the first recorded event after `HardReset(startTick)` carries
  `startTick`.

## 7. Steps

1. Fix §1.1 (`FreshOrDefault` for attack and, if affected, jump) with its test.
2. Add `FxEvent`, `IFxSink`, `FxSink`; record from `ShootSystem` and `ProjectileHitSystem`. Add
   `SpawnTick` and `Channel` to projectiles.
3. Add `FxLog` plus its tests (dedup, late guard, pruning, full sync).
4. Set `FxSink` in `ClientSetup`; call `FxLog.Flush` from `ClientGame._Process`; clear it on full
   sync.
5. Add `FxPlayer` and route the launch and hit sounds through it.
6. Remove the input-based launch sound and `ProjectilePresentationBehavior`.
7. Re-check whether the duplicate arrow view is gone after step 1. If not, it is a separate view
   identity bug in `EntityViewUpdater` and gets its own plan.

## 8. Rejected approaches (earlier drafts) and why

### 8.1 ECS events + client-only `FxCollectSystem`

The first draft sent `AttackStartedFx` / `ShotReleasedFx` / `ProjectileHitFx` with
`W.SendEvent` and read them in a client-only system at the last order.

Why it doesn't work:

- **Full sync breaks the client receiver.** Receiver state is part of the world snapshot
  (§5.1). The server has no `FxCollectSystem`, so its snapshot has no receiver for these events.
  Loading it resets `_receiversCount` (`World.Events.cs:856-877`) and the client's receiver ID
  points at nothing.
- **Making the events `INonSerializable` doesn't fix it.** `ReadEvents` → `ClearEvents` →
  `Reset()` clears the receivers of *every* pool, serializable or not, so the receiver would be
  wiped on every rollback instead.
- A workaround exists (run the draining system in GameCore on the server too, so receiver state
  matches), but it puts presentation-only work into the server tick, needs a
  client-only-system hook, and adds three types to both lists in `GameTypes`. The sink needs none
  of that.

### 8.2 Prune window of `FramesCapacity + margin` ticks

`FramesCapacity` counts saved *frames*, not ticks. With `SaveEachNthTick = 5` the real window is
`(26 - 1) * 5 = 125` ticks, about five times more than the draft assumed. With the short window,
a key could be pruned and then replayed by a deep rollback (hidden only as long as every
`LateTicks` stays short).

### 8.3 A single ~150 ms late guard for everything

Fine for local cosmetic effects, but remote attacks always arrive through corrections and are
often older than that. The draft would have silenced most enemy shot sounds.

### 8.4 Hit key `(ProjectileHitFx, hit tick, Channel)`

A correction that shifts the hit by one tick (the target moved slightly differently) creates a
new key and a second hit sound. It also merged two arrows from the same shooter that hit on the
same tick. Keying by the arrow's `SpawnTick` fixes both.

### 8.5 Keeping `LastFresh()` for the attack input

The draft assumed the attack only exists on its input tick. It doesn't for predicted remote
players (§1.1), so no tick-based key could deduplicate the resulting ghost attacks.
