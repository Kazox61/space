using Fixed64;

namespace Space.GameCore;

/// <summary>
/// Encoding of <see cref="LevelNavigation"/> inside the level payload. Every count is checked
/// against a limit and against the bytes actually left before anything is allocated, and the
/// decoded mesh goes through <see cref="NavMesh"/>'s own validation before it is returned.
/// Portal orientation and centroids are not stored; they are derived again from the vertices.
/// </summary>
internal static class LevelNavigationCodec {
	public const int MaximumVertexCount = 1_000_000;
	public const int MaximumTriangleCount = 1_000_000;
	public const int MaximumGridTriangleCount = 16_000_000;

	internal const int SettingsSize = 11 * sizeof(long) + 2 * sizeof(int);
	internal const int VertexSize = 3 * sizeof(long);
	internal const int TriangleSize = 7 * sizeof(int) + sizeof(long) + sizeof(byte);
	internal const int MeshMetadataSize = 7 * sizeof(long) + 2 * sizeof(int);

	public static void Write(BinaryWriter writer, LevelNavigation? navigation) {
		if (navigation is null) {
			writer.Write((byte)0);
			return;
		}
		writer.Write((byte)1);
		WriteSettings(writer, navigation.Settings);
		WriteMesh(writer, navigation.Mesh);
		WriteZones(writer, navigation.Zones);
	}

	public static LevelNavigation? Read(BinaryReader reader) {
		var present = reader.ReadByte();
		if (present == 0) {
			return null;
		}
		if (present != 1) {
			throw new InvalidDataException($"Level navigation flag {present} is invalid.");
		}
		var settings = ReadSettings(reader);
		var mesh = ReadMesh(reader);
		var zones = ReadZones(reader, mesh.TriangleCount);
		try {
			return new LevelNavigation(settings, mesh, zones);
		} catch (ArgumentException exception) {
			throw new InvalidDataException($"Level navigation zones are invalid: {exception.Message}", exception);
		}
	}

	private static void WriteZones(BinaryWriter writer, IReadOnlyList<NavZoneData> zones) {
		writer.Write(zones.Count);
		foreach (var zone in zones) {
			writer.Write(zone.Id);
			writer.Write(zone.Triangles.Length);
			foreach (var triangle in zone.Triangles) {
				writer.Write(triangle);
			}
		}
	}

	/// <summary>Zone ids, order, and triangle ownership are checked by <see cref="LevelNavigation"/>.</summary>
	private static NavZoneData[] ReadZones(BinaryReader reader, int triangleCount) {
		var count = reader.ReadInt32();
		if (count < 0 || count > NavZoneData.MaxZones) {
			throw new InvalidDataException($"Level navigation zone count {count} is invalid.");
		}
		var zones = new NavZoneData[count];
		for (var z = 0; z < count; z++) {
			var id = LevelDataCodec.ReadBoundedString(reader, NavZoneData.MaxIdLength, $"Level navigation zone {z} id");
			var length = ReadCount(reader, "zone triangle", triangleCount, sizeof(int));
			var triangles = new int[length];
			for (var i = 0; i < length; i++) {
				triangles[i] = reader.ReadInt32();
			}
			try {
				zones[z] = new NavZoneData(id, triangles);
			} catch (ArgumentException exception) {
				throw new InvalidDataException($"Level navigation zone {z} is invalid: {exception.Message}", exception);
			}
		}
		return zones;
	}

	private static void WriteSettings(BinaryWriter writer, in NavBakeSettings settings) {
		writer.Write(settings.VoxelSize.RawValue);
		writer.Write(settings.VoxelHeight.RawValue);
		writer.Write(settings.AgentRadius.RawValue);
		writer.Write(settings.AgentHeight.RawValue);
		writer.Write(settings.AgentMaxClimb.RawValue);
		writer.Write(settings.AgentMaxSlopeDegrees.RawValue);
		writer.Write(settings.RegionMinSize);
		writer.Write(settings.RegionMergeSize);
		writer.Write(settings.EdgeMaxLength.RawValue);
		writer.Write(settings.EdgeMaxError.RawValue);
		writer.Write(settings.DetailSampleDistance.RawValue);
		writer.Write(settings.DetailSampleMaxError.RawValue);
		writer.Write(settings.LookupCellSize.RawValue);
	}

	private static NavBakeSettings ReadSettings(BinaryReader reader) {
		var settings = new NavBakeSettings(
			VoxelSize: ReadFP(reader),
			VoxelHeight: ReadFP(reader),
			AgentRadius: ReadFP(reader),
			AgentHeight: ReadFP(reader),
			AgentMaxClimb: ReadFP(reader),
			AgentMaxSlopeDegrees: ReadFP(reader),
			RegionMinSize: reader.ReadInt32(),
			RegionMergeSize: reader.ReadInt32(),
			EdgeMaxLength: ReadFP(reader),
			EdgeMaxError: ReadFP(reader),
			DetailSampleDistance: ReadFP(reader),
			DetailSampleMaxError: ReadFP(reader),
			LookupCellSize: ReadFP(reader)
		);
		try {
			settings.Validate();
		} catch (ArgumentOutOfRangeException exception) {
			throw new InvalidDataException($"Level navigation bake settings are invalid: {exception.Message}", exception);
		}
		return settings;
	}

	private static void WriteMesh(BinaryWriter writer, NavMeshData mesh) {
		writer.Write(mesh.Vertices.Length);
		foreach (var v in mesh.Vertices) {
			writer.Write(v.X.RawValue);
			writer.Write(v.Y.RawValue);
			writer.Write(v.Z.RawValue);
		}

		writer.Write(mesh.TriangleCount);
		for (var i = 0; i < mesh.TriangleCount; i++) {
			var t = mesh.Triangles[i];
			var area = mesh.Areas[i];
			writer.Write(t.V0);
			writer.Write(t.V1);
			writer.Write(t.V2);
			writer.Write(t.Neighbor0);
			writer.Write(t.Neighbor1);
			writer.Write(t.Neighbor2);
			writer.Write(area.AreaMask);
			writer.Write(area.CostMultiplier.RawValue);
			writer.Write(area.IsBlocked ? (byte)1 : (byte)0);
		}

		writer.Write(mesh.BoundsXZ.Min.X.RawValue);
		writer.Write(mesh.BoundsXZ.Min.Y.RawValue);
		writer.Write(mesh.BoundsXZ.Max.X.RawValue);
		writer.Write(mesh.BoundsXZ.Max.Y.RawValue);
		writer.Write(mesh.GridWidth);
		writer.Write(mesh.GridHeight);
		writer.Write(mesh.GridCellSize.RawValue);
		writer.Write(mesh.GridOrigin.X.RawValue);
		writer.Write(mesh.GridOrigin.Y.RawValue);
		foreach (var value in mesh.GridCells) {
			writer.Write(value);
		}
		writer.Write(mesh.GridTriangles.Length);
		foreach (var value in mesh.GridTriangles) {
			writer.Write(value);
		}
	}

	private static NavMeshData ReadMesh(BinaryReader reader) {
		var vertexCount = ReadCount(reader, "vertex", MaximumVertexCount, VertexSize);
		var vertices = new FVector3[vertexCount];
		for (var i = 0; i < vertexCount; i++) {
			vertices[i] = new FVector3(ReadCoordinate(reader, i), ReadCoordinate(reader, i), ReadCoordinate(reader, i));
		}

		var triangleCount = ReadCount(reader, "triangle", MaximumTriangleCount, TriangleSize);
		var triangles = new NavTriangle[triangleCount];
		var areas = new NavTriangleArea[triangleCount];
		for (var t = 0; t < triangleCount; t++) {
			var v0 = ReadVertexIndex(reader, vertexCount, t);
			var v1 = ReadVertexIndex(reader, vertexCount, t);
			var v2 = ReadVertexIndex(reader, vertexCount, t);
			var n0 = reader.ReadInt32();
			var n1 = reader.ReadInt32();
			var n2 = reader.ReadInt32();
			triangles[t] = NavTriangle.Create(vertices, v0, v1, v2, n0, n1, n2);
			areas[t] = ReadArea(reader, t);
		}

		var boundsMin = new FVector2(ReadFP(reader), ReadFP(reader));
		var boundsMax = new FVector2(ReadFP(reader), ReadFP(reader));
		var gridWidth = reader.ReadInt32();
		var gridHeight = reader.ReadInt32();
		if (gridWidth <= 0 || gridHeight <= 0 || (long)gridWidth * gridHeight > NavMesh.MaxGridCells) {
			throw new InvalidDataException($"Level navmesh grid {gridWidth}x{gridHeight} is invalid.");
		}
		var cellSize = ReadFP(reader);
		var origin = new FVector2(ReadFP(reader), ReadFP(reader));
		var cellValues = gridWidth * gridHeight * 2;
		EnsureAvailable(reader, cellValues, sizeof(int), "grid cell");
		var gridCells = new int[cellValues];
		for (var i = 0; i < cellValues; i++) {
			gridCells[i] = reader.ReadInt32();
		}
		var gridTriangleCount = ReadCount(reader, "grid triangle", MaximumGridTriangleCount, sizeof(int));
		var gridTriangles = new int[gridTriangleCount];
		for (var i = 0; i < gridTriangleCount; i++) {
			gridTriangles[i] = reader.ReadInt32();
		}

		try {
			var mesh = new NavMesh(vertices, triangles, areas, new FAABB2(boundsMin, boundsMax), gridCells, gridTriangles, gridWidth, gridHeight, cellSize, origin);
			return new NavMeshData(mesh);
		} catch (ArgumentException exception) {
			throw new InvalidDataException($"Level navmesh is invalid: {exception.Message}", exception);
		}
	}

	private static NavTriangleArea ReadArea(BinaryReader reader, int triangle) {
		var mask = reader.ReadInt32();
		var cost = ReadFP(reader);
		var blocked = reader.ReadByte();
		if (mask == 0 || cost <= FP.Zero || blocked > 1) {
			throw new InvalidDataException($"Level navmesh triangle {triangle} has invalid area data.");
		}
		return new NavTriangleArea { AreaMask = mask, CostMultiplier = cost, IsBlocked = blocked == 1 };
	}

	private static int ReadVertexIndex(BinaryReader reader, int vertexCount, int triangle) {
		var index = reader.ReadInt32();
		if ((uint)index >= (uint)vertexCount) {
			throw new InvalidDataException($"Level navmesh triangle {triangle} references vertex {index} of {vertexCount}.");
		}
		return index;
	}

	private static FP ReadCoordinate(BinaryReader reader, int vertex) {
		var value = ReadFP(reader);
		if (FP.Abs(value) > NavMesh.MaxCoordinate) {
			throw new InvalidDataException($"Level navmesh vertex {vertex} is outside +/-{NavMesh.MaxCoordinate}.");
		}
		return value;
	}

	private static int ReadCount(BinaryReader reader, string kind, int maximum, int elementSize) {
		var count = reader.ReadInt32();
		if (count < 0 || count > maximum) {
			throw new InvalidDataException($"Level navmesh {kind} count {count} is invalid.");
		}
		EnsureAvailable(reader, count, elementSize, kind);
		return count;
	}

	/// <summary>Refuses a count the remaining payload cannot hold, before its array is allocated.</summary>
	private static void EnsureAvailable(BinaryReader reader, long count, int elementSize, string kind) {
		var stream = reader.BaseStream;
		if (count * elementSize > stream.Length - stream.Position) {
			throw new InvalidDataException($"Level navmesh {kind} data is truncated.");
		}
	}

	private static FP ReadFP(BinaryReader reader) {
		return FP.FromRaw(reader.ReadInt64());
	}
}
