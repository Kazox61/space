using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Spawns one <see cref="NavCharacter"/> that chases the nearest player, on levels that ship a
	/// navmesh. Runs once at initialization on client and server alike, like
	/// <see cref="SpawnDummySystem"/>.
	/// </summary>
	public struct SpawnNavCharacterSystem : ISystem {
		public void Init() {
			var navigation = Systems.GetResource<NavigationRes>();
			if (!navigation.HasMesh) {
				return;
			}

			var res = Systems.GetResource<NavCharacterRes>();
			var character = W.NewEntity<NavCharacter>();
			character.Set(new Transform { Position = res.SpawnPosition, Rotation = Fixed64.FQuaternion.Identity });
			character.Set(NavAgent.Create(res.MoveSpeed, res.ArrivalRadius, SnapDistance(navigation), res.RepathIntervalTicks));
			character.Set<ChasesNearestPlayer>();
		}

		/// <summary>
		/// How far a player standing somewhere unwalkable (on top of an obstacle) may be from the
		/// ground the character approaches instead: the level's lookup cell size, the reach the
		/// nearest-point search is guaranteed to cover. A player further inside an obstacle leaves the
		/// character standing still.
		/// </summary>
		public static Fixed64.FP SnapDistance(NavigationRes navigation) => navigation.Mesh!.GridCellSize;
	}
}
