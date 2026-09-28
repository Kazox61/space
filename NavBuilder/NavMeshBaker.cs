using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using Fixed64;
using Space.GameCore;

namespace Space.NavBuilder;

/// <summary>What the Recast stage produced before the fixed-point build.</summary>
public readonly record struct NavMeshBakeReport(
	int SourceTriangles,
	int WalkableCandidates,
	int RecastPolygons,
	int RecastDetailTriangles,
	NavMeshBuildReport Build
);

public readonly record struct NavMeshBakeResult(NavMesh Mesh, NavMeshBakeReport Report) {
	/// <summary>One entry per input zone, ordinal by id, as <see cref="LevelNavigation.Zones"/> stores them.</summary>
	public NavZoneData[] Zones { get; init; } = [];
}

/// <summary>
/// Offline bake: rasterizes the level's static collider triangles with DotRecast's Recast stage,
/// then turns the resulting detail mesh into a fixed-point <see cref="NavMesh"/> with
/// <see cref="NavMeshBuilder"/>. Floats exist only between the two conversions in this class.
/// </summary>
public static class NavMeshBaker {
	/// <summary>Navigation area index given to Recast's default walkable area.</summary>
	public const int WalkableArea = 0;

	/// <summary>Recast area id of the first zone; zone <c>i</c> is marked with this plus <c>i</c>.</summary>
	private const int FirstZoneRecastArea = 1;

	public static NavMeshBakeResult Bake(IEnumerable<StaticBox> boxes, NavBakeSettings settings) {
		return Bake(StaticBoxTriangulator.Build(boxes), [], settings);
	}

	public static NavMeshBakeResult Bake(IEnumerable<StaticBox> boxes, IEnumerable<NavZoneVolume> zones, NavBakeSettings settings) {
		return Bake(StaticBoxTriangulator.Build(boxes), zones, settings);
	}

	/// <summary>
	/// The level with its navigation baked from its static boxes and navigation zones, replacing any
	/// it had. A level without a <see cref="NavContribution.Walkable"/> box gets no navigation and a
	/// null report.
	/// </summary>
	public static LevelData BakeLevel(LevelData level, NavBakeSettings settings, out NavMeshBakeReport? report) {
		ArgumentNullException.ThrowIfNull(level);
		if (!level.StaticBoxes.Any(static box => box.Navigation == NavContribution.Walkable)) {
			CollectZones(OrderZones(level.NavZones), []);
			report = null;
			return level.WithNavigation(null);
		}
		var result = Bake(level.StaticBoxes, level.NavZones, settings);
		report = result.Report;
		return level.WithNavigation(new LevelNavigation(settings, new NavMeshData(result.Mesh), result.Zones));
	}

	/// <remarks>
	/// <see cref="NavContribution.Walkable"/> triangles become walkable where their slope allows;
	/// <see cref="NavContribution.ObstacleOnly"/> triangles are rasterized as solid, non-walkable
	/// geometry, so they block and cut clearance but are never stood on. Each zone marks the eroded
	/// walkable surface inside its volume with its own Recast area, so regions, polygons, and
	/// therefore navmesh triangles never straddle a zone's boundary. Zones must not overlap; where
	/// they do, the one with the later id owns the overlap.
	/// </remarks>
	public static NavMeshBakeResult Bake(NavTriangleSoup soup, IEnumerable<NavZoneVolume> zones, NavBakeSettings settings) {
		ArgumentNullException.ThrowIfNull(soup);
		ArgumentNullException.ThrowIfNull(zones);
		settings.Validate();
		var orderedZones = OrderZones(zones);
		if (soup.Indices.Count == 0) {
			throw new InvalidDataException("Navmesh bake has no source geometry.");
		}

		var vertices = ToFloats(soup.Vertices);
		var triangles = soup.Indices.ToArray();
		var triangleCount = triangles.Length / 3;
		var config = CreateConfig(settings);
		var context = new RcContext();

		var areas = RcRecast.MarkWalkableTriangles(context, config.WalkableSlopeAngle, vertices, triangles, triangleCount, config.WalkableAreaMod);
		var walkableCandidates = 0;
		for (var t = 0; t < triangleCount; t++) {
			if (soup.TriangleContributions[t] != NavContribution.Walkable) {
				areas[t] = RcRecast.RC_NULL_AREA;
			} else if (areas[t] != RcRecast.RC_NULL_AREA) {
				walkableCandidates++;
			}
		}
		if (walkableCandidates == 0) {
			throw new InvalidDataException("Navmesh bake has no walkable source triangle within the slope limit.");
		}

		var heightfield = CreateHeightfield(vertices, config);
		RcRasterizations.RasterizeTriangles(context, vertices, triangles, areas, triangleCount, heightfield, config.WalkableClimb);
		var result = new RcBuilder().Build(context, 0, 0, new ZoneVolumes(orderedZones), config, heightfield, false);

		var polyMesh = result.Mesh;
		var detail = result.MeshDetail;
		if (polyMesh.npolys == 0 || detail is null || detail.ntris == 0) {
			throw new InvalidDataException("Recast produced no walkable polygons; check agent size and source geometry.");
		}

		ExtractDetailMesh(polyMesh, detail, orderedZones.Length, out var navVertices, out var navIndices, out var navAreas, out var navZones);
		var build = NavMeshBuilder.Build(navVertices, navIndices, navAreas, navZones, settings.LookupCellSize);
		var report = new NavMeshBakeReport(triangleCount, walkableCandidates, polyMesh.npolys, detail.ntris, build.Report);
		return new NavMeshBakeResult(build.Mesh, report) { Zones = CollectZones(orderedZones, build.TriangleZones) };
	}

	private static NavZoneVolume[] OrderZones(IEnumerable<NavZoneVolume> zones) {
		var ordered = zones.OrderBy(static zone => zone.Id, StringComparer.Ordinal).ToArray();
		if (ordered.Length > NavZoneData.MaxZones) {
			throw new InvalidDataException($"Navmesh bake has {ordered.Length} navigation zones; at most {NavZoneData.MaxZones} are supported.");
		}
		for (var i = 0; i < ordered.Length; i++) {
			ordered[i].Validate();
			if (i > 0 && StringComparer.Ordinal.Equals(ordered[i - 1].Id, ordered[i].Id)) {
				throw new InvalidDataException($"Navigation zone id '{ordered[i].Id}' is duplicated.");
			}
		}
		return ordered;
	}

	private static NavZoneData[] CollectZones(NavZoneVolume[] zones, int[] triangleZones) {
		var result = new NavZoneData[zones.Length];
		for (var z = 0; z < zones.Length; z++) {
			var triangles = new List<int>();
			for (var t = 0; t < triangleZones.Length; t++) {
				if (triangleZones[t] == z) {
					triangles.Add(t);
				}
			}
			if (triangles.Count == 0) {
				throw new InvalidDataException($"Navigation zone '{zones[z].Id}' covers no walkable navmesh; its volume must enclose walkable ground.");
			}
			result[z] = new NavZoneData(zones[z].Id, triangles);
		}
		return result;
	}

	private static RcConfig CreateConfig(NavBakeSettings settings) {
		return new RcConfig(
			RcPartition.WATERSHED,
			settings.VoxelSize.ToFloat(), settings.VoxelHeight.ToFloat(),
			settings.AgentMaxSlopeDegrees.ToFloat(), settings.AgentHeight.ToFloat(), settings.AgentRadius.ToFloat(), settings.AgentMaxClimb.ToFloat(),
			settings.RegionMinSize, settings.RegionMergeSize,
			settings.EdgeMaxLength.ToFloat(), settings.EdgeMaxError.ToFloat(),
			6,
			settings.DetailSampleDistance.ToFloat(), settings.DetailSampleMaxError.ToFloat(),
			filterLowHangingObstacles: true, filterLedgeSpans: true, filterWalkableLowHeightSpans: true,
			new RcAreaModification(RcRecast.RC_WALKABLE_AREA), buildMeshDetail: true
		);
	}

	private static RcHeightfield CreateHeightfield(float[] vertices, RcConfig config) {
		var min = new RcVec3f(float.MaxValue, float.MaxValue, float.MaxValue);
		var max = new RcVec3f(float.MinValue, float.MinValue, float.MinValue);
		for (var i = 0; i < vertices.Length; i += 3) {
			var v = new RcVec3f(vertices[i], vertices[i + 1], vertices[i + 2]);
			min = RcVec3f.Min(min, v);
			max = RcVec3f.Max(max, v);
		}
		RcRecast.CalcGridSize(min, max, config.Cs, out var width, out var height);
		return new RcHeightfield(width, height, min, max, config.Cs, config.Ch, config.BorderSize);
	}

	/// <summary>
	/// Flattens Recast's per-polygon detail sub-meshes into one triangle soup. Every triangle gets
	/// the walkable area and the zone its polygon's Recast area stands for. Vertices shared between
	/// sub-meshes are duplicated here and welded again by <see cref="NavMeshBuilder"/>.
	/// </summary>
	private static void ExtractDetailMesh(RcPolyMesh polyMesh, RcPolyMeshDetail detail, int zoneCount, out FVector3[] vertices, out int[] indices, out int[] areas, out int[] zones) {
		vertices = new FVector3[detail.nverts];
		for (var i = 0; i < detail.nverts; i++) {
			vertices[i] = new FVector3(ToFixed(detail.verts[i * 3]), ToFixed(detail.verts[i * 3 + 1]), ToFixed(detail.verts[i * 3 + 2]));
		}

		indices = new int[detail.ntris * 3];
		areas = new int[detail.ntris];
		zones = new int[detail.ntris];
		for (var mesh = 0; mesh < detail.nmeshes; mesh++) {
			var vertexBase = detail.meshes[mesh * 4];
			var triangleBase = detail.meshes[mesh * 4 + 2];
			var triangleCount = detail.meshes[mesh * 4 + 3];
			var zone = ToZone(polyMesh.areas[mesh], zoneCount);
			for (var t = triangleBase; t < triangleBase + triangleCount; t++) {
				indices[t * 3] = vertexBase + detail.tris[t * 4];
				indices[t * 3 + 1] = vertexBase + detail.tris[t * 4 + 1];
				indices[t * 3 + 2] = vertexBase + detail.tris[t * 4 + 2];
				areas[t] = WalkableArea;
				zones[t] = zone;
			}
		}
	}

	/// <summary>The zone a Recast polygon area stands for, or -1 for plain walkable ground.</summary>
	private static int ToZone(int recastArea, int zoneCount) {
		if (recastArea == RcRecast.RC_WALKABLE_AREA) {
			return -1;
		}
		var zone = recastArea - FirstZoneRecastArea;
		return zone >= 0 && zone < zoneCount
			? zone
			: throw new InvalidDataException($"Recast polygon has unmapped area {recastArea}.");
	}

	/// <summary>
	/// A zone's volume as a Recast convex volume: the XZ convex hull of its eight corners, from its
	/// lowest to its highest corner. Exact for boxes rotated only about Y; a tilted box marks its
	/// upright bounding prism.
	/// </summary>
	private static RcConvexVolume ToConvexVolume(in NavZoneVolume zone, int index) {
		var transform = zone.Transform;
		transform.Rotation = Fixed32.FQuaternion.Normalize(transform.Rotation);
		var h = zone.HalfExtents;
		var corners = new FVector3[8];
		for (var i = 0; i < 8; i++) {
			var local = new Fixed32.FVector3((i & 1) != 0 ? h.X : -h.X, (i & 2) != 0 ? h.Y : -h.Y, (i & 4) != 0 ? h.Z : -h.Z);
			var world = Fixed.FWorldTransform.TransformPoint(transform, local);
			corners[i] = new FVector3(world.X, world.Y, world.Z);
		}
		var minY = corners.Min(static c => c.Y);
		var maxY = corners.Max(static c => c.Y);
		var hull = ConvexHullXZ(corners);
		var verts = new float[hull.Count * 3];
		for (var i = 0; i < hull.Count; i++) {
			verts[i * 3] = hull[i].X.ToFloat();
			verts[i * 3 + 1] = minY.ToFloat();
			verts[i * 3 + 2] = hull[i].Z.ToFloat();
		}
		return new RcConvexVolume {
			verts = verts,
			hmin = minY.ToFloat(),
			hmax = maxY.ToFloat(),
			areaMod = new RcAreaModification(FirstZoneRecastArea + index),
		};
	}

	/// <summary>Andrew's monotone chain on exact fixed-point XZ, counter-clockwise, collinear points dropped.</summary>
	private static List<FVector3> ConvexHullXZ(FVector3[] points) {
		var sorted = points
			.OrderBy(static p => p.X.RawValue)
			.ThenBy(static p => p.Z.RawValue)
			.ToArray();
		var hull = new List<FVector3>(sorted.Length * 2);
		for (var pass = 0; pass < 2; pass++) {
			var start = hull.Count;
			foreach (var p in sorted) {
				while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= FP.Zero) {
					hull.RemoveAt(hull.Count - 1);
				}
				hull.Add(p);
			}
			hull.RemoveAt(hull.Count - 1);
			Array.Reverse(sorted);
		}
		return hull;

		static FP Cross(FVector3 o, FVector3 a, FVector3 b) {
			return (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);
		}
	}

	/// <summary>
	/// Hands the zone volumes to <see cref="RcBuilder"/>, which marks them after eroding the walkable
	/// area. The builder reads nothing else from its geometry provider when given a heightfield.
	/// </summary>
	private sealed class ZoneVolumes : IRcInputGeomProvider {
		private readonly List<RcConvexVolume> _volumes;

		public ZoneVolumes(NavZoneVolume[] zones) {
			_volumes = new List<RcConvexVolume>(zones.Length);
			for (var i = 0; i < zones.Length; i++) {
				_volumes.Add(ToConvexVolume(zones[i], i));
			}
		}

		public IList<RcConvexVolume> ConvexVolumes() => _volumes;

		public void AddConvexVolume(RcConvexVolume convexVolume) => throw new NotSupportedException();
		public RcTriMesh GetMesh() => throw new NotSupportedException();
		public RcVec3f GetMeshBoundsMin() => throw new NotSupportedException();
		public RcVec3f GetMeshBoundsMax() => throw new NotSupportedException();
		public IEnumerable<RcTriMesh> Meshes() => throw new NotSupportedException();
		public List<RcOffMeshConnection> GetOffMeshConnections() => throw new NotSupportedException();
		public void AddOffMeshConnection(RcVec3f start, RcVec3f end, float radius, bool bidir, int area, int flags) => throw new NotSupportedException();
		public void RemoveOffMeshConnections(Predicate<RcOffMeshConnection> filter) => throw new NotSupportedException();
	}

	private static float[] ToFloats(IReadOnlyList<FVector3> vertices) {
		var floats = new float[vertices.Count * 3];
		for (var i = 0; i < vertices.Count; i++) {
			floats[i * 3] = vertices[i].X.ToFloat();
			floats[i * 3 + 1] = vertices[i].Y.ToFloat();
			floats[i * 3 + 2] = vertices[i].Z.ToFloat();
		}
		return floats;
	}

	/// <summary>Exact: every float is representable in double, and scaling by 2^31 is exact.</summary>
	private static FP ToFixed(float value) {
		return FP.FromRaw(checked((long)Math.Round((double)value * FP.OneRaw)));
	}
}
