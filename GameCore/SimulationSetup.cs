using Shenanicode.Rollback;
using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public static class SimulationSetup {
		public static void Register() {
			Const.DeltaTime = FP.One / S.TickRate;
			Const.InvDeltaTime = S.TickRate.ToFP();

			Systems.SetResource(new PhysicsRes());
			Systems.SetResource(new CharacterRes());
			Systems.SetResource(new PlanetRes());
			Systems.SetResource(new DummyRes());
			// World-scoped (not Systems.SetResource): GameWorldRollback/GameWorldFullSyncHandler both
			// snapshot via W.Serializer, which only walks World<TWorld>'s own resource registry --
			// Systems<TSystemsType> keeps a completely separate one that never gets serialized here.
			W.SetResource(new PhysicsWorld());
			W.SetResource(new BroadPhase());
			Systems.Add(new SpawnPlayerSystem(), order: 0);
			Systems.Add(new SpawnSphereSystem(), order: 0);
			Systems.Add(new SpawnDummySystem(), order: 0);
			Systems.Add(new DamageSystem(), order: 0);
			Systems.Add(new DummyRespawnSystem(), order: 0);
			Systems.Add(new DeathSystem(), order: 1);
			Systems.Add(new PlayerMoverSystem(), order: 2);
			Systems.Add(new ShootSystem(), order: 3);
			Systems.Add(new ShapeProxySystem(), order: 4);
			Systems.Add(new ContactSystem(), order: 5);
			Systems.Add(new DummyPatrolSystem(), order: 6);
			Systems.Add(new ContactSolverSystem(), order: 7);
			Systems.Add(new BodyTransformSyncSystem(), order: 8);
			Systems.Add(new ProjectileHitSystem(), order: 9);
		}
	}
}
