using Shenanicode.Rollback;
using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public static class SimulationSetup {
		public static void Register() {
			Const.DeltaTime = FP.One / S.TickRate;
			Const.InvDeltaTime = S.TickRate.ToFP();

			// W.SetResource(new BroadPhase(200, 100, FVector2.One));

			// Systems.Add(new MovementIntegrationSystem());
			// Systems.Add(new ColliderWorldPositionSyncSystem());
			// Systems.Add(new BroadPhaseSystem());
			// Systems.Add(new DestroySelfSystem());
			//
			//

			Systems.SetResource(new PhysicsRes());
			Systems.SetResource(new CharacterRes());
			Systems.SetResource(new PlanetRes());
			Systems.Add(new SpawnSystem(), order: 0);
			Systems.Add(new DamageSystem(), order: 0);
			Systems.Add(new DeathSystem(), order: 1);
			Systems.Add(new MovementSystem(), order: 2);
			Systems.Add(new ProjectileMovementSystem(), order: 3);
		}
	}
}
