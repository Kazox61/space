using FFS.Libraries.StaticEcs;
using Fixed64;

namespace Space.GameCore;

/// <summary>
/// The world's navigation runtime. Not snapshotted: the mesh geometry never changes, and its
/// <see cref="NavMesh.Areas"/> are derived state that must be rewritten from ECS state before use.
/// Each world gets its own mesh and scratch buffers, so worlds in one process never share them.
/// </summary>
public sealed class NavigationRes : IResource {
	private readonly NavTriangleArea[] _bakedAreas = [];
	private readonly bool[] _zoneBlocked = [];
	private readonly FP[] _zoneCost = [];

	public NavMesh? Mesh { get; }
	public NavMeshQuery? Query { get; }
	public NavPathfinder? Pathfinder { get; }
	public NavFunnel? Funnel { get; }
	public NavConfig Config { get; }

	/// <summary>The level's switchable zones; empty without navigation.</summary>
	public IReadOnlyList<NavZoneData> Zones { get; } = [];

	/// <summary>
	/// Digest of every zone's state as last written by <see cref="ApplyZones"/>. Derived state:
	/// agents compare it with the value they planned under and re-plan when it changed.
	/// </summary>
	public ulong ZoneSignature { get; private set; }

	public NavigationRes(LevelNavigation? navigation, NavConfig config) {
		Config = config;
		if (navigation is null) {
			return;
		}
		Mesh = navigation.Mesh.CreateMesh();
		Query = new NavMeshQuery(Mesh);
		Pathfinder = new NavPathfinder(Query, config);
		Funnel = new NavFunnel(Mesh, config);
		Zones = navigation.Zones;
		_bakedAreas = navigation.Mesh.Areas.ToArray();
		_zoneBlocked = new bool[Zones.Count];
		_zoneCost = new FP[Zones.Count];
		ResetZones();
		ApplyZones();
	}

	public bool HasMesh => Mesh is not null;

	/// <summary>Index of the zone with <paramref name="id"/> (the <see cref="NavZoneState.Zone"/> to use), or -1.</summary>
	public int FindZone(string id) {
		for (var i = 0; i < Zones.Count; i++) {
			if (StringComparer.Ordinal.Equals(Zones[i].Id, id)) {
				return i;
			}
		}
		return -1;
	}

	/// <summary>Whether zone <paramref name="zone"/> was blocked by the last <see cref="ApplyZones"/>.</summary>
	public bool IsZoneBlocked(int zone) => (uint)zone < (uint)_zoneBlocked.Length && _zoneBlocked[zone];

	/// <summary>Cost multiplier zone <paramref name="zone"/> got from the last <see cref="ApplyZones"/>.</summary>
	public FP ZoneCostMultiplier(int zone) => (uint)zone < (uint)_zoneCost.Length ? _zoneCost[zone] : FP.One;

	/// <summary>Starts a new <see cref="ApplyZones"/> pass: every zone open at its baked cost.</summary>
	public void ResetZones() {
		Array.Fill(_zoneBlocked, false);
		Array.Fill(_zoneCost, FP.One);
	}

	/// <summary>
	/// Merges one <see cref="NavZoneState"/> into the pending pass. Several states for one zone
	/// combine without depending on their order: any blocked blocks, the highest cost wins.
	/// Out-of-range zones are ignored.
	/// </summary>
	public void AddZoneState(in NavZoneState state) {
		if ((uint)state.Zone >= (uint)_zoneBlocked.Length) {
			return;
		}
		_zoneBlocked[state.Zone] |= state.Blocked;
		var cost = FP.Max(state.CostMultiplier, FP.One);
		if (cost > _zoneCost[state.Zone]) {
			_zoneCost[state.Zone] = cost;
		}
	}

	/// <summary>
	/// Rewrites every zone triangle's area from its baked value and the pending zone states, and
	/// updates <see cref="ZoneSignature"/>. Triangles outside zones are never written.
	/// </summary>
	public void ApplyZones() {
		if (Mesh is null) {
			return;
		}
		var areas = Mesh.Areas;
		var signature = 14695981039346656037UL;
		for (var z = 0; z < Zones.Count; z++) {
			var blocked = _zoneBlocked[z];
			var cost = _zoneCost[z];
			foreach (var triangle in Zones[z].Triangles) {
				var area = _bakedAreas[triangle];
				area.IsBlocked |= blocked;
				area.CostMultiplier *= cost;
				areas[triangle] = area;
			}
			signature = unchecked((signature ^ (blocked ? 1UL : 0UL)) * 1099511628211UL);
			signature = unchecked((signature ^ (ulong)cost.RawValue) * 1099511628211UL);
		}
		ZoneSignature = signature;
	}
}
