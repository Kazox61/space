using Fixed64;

namespace Space.GameCore;

/// <summary>
/// A level's baked navmesh as immutable data. Worlds never share it directly: each takes its own
/// <see cref="NavMesh"/> from <see cref="CreateMesh"/>, whose runtime areas start from these.
/// </summary>
public sealed class NavMeshData {
	private readonly NavMesh _template;

	public NavMeshData(NavMesh mesh) {
		ArgumentNullException.ThrowIfNull(mesh);
		_template = mesh.CopyWithOwnAreas();
	}

	public ReadOnlySpan<FVector3> Vertices => _template.Vertices;
	public ReadOnlySpan<NavTriangle> Triangles => _template.Triangles;
	public ReadOnlySpan<NavTriangleArea> Areas => _template.Areas;
	public ReadOnlySpan<int> GridCells => _template.GridCells;
	public ReadOnlySpan<int> GridTriangles => _template.GridTriangles;
	public FAABB2 BoundsXZ => _template.BoundsXZ;
	public int GridWidth => _template.GridWidth;
	public int GridHeight => _template.GridHeight;
	public FP GridCellSize => _template.GridCellSize;
	public FVector2 GridOrigin => _template.GridOrigin;
	public int TriangleCount => _template.TriangleCount;

	/// <summary>A new runtime mesh over this data with its own, freshly initialized areas.</summary>
	public NavMesh CreateMesh() {
		return _template.CopyWithOwnAreas();
	}
}

/// <summary>
/// The navmesh triangles one <see cref="NavZoneVolume"/> covers. The bake splits the mesh along the
/// volume, so switching these triangles off closes exactly the zone.
/// </summary>
public sealed class NavZoneData {
	/// <summary>Most zones a level may have. Recast marks each with its own area id below its walkable one.</summary>
	public const int MaxZones = 62;

	public const int MaxIdLength = 64;

	private readonly int[] _triangles;

	/// <param name="triangles">Strictly ascending triangle indices; at least one.</param>
	public NavZoneData(string id, IEnumerable<int> triangles) {
		ArgumentNullException.ThrowIfNull(id);
		ArgumentNullException.ThrowIfNull(triangles);
		if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength) {
			throw new ArgumentException($"Zone id '{id}' must be non-empty and at most {MaxIdLength} characters.", nameof(id));
		}
		_triangles = triangles.ToArray();
		if (_triangles.Length == 0) {
			throw new ArgumentException($"Zone '{id}' covers no triangle.", nameof(triangles));
		}
		for (var i = 0; i < _triangles.Length; i++) {
			if (_triangles[i] < 0 || (i > 0 && _triangles[i] <= _triangles[i - 1])) {
				throw new ArgumentException($"Zone '{id}' triangles must be non-negative and strictly ascending.", nameof(triangles));
			}
		}
		Id = id;
	}

	/// <summary>Matches the <see cref="NavZoneVolume.Id"/> it was baked from.</summary>
	public string Id { get; }

	public ReadOnlySpan<int> Triangles => _triangles;
}

/// <summary>
/// A level's navigation: the baked mesh, the settings it was baked with, and its switchable zones,
/// ordered like <see cref="LevelData.NavZones"/> (ordinal by id). A zone's index in
/// <see cref="Zones"/> is the one <see cref="NavZoneState.Zone"/> refers to.
/// </summary>
public sealed class LevelNavigation {
	public LevelNavigation(NavBakeSettings settings, NavMeshData mesh, IEnumerable<NavZoneData>? zones = null) {
		ArgumentNullException.ThrowIfNull(mesh);
		settings.Validate();
		Settings = settings;
		Mesh = mesh;
		Zones = zones?.ToArray() ?? [];
		Validate();
	}

	public NavBakeSettings Settings { get; }
	public NavMeshData Mesh { get; }
	public IReadOnlyList<NavZoneData> Zones { get; }

	private void Validate() {
		if (Zones.Count > NavZoneData.MaxZones) {
			throw new ArgumentException($"Level navigation has {Zones.Count} zones; at most {NavZoneData.MaxZones} are supported.");
		}
		var owner = new int[Mesh.TriangleCount];
		for (var z = 0; z < Zones.Count; z++) {
			var zone = Zones[z];
			if (z > 0 && StringComparer.Ordinal.Compare(Zones[z - 1].Id, zone.Id) >= 0) {
				throw new ArgumentException($"Level navigation zone ids are duplicated or not ordinally ordered at '{zone.Id}'.");
			}
			foreach (var triangle in zone.Triangles) {
				if (triangle >= owner.Length) {
					throw new ArgumentException($"Zone '{zone.Id}' references triangle {triangle} of {owner.Length}.");
				}
				if (owner[triangle] != 0) {
					throw new ArgumentException($"Triangle {triangle} belongs to zones '{Zones[owner[triangle] - 1].Id}' and '{zone.Id}'.");
				}
				owner[triangle] = z + 1;
			}
		}
	}
}
