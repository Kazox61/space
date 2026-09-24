using System;
using FFS.Libraries.StaticEcs;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Snapshot of physics object counts for lifecycle regression checks and leak diagnostics
	/// (Phase 0 of the Box3D migration plan -- see docs/physics-box3d-migration-assessment.md).
	/// </summary>
	public readonly record struct PhysicsCounts(int Bodies, int Shapes, int Proxies, int Contacts, int CachedPairs, int MovedProxies) {
		public override string ToString() => $"bodies={Bodies} shapes={Shapes} proxies={Proxies} contacts={Contacts} pairs={CachedPairs} moved={MovedProxies}";
	}

	public static class PhysicsDiagnostics {
		/// <summary>
		/// Captures the current physics object counts. Call after <c>Systems.Update()</c>: shapes only
		/// receive their broad-phase proxies during <see cref="ShapeProxySystem"/>'s update, so a
		/// capture taken between shape creation and the next update undercounts proxies.
		/// </summary>
		public static PhysicsCounts Capture() {
			var broadPhase = W.GetResource<BroadPhase>();
			return new PhysicsCounts(
				W.Query<All<Body>>().EntitiesCount(),
				W.Query<All<Shape>>().EntitiesCount(),
				broadPhase.ProxyCount,
				W.Query<All<Contact>>().EntitiesCount(),
				broadPhase.CachedPairCount,
				broadPhase.MovedProxyCount
			);
		}

		/// <summary>Counters and per-phase timings of the most recently completed physics step.</summary>
		public static PhysicsStepStats LastStep => PhysicsRuntime.Get().Stats;

		/// <summary>Structural health of the static, kinematic, and dynamic broad-phase trees.</summary>
		public static (BroadPhaseTreeStats Static, BroadPhaseTreeStats Kinematic, BroadPhaseTreeStats Dynamic) CaptureBroadPhase() {
			var broadPhase = W.GetResource<BroadPhase>();
			return (broadPhase.GetTreeStats(BodyType.Static), broadPhase.GetTreeStats(BodyType.Kinematic), broadPhase.GetTreeStats(BodyType.Dynamic));
		}

		/// <summary>
		/// Throws when physics state is stale or inconsistent: everything
		/// <see cref="BroadPhase.Validate"/> checks (proxy ownership, proxy AABBs, pairs, contacts),
		/// plus proxies that no longer bound a static or sleeping shape, and sleeping bodies carrying
		/// velocity, forces, or solver deltas -- the signature of state written directly instead of
		/// through <see cref="BodyOperations"/>, which would never be simulated.
		/// </summary>
		public static void Validate() {
			W.GetResource<BroadPhase>().Validate();

			foreach (var bodyEntity in W.Query<All<Body>>().Entities()) {
				ref readonly var body = ref bodyEntity.Read<Body>();
				if (!PhysicsSleep.IsSleeping(body)) {
					continue;
				}
				if (body.LinearVelocity != FVector3.Zero || body.AngularVelocity != FVector3.Zero
					|| body.Force != FVector3.Zero || body.Torque != FVector3.Zero
					|| body.DeltaPosition != FVector3.Zero || body.DeltaRotation != FQuaternion.Identity) {
					throw new InvalidOperationException($"Sleeping body {bodyEntity.GID.Raw} carries motion; wake it through BodyOperations before changing its state.");
				}
			}

			foreach (var shapeEntity in W.Query<All<Shape, W.Link<BodyOwner>>>().Entities()) {
				ref readonly var shape = ref shapeEntity.Read<Shape>();
				if (shape.ProxyKey == Shape.NullProxyKey
					|| !shapeEntity.Read<W.Link<BodyOwner>>().Value.TryUnpack<TWorld>(out var bodyEntity)
					|| !bodyEntity.Has<Body>()) {
					continue;
				}
				ref readonly var body = ref bodyEntity.Read<Body>();
				// Awake shapes legitimately lag their body by one solver step; static and sleeping ones
				// never move, so their proxy must still enclose their geometry.
				if (PhysicsSleep.IsAwake(body)) {
					continue;
				}
				var bounds = shape.ComputeFatAABB(body.Transform, B3Config.SpeculativeDistance);
				if (!shape.FatAabb.Contains(bounds)) {
					throw new InvalidOperationException($"Shape {shapeEntity.GID.Raw} has a stale broad-phase proxy that no longer encloses it.");
				}
			}
		}
	}
}
