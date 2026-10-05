using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed64;
using Shenanicode.Rollback;
using F32 = Fixed32;

namespace Space.GameCore;

/// <summary>Semantic colors for <see cref="INavDebugDraw"/>; the renderer picks the actual palette.</summary>
public enum NavDebugColor : byte {
	/// <summary>Passable triangle of the default area (bit 0).</summary>
	WalkableArea,
	/// <summary>Passable triangle of any other area.</summary>
	OtherArea,
	/// <summary>Passable triangle whose A* cost multiplier is not one.</summary>
	CostlyArea,
	BlockedArea,
	/// <summary>Edge shared by two triangles; agents cross it through a portal.</summary>
	InteriorEdge,
	/// <summary>Edge with no neighbor: the mesh ends here and no corridor can cross it.</summary>
	BoundaryEdge,
	/// <summary>Collider the bake treated as walkable ground.</summary>
	WalkableSource,
	/// <summary>Collider the bake rasterized as solid but never stood on.</summary>
	ObstacleSource,
	/// <summary>Footprint of an obstacle grown by the baked agent radius: the ground Recast eroded around it.</summary>
	ObstacleClearance,
	/// <summary>Collider the bake ignored.</summary>
	ExcludedSource,
	/// <summary>Triangles of an agent's corridor still ahead of it.</summary>
	Corridor,
	/// <summary>Outline of the triangle an agent was located in.</summary>
	CurrentTriangle,
	/// <summary>String-pulled path through the corridor, from the agent to its path target.</summary>
	Path,
	/// <summary>Funnel corner on the path.</summary>
	Corner,
	/// <summary>Segment from the agent to the corner it steers at this tick.</summary>
	Steering,
	/// <summary>Requested destination.</summary>
	Destination,
	/// <summary>Where the corridor ends: the snapped destination, or a truncated corridor's last centroid.</summary>
	PathTarget,
	/// <summary>Segment from an agent whose last plan failed to the destination it could not reach.</summary>
	FailedPath,
	/// <summary>Volume of a switchable navigation zone that is open.</summary>
	OpenZone,
	/// <summary>Volume of a switchable navigation zone that is blocked.</summary>
	BlockedZone,
}

/// <summary>What <c>NavDebugDraw.Draw</c> emits.</summary>
[Flags]
public enum NavDebugDrawFlags {
	None = 0,
	/// <summary>Filled triangles colored by area classification.</summary>
	Triangles = 1 << 0,
	/// <summary>Interior and boundary edges.</summary>
	Edges = 1 << 1,
	/// <summary>
	/// The level's static geometry by <see cref="NavContribution"/>, each obstacle's clearance footprint, and
	/// each navigation zone's volume by its state.
	/// </summary>
	Sources = 1 << 2,
	/// <summary>Corridor, path, corners, destination, current triangle, and status of each agent.</summary>
	Agents = 1 << 3,
	Default = Triangles | Edges | Sources | Agents,
}

/// <summary>
/// Renderer callbacks for navigation debug drawing. Everything is in world space with Y up.
/// Implementations convert to engine types.
/// </summary>
public interface INavDebugDraw {
	void DrawTriangle(FVector3 a, FVector3 b, FVector3 c, NavDebugColor color);
	void DrawSegment(FVector3 start, FVector3 end, NavDebugColor color);
	void DrawPoint(FVector3 point, NavDebugColor color);

	/// <summary>
	/// One agent's state, for a label. <paramref name="index"/> counts agents in query order, the
	/// numbering <c>NavDebugDraw.Draw</c>'s selection uses.
	/// </summary>
	void DrawAgent(int index, FVector3 feet, in NavAgent agent);
}

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	/// <summary>
	/// Reports the world's navmesh, the level's navigation sources, and every <see cref="NavAgent"/>'s
	/// path to an <see cref="INavDebugDraw"/>. Read-only: it never writes ECS state, the mesh, or the
	/// world's <see cref="NavigationRes"/> scratch, and uses its own query and funnel instead, so
	/// drawing between ticks cannot change what the simulation computes.
	/// </summary>
	public sealed class NavDebugDraw {
		private readonly IReadOnlyList<NavSourceBox> _sources;
		private readonly IReadOnlyList<NavZoneVolume> _zones;
		private readonly FP _agentRadius;
		private NavMesh? _mesh;
		private NavMeshQuery? _query;
		private NavFunnel? _funnel;
		private FVector3[] _waypoints = [];

		/// <param name="level">The level the world was set up from; supplies the static geometry and bake settings.</param>
		public NavDebugDraw(LevelData level) {
			ArgumentNullException.ThrowIfNull(level);
			_sources = level.NavigationSources;
			_zones = level.NavZones;
			_agentRadius = level.Navigation?.Settings.AgentRadius ?? FP.Zero;
		}

		/// <summary>Agents found by the last <see cref="Draw"/>.</summary>
		public int AgentCount { get; private set; }

		/// <summary>The world's mesh as of the last <see cref="Draw"/>, or null for a level without navigation.</summary>
		public NavMesh? Mesh => _mesh;

		/// <param name="selectedAgent">Agent index to draw, or -1 for every agent.</param>
		public void Draw(INavDebugDraw draw, NavDebugDrawFlags flags = NavDebugDrawFlags.Default, int selectedAgent = -1) {
			AgentCount = 0;
			if (!W.IsWorldInitialized || !Systems.HasResource<NavigationRes>()) {
				return;
			}
			var navigation = Systems.GetResource<NavigationRes>();
			if ((flags & NavDebugDrawFlags.Sources) != 0) {
				DrawSources(draw);
				DrawZones(draw, navigation);
			}

			if (!ReferenceEquals(navigation.Mesh, _mesh)) {
				Bind(navigation);
			}
			if (_mesh is null) {
				return;
			}
			if ((flags & NavDebugDrawFlags.Triangles) != 0) {
				DrawTriangles(draw, _mesh);
			}
			if ((flags & NavDebugDrawFlags.Edges) != 0) {
				DrawEdges(draw, _mesh);
			}
			if ((flags & NavDebugDrawFlags.Agents) != 0) {
				DrawAgents(draw, selectedAgent);
			}
		}

		private void Bind(NavigationRes navigation) {
			_mesh = navigation.Mesh;
			if (_mesh is null) {
				_query = null;
				_funnel = null;
				return;
			}
			_query = new NavMeshQuery(_mesh);
			_funnel = new NavFunnel(_mesh, navigation.Config);
			_waypoints = new FVector3[navigation.Config.MaxPortals + 1];
		}

		private static void DrawTriangles(INavDebugDraw draw, NavMesh mesh) {
			var vertices = mesh.Vertices;
			var triangles = mesh.Triangles;
			var areas = mesh.Areas;
			for (var i = 0; i < triangles.Length; i++) {
				ref readonly var triangle = ref triangles[i];
				draw.DrawTriangle(vertices[triangle.V0], vertices[triangle.V1], vertices[triangle.V2], AreaColor(areas[i]));
			}
		}

		private static NavDebugColor AreaColor(in NavTriangleArea area) {
			if (area.IsBlocked) {
				return NavDebugColor.BlockedArea;
			}
			if (area.CostMultiplier != FP.One) {
				return NavDebugColor.CostlyArea;
			}
			return area.AreaMask == 1 ? NavDebugColor.WalkableArea : NavDebugColor.OtherArea;
		}

		private static void DrawEdges(INavDebugDraw draw, NavMesh mesh) {
			var vertices = mesh.Vertices;
			var triangles = mesh.Triangles;
			for (var i = 0; i < triangles.Length; i++) {
				ref readonly var triangle = ref triangles[i];
				for (var edge = 0; edge < 3; edge++) {
					var neighbor = triangle.GetNeighbor(edge);
					// Shared edges once, from the lower-indexed side.
					if (neighbor >= 0 && neighbor < i) {
						continue;
					}
					triangle.GetEdgeVertices(edge, out var a, out var b);
					draw.DrawSegment(vertices[a], vertices[b], neighbor < 0 ? NavDebugColor.BoundaryEdge : NavDebugColor.InteriorEdge);
				}
			}
		}

		private void DrawSources(INavDebugDraw draw) {
			Span<FVector3> corners = stackalloc FVector3[8];
			for (var b = 0; b < _sources.Count; b++) {
				var box = _sources[b];
				var transform = new FWorldTransform(box.Transform.Position, F32.FQuaternion.Normalize(box.Transform.Rotation));
				BoxCorners(transform, box.HalfExtents, corners);
				var color = box.Navigation switch {
					NavContribution.Walkable => NavDebugColor.WalkableSource,
					NavContribution.ObstacleOnly => NavDebugColor.ObstacleSource,
					_ => NavDebugColor.ExcludedSource,
				};
				DrawBoxEdges(draw, corners, color);

				if (box.Navigation == NavContribution.ObstacleOnly && _agentRadius > FP.Zero) {
					// The obstacle's footprint grown by the agent radius, at its lowest corner: Recast
					// erodes walkable ground this far from solid spans (square corners, where the real
					// erosion is rounded).
					var radius = _agentRadius.To32();
					BoxCorners(transform, new F32.FVector3(box.HalfExtents.X + radius, box.HalfExtents.Y, box.HalfExtents.Z + radius), corners);
					var floor = corners[0].Y;
					foreach (var corner in corners) {
						floor = FP.Min(floor, corner.Y);
					}
					for (var i = 0; i < 8; i++) {
						corners[i] = new FVector3(corners[i].X, floor, corners[i].Z);
					}
					// Corners 0, 1, 5, 4 are the bottom face (bits: x, y, z); flattened, any face ring works.
					draw.DrawSegment(corners[0], corners[1], NavDebugColor.ObstacleClearance);
					draw.DrawSegment(corners[1], corners[5], NavDebugColor.ObstacleClearance);
					draw.DrawSegment(corners[5], corners[4], NavDebugColor.ObstacleClearance);
					draw.DrawSegment(corners[4], corners[0], NavDebugColor.ObstacleClearance);
				}
			}
		}

		/// <summary>Each zone volume, colored by the state the world's mesh was last given.</summary>
		private void DrawZones(INavDebugDraw draw, NavigationRes navigation) {
			Span<FVector3> corners = stackalloc FVector3[8];
			foreach (var zone in _zones) {
				var transform = new FWorldTransform(zone.Transform.Position, F32.FQuaternion.Normalize(zone.Transform.Rotation));
				BoxCorners(transform, zone.HalfExtents, corners);
				var index = navigation.FindZone(zone.Id);
				var blocked = index >= 0 && navigation.IsZoneBlocked(index);
				DrawBoxEdges(draw, corners, blocked ? NavDebugColor.BlockedZone : NavDebugColor.OpenZone);
			}
		}

		/// <summary>Corner <c>i</c> takes +X, +Y, +Z for bits 1, 2, 4 of <c>i</c>.</summary>
		private static void BoxCorners(FWorldTransform transform, F32.FVector3 halfExtents, Span<FVector3> corners) {
			for (var i = 0; i < 8; i++) {
				var local = new F32.FVector3(
					(i & 1) != 0 ? halfExtents.X : -halfExtents.X,
					(i & 2) != 0 ? halfExtents.Y : -halfExtents.Y,
					(i & 4) != 0 ? halfExtents.Z : -halfExtents.Z);
				var world = FWorldTransform.TransformPoint(transform, local);
				corners[i] = new FVector3(world.X, world.Y, world.Z);
			}
		}

		private static void DrawBoxEdges(INavDebugDraw draw, ReadOnlySpan<FVector3> corners, NavDebugColor color) {
			for (var i = 0; i < 8; i++) {
				for (var bit = 1; bit < 8; bit <<= 1) {
					if ((i & bit) == 0) {
						draw.DrawSegment(corners[i], corners[i | bit], color);
					}
				}
			}
		}

		private void DrawAgents(INavDebugDraw draw, int selectedAgent) {
			var index = 0;
			foreach (var entity in W.Query<All<NavAgent, Transform, Mover>>().Entities()) {
				if (selectedAgent < 0 || selectedAgent == index) {
					ref readonly var agent = ref entity.Read<NavAgent>();
					var feet = NavAgentSystem.Feet(entity.Read<Transform>(), entity.Read<Mover>());
					DrawAgent(draw, index, feet, agent);
				}
				index++;
			}
			AgentCount = index;
		}

		private void DrawAgent(INavDebugDraw draw, int index, FVector3 feet, in NavAgent agent) {
			var mesh = _mesh!;
			draw.DrawAgent(index, feet, agent);
			if (agent.HasDestination) {
				draw.DrawPoint(agent.Destination, NavDebugColor.Destination);
			}
			if (agent.CurrentTriangle >= 0 && agent.CurrentTriangle < mesh.TriangleCount) {
				DrawTriangleOutline(draw, mesh, agent.CurrentTriangle, NavDebugColor.CurrentTriangle);
			}
			if (agent.Status == NavAgentStatus.Failed && agent.HasDestination) {
				draw.DrawSegment(feet, agent.Destination, NavDebugColor.FailedPath);
				return;
			}
			if (agent.Status is not (NavAgentStatus.Moving or NavAgentStatus.Arrived) || agent.CorridorLength <= 0) {
				return;
			}

			ReadOnlySpan<int> corridor = agent.Corridor;
			var ahead = corridor[Math.Clamp(agent.CorridorIndex, 0, agent.CorridorLength - 1)..agent.CorridorLength];
			var vertices = mesh.Vertices;
			foreach (var triangle in ahead) {
				if ((uint)triangle >= (uint)mesh.TriangleCount) {
					return; // Not a corridor this mesh planned; nothing sensible to draw.
				}
				ref readonly var t = ref mesh.Triangles[triangle];
				draw.DrawTriangle(vertices[t.V0], vertices[t.V1], vertices[t.V2], NavDebugColor.Corridor);
			}
			draw.DrawPoint(agent.PathTarget, NavDebugColor.PathTarget);

			// The agent moved after the tick located it, so start the path where it stands now.
			var start = NavAgentSystem.Locate(_query!, feet, agent.AreaMask, agent.StartSnapDistance, out var located);
			var from = ahead.IndexOf(located);
			if (from > 0) {
				ahead = ahead[from..];
			}
			var count = _funnel!.FindPath(ahead, start, agent.PathTarget, _waypoints);
			for (var i = 1; i < count; i++) {
				draw.DrawSegment(_waypoints[i - 1], _waypoints[i], NavDebugColor.Path);
				if (i < count - 1) {
					draw.DrawPoint(_waypoints[i], NavDebugColor.Corner);
				}
			}
			if (agent.Status == NavAgentStatus.Moving) {
				// Match the system's buffer so target appending and near-corner pruning are identical.
				Span<FVector3> corner = stackalloc FVector3[3];
				if (_funnel.FindCorners(ahead, start, agent.PathTarget, corner) > 0) {
					draw.DrawSegment(feet, corner[0], NavDebugColor.Steering);
				}
			}
		}

		private static void DrawTriangleOutline(INavDebugDraw draw, NavMesh mesh, int index, NavDebugColor color) {
			ref readonly var triangle = ref mesh.Triangles[index];
			var vertices = mesh.Vertices;
			draw.DrawSegment(vertices[triangle.V0], vertices[triangle.V1], color);
			draw.DrawSegment(vertices[triangle.V1], vertices[triangle.V2], color);
			draw.DrawSegment(vertices[triangle.V2], vertices[triangle.V0], color);
		}
	}
}
