using Fixed64;

namespace Space.GameCore.Tests;

/// <summary>Every stored field of a <see cref="NavMesh"/> as bytes, for byte-identity assertions.</summary>
internal static class NavMeshBytes {
	public static byte[] Of(NavMesh mesh) {
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);

		writer.Write(mesh.Vertices.Length);
		foreach (var v in mesh.Vertices) {
			Write(writer, v.X);
			Write(writer, v.Y);
			Write(writer, v.Z);
		}

		writer.Write(mesh.TriangleCount);
		for (var i = 0; i < mesh.TriangleCount; i++) {
			var t = mesh.Triangles[i];
			writer.Write(t.V0);
			writer.Write(t.V1);
			writer.Write(t.V2);
			writer.Write(t.Neighbor0);
			writer.Write(t.Neighbor1);
			writer.Write(t.Neighbor2);
			writer.Write(t.PortalFlip);
			Write(writer, t.CenterXZ.X);
			Write(writer, t.CenterXZ.Y);
			Write(writer, t.CenterY);
			var area = mesh.Areas[i];
			writer.Write(area.AreaMask);
			Write(writer, area.CostMultiplier);
			writer.Write(area.IsBlocked);
		}

		Write(writer, mesh.BoundsXZ.Min.X);
		Write(writer, mesh.BoundsXZ.Min.Y);
		Write(writer, mesh.BoundsXZ.Max.X);
		Write(writer, mesh.BoundsXZ.Max.Y);
		writer.Write(mesh.GridWidth);
		writer.Write(mesh.GridHeight);
		Write(writer, mesh.GridCellSize);
		Write(writer, mesh.GridOrigin.X);
		Write(writer, mesh.GridOrigin.Y);
		foreach (var value in mesh.GridCells) {
			writer.Write(value);
		}
		writer.Write(mesh.GridTriangles.Length);
		foreach (var value in mesh.GridTriangles) {
			writer.Write(value);
		}

		writer.Flush();
		return stream.ToArray();
	}

	private static void Write(BinaryWriter writer, FP value) {
		writer.Write(value.RawValue);
	}
}
