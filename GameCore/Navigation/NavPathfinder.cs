// Derived from xpTURN Klotho 0.14.1 (FPNavMeshPathfinder.cs), Apache-2.0.
// Modified for Space: Fixed64 math, caller-owned corridor buffer, status result instead of
// logging and diagnostic counters, explicit triangle-index tie-breaking, partial paths not ported,
// blocked start triangles escape like forbidden ones.
using Fixed64;

namespace Space.GameCore;

public enum NavPathStatus : byte {
	/// <summary>The whole corridor fit into the buffer.</summary>
	Found,

	/// <summary>A route exists but was longer than the buffer; the start-side prefix was kept.</summary>
	FoundTruncated,

	StartOffMesh,
	EndOffMesh,

	/// <summary>The destination triangle is blocked.</summary>
	EndpointBlocked,

	/// <summary>The destination lies on ground the area mask forbids.</summary>
	EndAreaRejected,

	/// <summary>The search exhausted the reachable graph without reaching the destination.</summary>
	NoRoute,

	/// <summary>The search ran out of <see cref="NavConfig.MaxIterations"/> with work still queued.</summary>
	IterationLimit,
}

public static class NavPathStatusExtensions {
	public static bool IsFound(this NavPathStatus status) {
		return status is NavPathStatus.Found or NavPathStatus.FoundTruncated;
	}
}

/// <summary>
/// A* over the triangle adjacency graph. Allocation-free after construction; scratch state is
/// reset per call, so results depend only on the mesh, its areas, and the arguments.
/// </summary>
public sealed class NavPathfinder {
	private readonly NavMesh _mesh;
	private readonly NavMeshQuery _query;
	private readonly NavConfig _config;

	private readonly NavBinaryHeap _open;
	private readonly FP[] _gScores;
	private readonly int[] _cameFrom;
	private readonly bool[] _closed;
	private readonly FVector2[] _entryPoints;
	private readonly int[] _nodeGeneration;
	private int _generation;

	public NavPathfinder(NavMeshQuery query, NavConfig config) {
		ArgumentNullException.ThrowIfNull(query);
		_query = query;
		_mesh = query.Mesh;
		_config = config;

		var count = _mesh.TriangleCount;
		_open = new NavBinaryHeap(count);
		_gScores = new FP[count];
		_cameFrom = new int[count];
		_closed = new bool[count];
		_entryPoints = new FVector2[count];
		_nodeGeneration = new int[count];
	}

	/// <summary>
	/// Finds a triangle corridor from <paramref name="start"/> to <paramref name="end"/> and writes
	/// it, start first, into <paramref name="corridor"/>.
	/// </summary>
	/// <remarks>
	/// The start triangle is exempt from <paramref name="areaMask"/> and from being blocked, and the
	/// search may cross forbidden or blocked ground only while it has not yet left the forbidden or
	/// blocked region it started in, so an agent standing somewhere it may not be (a zone that just
	/// closed around it) can still get a route out. The end is never exempt.
	/// </remarks>
	public NavPathStatus FindPath(FVector3 start, FVector3 end, int areaMask, Span<int> corridor, out int corridorLength) {
		if (corridor.IsEmpty) {
			throw new ArgumentException("Corridor buffer must not be empty.", nameof(corridor));
		}
		corridorLength = 0;

		var startTriangle = _query.FindTriangle(NavGeometry.ToXZ(start), start.Y);
		var endTriangle = _query.FindTriangleForEndpoint(NavGeometry.ToXZ(end), end.Y, areaMask);
		if (startTriangle < 0) {
			return NavPathStatus.StartOffMesh;
		}
		if (endTriangle < 0) {
			return NavPathStatus.EndOffMesh;
		}
		return FindPath(start, startTriangle, end, endTriangle, areaMask, corridor, out corridorLength);
	}

	/// <summary>
	/// <see cref="FindPath(FVector3, FVector3, int, Span{int}, out int)"/> from triangles the caller
	/// already resolved, for example with <see cref="NavMeshQuery.ClosestPoint"/>, so the endpoints
	/// are not looked up a second time.
	/// </summary>
	public NavPathStatus FindPath(FVector3 start, int startTriangle, FVector3 end, int endTriangle, int areaMask, Span<int> corridor, out int corridorLength) {
		if (corridor.IsEmpty) {
			throw new ArgumentException("Corridor buffer must not be empty.", nameof(corridor));
		}
		ArgumentOutOfRangeException.ThrowIfNegative(startTriangle);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(startTriangle, _mesh.TriangleCount);
		ArgumentOutOfRangeException.ThrowIfNegative(endTriangle);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(endTriangle, _mesh.TriangleCount);
		corridorLength = 0;

		var startXZ = NavGeometry.ToXZ(start);
		var endXZ = NavGeometry.ToXZ(end);
		var areas = _mesh.Areas;
		if (areas[endTriangle].IsBlocked) {
			return NavPathStatus.EndpointBlocked;
		}
		if ((areaMask & areas[endTriangle].AreaMask) == 0) {
			return NavPathStatus.EndAreaRejected;
		}

		if (startTriangle == endTriangle) {
			corridor[0] = startTriangle;
			corridorLength = 1;
			return NavPathStatus.Found;
		}

		Reset();
		Touch(startTriangle);
		_entryPoints[startTriangle] = startXZ;
		_gScores[startTriangle] = FP.Zero;
		_open.Push(startTriangle, FVector2.Distance(startXZ, endXZ));

		var triangles = _mesh.Triangles;
		var iterations = 0;
		while (_open.Count > 0 && iterations < _config.MaxIterations) {
			iterations++;
			var current = _open.Pop();
			if (current == endTriangle) {
				return ReconstructCorridor(endTriangle, corridor, out corridorLength);
			}
			_closed[current] = true;

			ref readonly var currentTriangle = ref triangles[current];
			var currentAllowed = (areaMask & areas[current].AreaMask) != 0;
			var currentBlocked = areas[current].IsBlocked;
			for (var e = 0; e < 3; e++) {
				var neighbor = currentTriangle.GetNeighbor(e);
				if (neighbor < 0 || IsClosed(neighbor)) {
					continue;
				}
				// Never enter forbidden or blocked ground from ground that is not (the escape rule above).
				if (((areaMask & areas[neighbor].AreaMask) == 0 && currentAllowed) || (areas[neighbor].IsBlocked && !currentBlocked)) {
					continue;
				}

				Touch(neighbor);
				currentTriangle.GetEdgeVertices(e, out var va, out var vb);
				var edgeMid = (_mesh.GetVertexXZ(va) + _mesh.GetVertexXZ(vb)) * FP.Half;
				var tentativeG = _gScores[current] + FVector2.Distance(_entryPoints[current], edgeMid) * areas[neighbor].CostMultiplier;

				var queued = _open.Contains(neighbor);
				if (queued && tentativeG >= _gScores[neighbor]) {
					continue;
				}
				_gScores[neighbor] = tentativeG;
				_cameFrom[neighbor] = current;
				_entryPoints[neighbor] = edgeMid;
				var f = tentativeG + FVector2.Distance(edgeMid, endXZ);
				if (queued) {
					_open.DecreaseKey(neighbor, f);
				} else {
					_open.Push(neighbor, f);
				}
			}
		}

		return _open.Count == 0 ? NavPathStatus.NoRoute : NavPathStatus.IterationLimit;
	}

	private void Reset() {
		_open.Clear();
		_generation++;
		if (_generation == int.MaxValue) {
			// 0 is the never-touched sentinel, so wrap to 1.
			Array.Clear(_nodeGeneration);
			_generation = 1;
		}
	}

	private void Touch(int triangle) {
		if (_nodeGeneration[triangle] == _generation) {
			return;
		}
		_nodeGeneration[triangle] = _generation;
		_gScores[triangle] = FP.MaxValue;
		_cameFrom[triangle] = -1;
		_closed[triangle] = false;
		_entryPoints[triangle] = FVector2.Zero;
	}

	private bool IsClosed(int triangle) {
		return _nodeGeneration[triangle] == _generation && _closed[triangle];
	}

	/// <summary>
	/// Writes the chain ending at <paramref name="end"/> in start-to-end order. When it does not fit,
	/// keeps the start side so the corridor still begins at the agent's triangle.
	/// </summary>
	private NavPathStatus ReconstructCorridor(int end, Span<int> corridor, out int length) {
		var total = 0;
		for (var node = end; node >= 0; node = _cameFrom[node]) {
			total++;
		}

		var skip = Math.Max(0, total - corridor.Length);
		length = total - skip;
		var index = total - 1;
		for (var node = end; node >= 0; node = _cameFrom[node], index--) {
			if (index < length) {
				corridor[index] = node;
			}
		}
		return skip > 0 ? NavPathStatus.FoundTruncated : NavPathStatus.Found;
	}
}
