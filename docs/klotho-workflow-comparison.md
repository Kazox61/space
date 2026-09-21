# Klotho vs. Space — Workflow Comparison

> Question: what can we adopt from `Klotho/` (local checkout) to make the Space Godot workflow easier?
> Scope: developer workflow, not architecture migration. Space keeps StaticEcs + FixedPoint + static-rollback.
> All claims verified against local source unless marked otherwise.

## 1. Where the two projects stand

| Area | Space (today) | Klotho | Source |
|---|---|---|---|
| ECS | StaticEcs (submodule) | own sparse-set ECS | — |
| Deterministic math | FixedPoint (Fixed64) | FP64 32.32 | — |
| Netcode | static-rollback + LiteNetLib | own engine + LiteNetLib | — |
| Server | `Server/Program.cs` console app | `Server~/` dedicated host | `Server/Program.cs` |
| View sync | catalog + factory + updater node + pooled views | factory + updater node + pool | `Client/synchronizer/` |
| Config | hardcoded C# (`tickRate: 60`) | Godot `.tres` Resources | `GameCore/GameSessionSetup.cs:8` |
| Desync diagnostics | **none** (grep: no hash/desync in static-rollback) | full funnel: classify → tick → layer, online probe | `Klotho/Docs/DesyncDiagnostics.md` |
| Determinism enforcement | discipline only | build-time Roslyn analyzer | `Klotho/Docs/GameDevWorkflow.md` §2 |
| Determinism CI | none | headless hash-dump sample | `Klotho/Samples/GodotDeterminismCheck/Main.cs` |
| Replay | none | record/playback/seek/speed | `Klotho/Docs/Replay.md` |
| Editor/runtime debug viz | none | physics overlay + HUD + static-collider viewer + navmesh viewer | `Klotho/Docs/PhysicsVisualizer.Godot.md` |
| Local (no-server) play | in-process server by default; `--server` selects an external host | `StartLocal` + `NullTransport` | `Client/setup/ClientGame.cs`, `Klotho/Docs/QuickStart.Godot.md` §"Single player" |
| Docs culture | `CLAUDE.md` only | cookbook, symptom tables, per-topic guides | `Klotho/Docs/Cookbook.md` |

What Space already covers well (nothing to do): deterministic math, rollback/prediction, dedicated server + Docker deploy, formatting CI (Husky + GitHub Actions, PR #1).

## 2. Ranked recommendations

### 1. Local iteration without a separately launched server — adopted
`ClientGame` starts `OfflineServer` by default and connects over loopback. `--server <host>` selects an external server, while `--port <port>` changes the local or remote port.

### 2. Catalog-driven view lifecycle and pooling — adopted
Space now uses `EntityViewCatalog`/`EntityViewFactory` for asset resolution, `EntityViewUpdater` for reconciliation, and `EntityViewPool` for reuse. The updater scans the complete `All<ViewId>` set so initial state, rollback, and full-state synchronization cannot miss a view. `entity_view_catalog.tres` prewarms each scene, including the projectile burst pool.

To add a renderable entity:

1. Define its `IEntityType` and set a `ViewId` in `OnCreate`.
2. Create a `.tscn` whose root is `EntityView` and assign its behaviors.
3. Add the `ViewAsset`, scene, and prewarm count to `Client/config/entity_view_catalog.tres`.

`ClientGame` owns session/input coordination only; the updater owns all active view nodes and returns them to the pool before world teardown.

### 3. Headless determinism check in CI — directly transplantable
`Klotho/Samples/GodotDeterminismCheck/Main.cs`: headless Godot run, seeded RNG inputs, per-tick `GetStateHash()` dumped to CSV, byte-compared against a baseline. Their harness compares Godot-vs-console output to prove cross-platform determinism.
**Adapt:** small harness in `Test/` (already a headless-capable console project): create world via `GameWorldSetup`, feed seeded `PlayerInput`, tick N times, hash world state per tick. StaticEcs has no built-in state hash — walk registered component types and fold serialized bytes (start with a manual `GetHashCode`-style fold; formalize later). Add as a second job next to the formatting check; fail on baseline mismatch. This is the regression net that makes all other refactors safe.

### 4. Desync diagnostics (minimal funnel) — the highest-value network feature Space lacks
Grep confirms static-rollback has no hashing/desync/replay support. Klotho's funnel (classify input-vs-state divergence → first diverged tick → per-component layer) turns "it desynced sometime" into a one-line verdict (`Klotho/Docs/DesyncDiagnostics.md` §1, §3.5).
**Adapt incrementally:** (a) per-tick whole-world hash on server + client, compare, log on mismatch — reuse #3's hash; (b) ring buffer of last N hashes for first-diverged-tick; (c) per-component layered hashes for localization. Skip the online probe initially. Needs a hash hook in the tick loop where both sides run — `GameUpdateRoot` is the natural place.

### 5. Determinism analyzer — guardrail at compile time
Klotho's `DeterminismAnalyzer` flags `float`/`double`, `Mathf`, `System.Math`, `DateTime`, `Random` inside deterministic-context types (`Klotho/Docs/GameDevWorkflow.md` §2, diagnostics `KLOTHO_DET002–004`). Space relies on review discipline; one stray `float` in a GameCore component silently breaks rollback.
**Adapt:** a small Roslyn analyzer package referenced by `GameCore` (analyzers already flow via `FFS.StaticEcs.Analyzers`, so the pattern exists in `GameCore.csproj:8-12`). Rule 1 (no float fields in `IComponent` structs) alone catches most real desyncs. Medium effort (~a day for an MVP), compounding payoff.

### 6. Godot Resource configs + recommended-values doc
Klotho ships `GodotSimulationConfig`/`GodotSessionConfig` as inspector-editable `.tres` plus a per-genre tuning guide with latency formulas (`Klotho/Docs/SimulationConfigGuide.md` §1). Space recompiles to change tick rate (`GameSessionSetup.cs:8`).
**Adapt:** a `[Resource]` class in Client for tick rate / connection / interpolation-delay, loaded in `ClientSetup`. Keep defaults in code, override via `.tres`. Cheap; mostly QoL.

### 7. Rollback/physics debug overlay
Klotho's runtime visualizer draws FP bodies/contacts with an inspector HUD, and the docs are explicit that it's release-safe (default-off `[Export]` toggles) — `Klotho/Docs/PhysicsVisualizer.Godot.md` §2-4.
**Adapt for Space's stack:** a debug `Node3D` drawing box3d colliders/contacts + a `CanvasLayer` HUD showing: current tick, rollback events, ticks rolled back, prediction mismatch count. static-rollback presumably exposes rollback events (verify hooks in `Client/Client<T>`); if not, wrap `CLNT.Update` and diff tick counters. High daily value for a rollback game; medium effort.

### 8. Replay recording — later
Klotho records the verified input chain to a file and replays/scrubs it (`Klotho/Docs/Replay.md`). Space's rollback state is a pure function of inputs, so an input log + headless playback = reproducible bug reports. Depends on #3's harness. Defer.

### 9. Build-fingerprint handshake — check first
Klotho refuses joins on layout fingerprint mismatch (component set/MaxCount folded into a hash compared before tick 0; `GameDevWorkflow.md` "The first thing that will refuse your join"). Stale-server-vs-client is a classic desync source with Space's deploy.sh flow.
**Adapt:** fold registered component-type ids (+ a game version) into one hash, log/compare at connect. Check whether static-rollback's handshake already carries a version — if yes, just include the type-registration hash in it. Small effort, prevents a whole bug class.

## 3. Explicitly not adopted

- **Klotho's ECS/math/netcode** — Space's StaticEcs + FixedPoint + static-rollback already fill those roles; migrating buys nothing.
- **HFSM, NavMesh, DataAssets, lobby/entitlement docs** — not current pain points; revisit when relevant gameplay arrives.
- **Unity-facing anything.**

## 4. Suggested order

1. #3 determinism CI job (safety net for everything after)
2. #4a minimal desync hash logging (server+client)
3. #5 analyzer, #9 fingerprint handshake
4. #7 debug overlay, #6 `.tres` configs
5. #8 replay
