using Godot;

namespace Space.Client.LevelAuthoring;

/// <summary>
/// Draws a box as orange lines in the editor, on top of everything. Added as an internal child, so it is
/// hidden from the scene dock and never saved.
/// </summary>
public sealed class BoxOutline {
	private const string NodeName = "BoxOutline";
	private static readonly Color Color = new(1f, 0.55f, 0.1f);

	private readonly Node3D _owner;
	private MeshInstance3D _instance;
	private ImmediateMesh _mesh;
	private Vector3? _drawnSize;
	private bool _drawn;

	public BoxOutline(Node3D owner) => _owner = owner;

	/// <summary>Redraws when <paramref name="size"/> changed; null clears the outline.</summary>
	public void Update(Vector3? size) {
		if (_drawn && size == _drawnSize) {
			return;
		}
		_drawn = true;
		_drawnSize = size;

		if (_instance is null && EntitySpawn.FindInternalChild(_owner, NodeName) is MeshInstance3D { Mesh: ImmediateMesh existingMesh } existing) {
			// Reuse the outline left by an instance from before an editor C# reload.
			_instance = existing;
			_mesh = existingMesh;
		}
		if (_instance is null) {
			_mesh = new ImmediateMesh();
			_instance = new MeshInstance3D {
				Name = NodeName,
				Mesh = _mesh,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				MaterialOverride = new StandardMaterial3D {
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					AlbedoColor = Color,
					NoDepthTest = true,
				},
			};
			_owner.AddChild(_instance, false, Node.InternalMode.Back);
		}
		_mesh.ClearSurfaces();
		if (size is not { } boxSize) {
			return;
		}

		var h = boxSize * 0.5f;
		Vector3[] corners = [
			new(-h.X, -h.Y, -h.Z), new(h.X, -h.Y, -h.Z), new(h.X, -h.Y, h.Z), new(-h.X, -h.Y, h.Z),
			new(-h.X, h.Y, -h.Z), new(h.X, h.Y, -h.Z), new(h.X, h.Y, h.Z), new(-h.X, h.Y, h.Z),
		];
		_mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
		for (var i = 0; i < 4; i++) {
			Line(corners[i], corners[(i + 1) % 4]);
			Line(corners[i + 4], corners[(i + 1) % 4 + 4]);
			Line(corners[i], corners[i + 4]);
		}
		_mesh.SurfaceEnd();

		void Line(Vector3 a, Vector3 b) {
			_mesh.SurfaceAddVertex(a);
			_mesh.SurfaceAddVertex(b);
		}
	}
}
