using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Spawns one <see cref="NavCharacter"/> that chases the nearest player, on levels that ship a
	/// navmesh. Runs once at initialization on client and server alike, like
	/// <see cref="SpawnDummySystem"/>. Snaps the configured spawn to nearby passable ground and skips
	/// spawning when none is within the level's snap distance.
	/// </summary>
	public struct SpawnNavCharacterSystem : ISystem {
		public void Init() {
			var navigation = Systems.GetResource<NavigationRes>();
			if (!navigation.HasMesh) {
				return;
			}

			var res = Systems.GetResource<NavCharacterRes>();
			var snapDistance = SnapDistance(navigation);
			var agent = NavAgent.Create(res.MoveSpeed, res.ArrivalRadius, snapDistance, res.RepathIntervalTicks);
			// NavCharacter's standing capsule extends one unit below its center.
			var feetY = res.SpawnPosition.Y - Fixed64.FP.One;
			var query = navigation.Query!;
			var xz = query.ProjectToPassable(NavGeometry.ToXZ(res.SpawnPosition), feetY, snapDistance, agent.AreaMask, out var triangle);
			if (triangle < 0) {
				return;
			}
			var position = new Fixed64.FVector3(xz.X, query.SampleHeight(xz, triangle) + Fixed64.FP.One, xz.Y);
			var character = W.NewEntity<NavCharacter>();
			character.Set(new Transform { Position = position, Rotation = Fixed64.FQuaternion.Identity });
			character.Set(agent);
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
