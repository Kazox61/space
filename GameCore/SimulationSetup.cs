using FFS.Libraries.StaticEcs;
using Fixed64;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public static class SimulationSetup {
		public static void Register() {
			Const.DeltaTime = FP.One / S.TickRate;
			Const.InvDeltaTime = S.TickRate.ToFP();

			Systems.SetResource(new CharacterRes());
			Systems.SetResource(new PlanetRes());
			Systems.SetResource(new DummyRes());
			Systems.SetResource(new NavCharacterRes());
			// World-scoped (not Systems.SetResource): GameWorldRollback/GameWorldFullSyncHandler both
			// snapshot via W.Serializer, which only walks World<TWorld>'s own resource registry --
			// Systems<TSystemsType> keeps a completely separate one that never gets serialized here.
			W.SetResource(new PhysicsWorld());
			W.SetResource(new BroadPhase());
			Systems.Add(new SpawnPlayerSystem(), order: 0);
			Systems.Add(new SpawnSphereSystem(), order: 0);
			Systems.Add(new SpawnDummySystem(), order: 0);
			Systems.Add(new SpawnNavCharacterSystem(), order: 0);
			Systems.Add(new DamageSystem(), order: 0);
			Systems.Add(new DummyRespawnSystem(), order: 0);
			Systems.Add(new DeathSystem(), order: 1);
			// Controllers write CharacterMoveIntent; CharacterMoverSystem executes it.
			Systems.Add(new PlayerIntentSystem(), order: 2);
			Systems.Add(new NavChaseSystem(), order: 3);
			Systems.Add(new DoorSystem(), order: 3);
			// Rail velocity and linked floor availability must be final before planning and movers.
			Systems.Add(new RailMotionSystem(), order: 3);
			// Zone states are final for the tick once gameplay above has run; agents plan against them.
			Systems.Add(new NavZoneApplySystem(), order: 4);
			Systems.Add(new NavAgentSystem(), order: 5);
			Systems.Add(new CharacterMoverSystem(), order: 6);
			// Proxies follow the characters' final positions, before shape proxies and contacts update.
			Systems.Add(new SensorProxySystem(), order: 7);
			Systems.Add(new ShootSystem(), order: 7);
			Systems.Add(new ProjectileRangeSystem(), order: 8);
			Systems.Add(new ShapeProxySystem(), order: 9);
			Systems.Add(new ContactSystem(), order: 10);
			// Sensor events from ContactSystem this tick; toggled doors move from the next tick on.
			Systems.Add(new PressurePlateSystem(), order: 11);
			Systems.Add(new ContactSolverSystem(), order: 12);
			Systems.Add(new BodyTransformSyncSystem(), order: 13);
			Systems.Add(new ProjectileHitSystem(), order: 14);
		}
	}
}
