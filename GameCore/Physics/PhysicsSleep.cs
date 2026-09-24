using System.Collections.Generic;
using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;
using FP64 = Fixed64.FP;
using Vector64 = Fixed64.FVector3;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Sleeping and islands, adapted from box3d's island.c/solver_set.c. Box3D keeps persistent
	/// islands and moves sleeping ones into separate solver sets; this port keeps all sleep state on
	/// <see cref="Body"/> (<see cref="Body.IsAwake"/>, <see cref="Body.SleepTime"/>) so rollback and
	/// full synchronization restore it with the component data, and derives islands each tick:
	/// <list type="bullet">
	/// <item>An island edge is a rigid (non-sensor, non-event-only) contact with manifold points,
	/// the same "sim touching" rule box3d uses to link contacts. Static bodies never join islands.</item>
	/// <item>Every tick, before solving, any sleeping body linked to an awake body is woken, repeated
	/// to a fixed point. Because sleeping bodies never move, their retained contact manifolds stay
	/// exact, so this reproduces box3d's "wake the whole island" behavior without storing islands.</item>
	/// <item>After solving, awake bodies are grouped by union-find over the tick's edges; an island
	/// whose every body has rested for <see cref="B3Config.TimeToSleep"/> falls asleep together.</item>
	/// </list>
	/// Sleeping bodies are skipped by proxy refresh, narrowphase (when no awake body is involved),
	/// solving, and CCD, so resting scenes cost roughly one cheap check per body and contact.
	/// </summary>
	public static class PhysicsSleep {
		private static readonly FP MaximumSleepTime = 1024.ToFP();

		/// <summary>Whether a non-static body is currently simulated (static bodies are never awake).</summary>
		public static bool IsAwake(in Body body) => body.Type != BodyType.Static && (!body.EnableStateInitialized || body.IsAwake);

		/// <summary>Whether an enabled non-static body is asleep.</summary>
		public static bool IsSleeping(in Body body) => body.Type != BodyType.Static && BodyOperations.IsEnabled(body) && !IsAwake(body);

		/// <summary>Whether the solver integrates this body this tick.</summary>
		internal static bool IsSimulated(in Body body) => body.Type != BodyType.Static && BodyOperations.IsEnabled(body) && IsAwake(body);

		/// <summary>Whether a contact links its two bodies into one island (and can wake a sleeping one).</summary>
		internal static bool LinksIsland(in Contact contact) =>
			contact.Manifold.PointCount > 0 && !contact.IsSensorContact && !contact.IsEventOnly;

		/// <summary>Wakes one body and resets its sleep timer. Its island wakes before the next solve.</summary>
		internal static void WakeBody(ref Body body) {
			if (body.Type == BodyType.Static || !BodyOperations.IsEnabled(body)) {
				return;
			}
			if (!IsAwake(body)) {
				PhysicsRuntime.Get().Pending.BodiesWoken++;
			}
			body.IsAwake = true;
			body.SleepTime = FP.Zero;
		}

		internal static void WakeBody(W.Entity bodyEntity) {
			if (bodyEntity.Has<Body>()) {
				WakeBody(ref bodyEntity.Ref<Body>());
			}
		}

		/// <summary>Resolves a shape GID to its owning body entity.</summary>
		internal static bool TryGetBodyOfShape(EntityGID shapeGid, out W.Entity bodyEntity) {
			bodyEntity = default;
			if (!shapeGid.TryUnpack<TWorld>(out var shapeEntity) || !shapeEntity.Has<W.Link<BodyOwner>>()) {
				return false;
			}
			return shapeEntity.Read<W.Link<BodyOwner>>().Value.TryUnpack<TWorld>(out bodyEntity) && bodyEntity.Has<Body>();
		}

		/// <summary>
		/// Wakes every sleeping body reachable from an awake body through island edges, then records
		/// the edges between simulated non-static bodies for <see cref="UpdateIslands"/>. Runs to a
		/// fixed point, so the result does not depend on contact iteration order.
		/// </summary>
		internal static void PropagateWake(PhysicsRuntime runtime) {
			var edges = runtime.IslandEdges;
			bool changed;
			do {
				changed = false;
				edges.Clear();
				foreach (var contactEntity in W.Query<All<Contact>>().Entities()) {
					ref readonly var contact = ref contactEntity.Read<Contact>();
					if (!LinksIsland(contact)
						|| !TryGetBodyOfShape(contact.ShapeA, out var bodyAEntity)
						|| !TryGetBodyOfShape(contact.ShapeB, out var bodyBEntity)
						|| bodyAEntity == bodyBEntity) {
						continue;
					}

					ref var bodyA = ref bodyAEntity.Ref<Body>()!; // TryGetBodyOfShape only resolves entities with Body.
					ref var bodyB = ref bodyBEntity.Ref<Body>()!;
					if (IsSimulated(bodyA) && IsSleeping(bodyB)) {
						WakeBody(ref bodyB);
						changed = true;
					} else if (IsSimulated(bodyB) && IsSleeping(bodyA)) {
						WakeBody(ref bodyA);
						changed = true;
					}

					if (IsSimulated(bodyA) && IsSimulated(bodyB)) {
						edges.Add((bodyAEntity, bodyBEntity));
					}
				}
			} while (changed);
		}

		/// <summary>
		/// Advances <paramref name="body"/>'s sleep timer from the step it just took. Mirrors box3d's
		/// b3FinalizeBodies sleep velocity: surface speed from linear and angular velocity, plus half of
		/// the position-correction speed so a body being pushed apart does not count as resting.
		/// </summary>
		internal static void UpdateSleepTime(ref Body body, bool worldEnablesSleep, FP dt, FP invDt) {
			if (!worldEnablesSleep || !body.EnableSleep) {
				body.SleepTime = FP.Zero;
				return;
			}

			// Fixed64: kinematic bodies may exceed the dynamic speed clamp, and squared lengths or
			// velocity-times-extent products can leave the Q16.16 range.
			var extent = body.MaxExtent.To64();
			var maxVelocity = Vector64.Length(body.LinearVelocity.To64()) + Vector64.Length(body.AngularVelocity.To64()) * extent;
			var rotationArc = new Vector64(body.DeltaRotation.X.To64(), body.DeltaRotation.Y.To64(), body.DeltaRotation.Z.To64());
			var maxDeltaPosition = Vector64.Length(body.DeltaPosition.To64()) + 2 * Vector64.Length(rotationArc) * extent;
			var sleepVelocity = FP64.Max(maxVelocity, FP64.Half * invDt.To64() * maxDeltaPosition);

			body.SleepTime = sleepVelocity > body.SleepThreshold.To64() ? FP.Zero : FP.Min(body.SleepTime + dt, MaximumSleepTime);
		}

		/// <summary>
		/// Groups this tick's simulated bodies into islands over the edges recorded by
		/// <see cref="PropagateWake"/> and puts every island whose bodies have all rested long enough
		/// to sleep. <paramref name="bodies"/> are the solver's simulated bodies.
		/// </summary>
		internal static void UpdateIslands(PhysicsRuntime runtime, List<W.Entity> bodies) {
			var parents = runtime.IslandParents;
			var indices = runtime.BodyIndices;
			var minSleepTime = runtime.IslandSleepTimes;
			var broadPhase = W.GetResource<BroadPhase>();
			parents.Clear();
			indices.Clear();
			minSleepTime.Clear();
			for (var i = 0; i < bodies.Count; i++) {
				parents.Add(i);
				indices[bodies[i]] = i;
			}

			foreach (var (a, b) in runtime.IslandEdges) {
				if (indices.TryGetValue(a, out var indexA) && indices.TryGetValue(b, out var indexB)) {
					Union(parents, indexA, indexB);
				}
			}

			for (var i = 0; i < bodies.Count; i++) {
				minSleepTime.Add(FP.MaxValue);
			}
			var islandCount = 0;
			for (var i = 0; i < bodies.Count; i++) {
				var root = Find(parents, i);
				if (root == i) {
					islandCount++;
				}
				ref readonly var member = ref bodies[i].Read<Body>()!; // The solver's body list comes from Query<All<Body>>.
				var sleepTime = IsSimulated(member) ? member.SleepTime : FP.Zero;
				minSleepTime[root] = FP.Min(minSleepTime[root], sleepTime);
			}
			runtime.Pending.Islands = islandCount;

			for (var i = 0; i < bodies.Count; i++) {
				var root = Find(parents, i);
				if (minSleepTime[root] < B3Config.TimeToSleep) {
					continue;
				}
				if (root == i) {
					runtime.Pending.IslandsFellAsleep++;
				}
				// Finalization moved the body after the regular proxy pass; refresh once before it stops simulating.
				ref var body = ref bodies[i].Ref<Body>()!; // The solver's body list comes from Query<All<Body>>.
				ShapeProxySystem.RefreshBodyAABBs(bodies[i], body, broadPhase);
				body.IsAwake = false;
				body.IsEnabled = true;
				body.EnableStateInitialized = true;
				body.LinearVelocity = FVector3.Zero;
				body.AngularVelocity = FVector3.Zero;
				body.Force = FVector3.Zero;
				body.Torque = FVector3.Zero;
			}
		}

		private static int Find(List<int> parents, int index) {
			while (parents[index] != index) {
				parents[index] = parents[parents[index]];
				index = parents[index];
			}
			return index;
		}

		private static void Union(List<int> parents, int a, int b) {
			var rootA = Find(parents, a);
			var rootB = Find(parents, b);
			if (rootA == rootB) {
				return;
			}
			// Lower index wins so roots are independent of edge order.
			if (rootA < rootB) {
				parents[rootB] = rootA;
			} else {
				parents[rootA] = rootB;
			}
		}
	}
}
