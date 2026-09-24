using Fixed;
using Fixed32;
using Godot;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

/// <summary>
/// Line-based physics debug overlay for the predicted client world. F3 toggles it; F4 cycles the
/// draw set (shapes and contacts, then everything including AABBs and broad-phase proxies). Shapes
/// are colored by state: static grey, kinematic blue, awake green, sleeping purple, sensor yellow,
/// disabled red. The corner label shows the last step's counters, timings, and tree health.
/// </summary>
public partial class PhysicsDebugView : MeshInstance3D, IPhysicsDebugDraw {
	private const int CircleSegments = 16;

	private ImmediateMesh _mesh;
	private StandardMaterial3D _material;
	private Label _label;
	private bool _enabled;
	private bool _toggleHeld;
	private bool _cycleHeld;
	private PhysicsDebugDrawFlags _flags = PhysicsDebugDrawFlags.Default;
	private int _vertexCount;

	public override void _Ready() {
		_mesh = new ImmediateMesh();
		Mesh = _mesh;
		_material = new StandardMaterial3D {
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			NoDepthTest = true,
		};
		CastShadow = ShadowCastingSetting.Off;

		var layer = new CanvasLayer();
		AddChild(layer);
		_label = new Label { Position = new Vector2(8, 8), Visible = false };
		layer.AddChild(_label);
	}

	public override void _Process(double delta) {
		var toggle = Input.IsKeyPressed(Key.F3);
		if (toggle && !_toggleHeld) {
			_enabled = !_enabled;
			_label.Visible = _enabled;
		}
		_toggleHeld = toggle;

		var cycle = Input.IsKeyPressed(Key.F4);
		if (cycle && !_cycleHeld) {
			_flags = _flags == PhysicsDebugDrawFlags.Default ? PhysicsDebugDrawFlags.All : PhysicsDebugDrawFlags.Default;
		}
		_cycleHeld = cycle;

		_mesh.ClearSurfaces();
		if (!_enabled || !W.IsWorldInitialized || !W.HasResource<BroadPhase>()) {
			return;
		}

		_vertexCount = 0;
		_mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _material);
		PhysicsDebugDraw.Draw(this, _flags);
		if (_vertexCount == 0) {
			// An empty surface is an error; emit one degenerate line instead.
			AddLine(Vector3.Zero, Vector3.Zero, Colors.Transparent);
		}
		_mesh.SurfaceEnd();

		var trees = PhysicsDiagnostics.CaptureBroadPhase();
		_label.Text = $"{PhysicsDiagnostics.LastStep}\n{trees.Static}\n{trees.Kinematic}\n{trees.Dynamic}\nF4: {_flags}";
	}

	public void DrawSphere(FPos center, FP radius, PhysicsDebugColor color) {
		var c = ToGodot(center);
		var r = radius.ToFloat();
		var godotColor = ToGodot(color);
		DrawCircle(c, Vector3.Right, Vector3.Up, r, godotColor);
		DrawCircle(c, Vector3.Up, Vector3.Back, r, godotColor);
		DrawCircle(c, Vector3.Right, Vector3.Back, r, godotColor);
	}

	public void DrawCapsule(FPos center1, FPos center2, FP radius, PhysicsDebugColor color) {
		var a = ToGodot(center1);
		var b = ToGodot(center2);
		var r = radius.ToFloat();
		var godotColor = ToGodot(color);
		var axis = b - a;
		var up = axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Up;
		var side = Mathf.Abs(up.Dot(Vector3.Right)) < 0.9f ? up.Cross(Vector3.Right).Normalized() : up.Cross(Vector3.Forward).Normalized();
		var side2 = up.Cross(side);
		DrawCircle(a, side, side2, r, godotColor);
		DrawCircle(b, side, side2, r, godotColor);
		DrawCircle(a, side, up, r, godotColor);
		DrawCircle(b, side, up, r, godotColor);
		DrawCircle(a, side2, up, r, godotColor);
		DrawCircle(b, side2, up, r, godotColor);
		AddLine(a + side * r, b + side * r, godotColor);
		AddLine(a - side * r, b - side * r, godotColor);
		AddLine(a + side2 * r, b + side2 * r, godotColor);
		AddLine(a - side2 * r, b - side2 * r, godotColor);
	}

	public void DrawBox(FWorldTransform transform, FVector3 halfExtents, PhysicsDebugColor color) {
		var center = ToGodot(transform.Position);
		var rotation = new Quaternion(transform.Rotation.X.ToFloat(), transform.Rotation.Y.ToFloat(), transform.Rotation.Z.ToFloat(), transform.Rotation.W.ToFloat());
		var extent = ToGodot(halfExtents);
		var corners = new Vector3[8];
		for (var i = 0; i < 8; i++) {
			var local = new Vector3(
				(i & 1) != 0 ? extent.X : -extent.X,
				(i & 2) != 0 ? extent.Y : -extent.Y,
				(i & 4) != 0 ? extent.Z : -extent.Z);
			corners[i] = center + rotation * local;
		}
		DrawCorners(corners, ToGodot(color));
	}

	public void DrawAabb(FAABB aabb, PhysicsDebugColor color) {
		var lower = ToGodot(aabb.LowerBound);
		var upper = ToGodot(aabb.UpperBound);
		var corners = new Vector3[8];
		for (var i = 0; i < 8; i++) {
			corners[i] = new Vector3(
				(i & 1) != 0 ? upper.X : lower.X,
				(i & 2) != 0 ? upper.Y : lower.Y,
				(i & 4) != 0 ? upper.Z : lower.Z);
		}
		DrawCorners(corners, ToGodot(color));
	}

	public void DrawSegment(FPos start, FPos end, PhysicsDebugColor color) {
		AddLine(ToGodot(start), ToGodot(end), ToGodot(color));
	}

	public void DrawPoint(FPos point, PhysicsDebugColor color) {
		const float size = 0.05f;
		var p = ToGodot(point);
		var godotColor = ToGodot(color);
		AddLine(p - Vector3.Right * size, p + Vector3.Right * size, godotColor);
		AddLine(p - Vector3.Up * size, p + Vector3.Up * size, godotColor);
		AddLine(p - Vector3.Back * size, p + Vector3.Back * size, godotColor);
	}

	private void DrawCircle(Vector3 center, Vector3 axisA, Vector3 axisB, float radius, Color color) {
		var previous = center + axisA * radius;
		for (var i = 1; i <= CircleSegments; i++) {
			var angle = Mathf.Tau * i / CircleSegments;
			var next = center + (axisA * Mathf.Cos(angle) + axisB * Mathf.Sin(angle)) * radius;
			AddLine(previous, next, color);
			previous = next;
		}
	}

	private void DrawCorners(Vector3[] corners, Color color) {
		for (var i = 0; i < 8; i++) {
			for (var bit = 1; bit < 8; bit <<= 1) {
				if ((i & bit) == 0) {
					AddLine(corners[i], corners[i | bit], color);
				}
			}
		}
	}

	private void AddLine(Vector3 a, Vector3 b, Color color) {
		_mesh.SurfaceSetColor(color);
		_mesh.SurfaceAddVertex(a);
		_mesh.SurfaceSetColor(color);
		_mesh.SurfaceAddVertex(b);
		_vertexCount += 2;
	}

	private static Vector3 ToGodot(FPos p) => new(Fixed64.FConversions.ToFloat(p.X), Fixed64.FConversions.ToFloat(p.Y), Fixed64.FConversions.ToFloat(p.Z));

	private static Vector3 ToGodot(FVector3 v) => new(v.X.ToFloat(), v.Y.ToFloat(), v.Z.ToFloat());

	private static Color ToGodot(PhysicsDebugColor color) => color switch {
		PhysicsDebugColor.StaticShape => new Color(0.6f, 0.6f, 0.6f),
		PhysicsDebugColor.KinematicShape => new Color(0.35f, 0.6f, 1f),
		PhysicsDebugColor.AwakeShape => new Color(0.35f, 1f, 0.45f),
		PhysicsDebugColor.SleepingShape => new Color(0.65f, 0.4f, 0.9f),
		PhysicsDebugColor.DisabledShape => new Color(1f, 0.3f, 0.3f),
		PhysicsDebugColor.SensorShape => new Color(1f, 0.9f, 0.3f),
		PhysicsDebugColor.Aabb => new Color(0.9f, 0.5f, 0.9f),
		PhysicsDebugColor.Proxy => new Color(0.4f, 0.9f, 0.9f),
		PhysicsDebugColor.ContactTouching => new Color(1f, 0.25f, 0.25f),
		PhysicsDebugColor.ContactSpeculative => new Color(1f, 0.65f, 0.2f),
		PhysicsDebugColor.ContactNormal => new Color(1f, 1f, 1f),
		_ => Colors.Magenta,
	};
}
