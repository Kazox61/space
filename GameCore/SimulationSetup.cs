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
			Systems.SetResource(new PhysicsWorld());
			Systems.SetResource(new BroadPhase());
			Systems.Add(new SpawnSystem(), order: 0);
			Systems.Add(new DamageSystem(), order: 0);
			Systems.Add(new DeathSystem(), order: 1);
			Systems.Add(new MovementSystem(), order: 2);
			Systems.Add(new ProjectileMovementSystem(), order: 3);
			Systems.Add(new ShapeProxySystem(), order: 4);
			Systems.Add(new ContactSystem(), order: 5);
			Systems.Add(new ContactSolverSystem(), order: 6);
		}
	}
}
