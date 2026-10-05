using System;
using System.Collections.Generic;
using System.Text;
using Fixed64;
using Godot;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

/// <summary>
/// Navigation debug overlay for the predicted client world. F5 toggles it; F6 cycles the layers
/// (everything, agents only, mesh only); F7 cycles the agent shown (all, then each in turn).
///
/// Triangles are filled by area (walkable green, other areas blue, costly yellow, blocked red);
/// white edges are the mesh boundary, where no corridor can cross. Level colliders are outlined by
/// how the bake used them: walkable grey, obstacle-only orange, excluded dark. The yellow ring
/// around an obstacle is its footprint grown by the baked agent radius, the ground Recast eroded,
/// so the hole in the mesh there is the obstacle plus clearance. Per agent: the corridor ahead
/// (cyan fill), the triangle it stands in (cyan outline), the string-pulled path (magenta) with
/// its funnel corners, the steering segment to the next corner (white), the requested destination
/// (red cross) and the corridor's target (green cross), or a red line to an unreachable
/// destination. The corner label lists each agent's status and the bake settings.
///
/// Drawing goes through <see cref="Core{TWorld}.NavDebugDraw"/>, which reads the world and never
/// writes it.
/// </summary>
public partial class NavDebugView : Node3D, INavDebugDraw {
	private const float SurfaceLift = 0.03f;
	private const float PointSize = 0.25f;

	private static readonly NavDebugDrawFlags[] s_layers = [
		NavDebugDrawFlags.Default,
		NavDebugDrawFlags.Agents,
		NavDebugDrawFlags.Triangles | NavDebugDrawFlags.Edges | NavDebugDrawFlags.Sources,
	];

	/// <summary>The level the client world was set up from. Set before the node enters the tree.</summary>
	public LevelData Level { get; set; }

	private NavDebugDraw _draw;
	private ImmediateMesh _mesh;
	private StandardMaterial3D _fillMaterial;
	private StandardMaterial3D _lineMaterial;
	private Label _label;
	private readonly List<(Vector3 Position, Color Color)> _fill = [];
	private readonly List<(Vector3 Position, Color Color)> _lines = [];
	private readonly StringBuilder _agents = new();
	private bool _enabled;
	private bool _toggleHeld;
	private bool _layerHeld;
	private bool _selectHeld;
	private int _layer;
	private int _selectedAgent = -1;
	private NavMesh _summarizedMesh;
	private int _boundaryEdges;

	public override void _Ready() {
		_enabled = ClientPerformanceCapture.DebugDrawing;
		_draw = new NavDebugDraw(Level ?? LevelData.Empty);
		_mesh = new ImmediateMesh();
		AddChild(new MeshInstance3D { Mesh = _mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
		_fillMaterial = new StandardMaterial3D {
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
		};
		_lineMaterial = new StandardMaterial3D {
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			VertexColorUseAsAlbedo = true,
			NoDepthTest = true,
		};

		var layer = new CanvasLayer();
		AddChild(layer);
		_label = new Label { Visible = _enabled, HorizontalAlignment = HorizontalAlignment.Right };
		layer.AddChild(_label);
	}

	public override void _Process(double delta) {
		using var timing = ClientPerformanceCapture.Measure(ClientPerformanceCapture.Section.NavDebug);
		if (Pressed(Key.F5, ref _toggleHeld)) {
			_enabled = !_enabled;
			_label.Visible = _enabled;
		}
		if (Pressed(Key.F6, ref _layerHeld)) {
			_layer = (_layer + 1) % s_layers.Length;
		}
		if (Pressed(Key.F7, ref _selectHeld)) {
			_selectedAgent = _selectedAgent + 1 >= _draw.AgentCount ? -1 : _selectedAgent + 1;
		}

		ClientPerformanceCapture.RecordNavOverlay(_enabled, (int)s_layers[_layer], _selectedAgent);
		_mesh.ClearSurfaces();
		if (!_enabled) {
			return;
		}

		_fill.Clear();
		_lines.Clear();
		_agents.Clear();
		_draw.Draw(this, s_layers[_layer], _selectedAgent);
		if (_selectedAgent >= _draw.AgentCount) {
			_selectedAgent = -1;
		}
		Emit(Mesh.PrimitiveType.Triangles, _fill, _fillMaterial);
		Emit(Mesh.PrimitiveType.Lines, _lines, _lineMaterial);

		_label.Text = Summary();
		var viewport = GetViewport().GetVisibleRect().Size;
		_label.Position = new Vector2(viewport.X - _label.Size.X - 8, 8);
	}

	public void DrawTriangle(FVector3 a, FVector3 b, FVector3 c, NavDebugColor color) {
		var lift = Vector3.Up * (color == NavDebugColor.Corridor ? 2 * SurfaceLift : SurfaceLift);
		var godotColor = ToGodot(color);
		_fill.Add((ToGodot(a) + lift, godotColor));
		_fill.Add((ToGodot(b) + lift, godotColor));
		_fill.Add((ToGodot(c) + lift, godotColor));
	}

	public void DrawSegment(FVector3 start, FVector3 end, NavDebugColor color) {
		var godotColor = ToGodot(color);
		var lift = Vector3.Up * SurfaceLift;
		_lines.Add((ToGodot(start) + lift, godotColor));
		_lines.Add((ToGodot(end) + lift, godotColor));
	}

	public void DrawPoint(FVector3 point, NavDebugColor color) {
		var p = ToGodot(point) + Vector3.Up * SurfaceLift;
		var godotColor = ToGodot(color);
		foreach (var axis in (ReadOnlySpan<Vector3>)[Vector3.Right, Vector3.Up, Vector3.Back]) {
			_lines.Add((p - axis * PointSize, godotColor));
			_lines.Add((p + axis * PointSize, godotColor));
		}
	}

	public void DrawAgent(int index, FVector3 feet, in NavAgent agent) {
		_agents.Append($"\n#{index} {agent.Status} ({agent.PathStatus}) at {Format(feet)}");
		if (!agent.HasDestination) {
			_agents.Append(", no destination");
			return;
		}
		_agents.Append($"\n   dest {Format(agent.Destination)}  target {Format(agent.PathTarget)}");
		_agents.Append($"\n   triangle {agent.CurrentTriangle}  corridor {agent.CorridorIndex + 1}/{agent.CorridorLength}  re-plan in {agent.NextRepathTick - S.CurrentTick} ticks");
	}

	private string Summary() {
		var text = new StringBuilder("Navigation (F5)  layers F6: ").Append(s_layers[_layer]);
		text.Append("  agent F7: ").Append(_selectedAgent < 0 ? "all" : $"#{_selectedAgent}");
		var mesh = _draw.Mesh;
		if (mesh is null) {
			return text.Append("\nno navmesh in this level").ToString();
		}
		if (!ReferenceEquals(mesh, _summarizedMesh)) {
			_summarizedMesh = mesh;
			_boundaryEdges = CountBoundaryEdges(mesh);
		}
		text.Append($"\nmesh: {mesh.TriangleCount} triangles, {_boundaryEdges} boundary edges");
		if (Level?.Navigation is { } navigation) {
			var s = navigation.Settings;
			text.Append($"\nbake: agent radius {s.AgentRadius.ToFloat():0.##}, height {s.AgentHeight.ToFloat():0.##}, climb {s.AgentMaxClimb.ToFloat():0.##}, slope {s.AgentMaxSlopeDegrees.ToFloat():0.#} deg");
			text.Append("\nobstacle-only colliders are solid, never walkable; ground within the agent radius of them is eroded");
		}
		return text.Append(_draw.AgentCount == 0 ? "\nno agents" : _agents.ToString()).ToString();
	}

	private static int CountBoundaryEdges(NavMesh mesh) {
		var count = 0;
		foreach (ref readonly var triangle in mesh.Triangles) {
			for (var edge = 0; edge < 3; edge++) {
				if (triangle.GetNeighbor(edge) < 0) {
					count++;
				}
			}
		}
		return count;
	}

	private void Emit(Mesh.PrimitiveType primitive, List<(Vector3 Position, Color Color)> vertices, Material material) {
		if (vertices.Count == 0) {
			return;
		}
		_mesh.SurfaceBegin(primitive, material);
		foreach (var (position, color) in vertices) {
			_mesh.SurfaceSetColor(color);
			_mesh.SurfaceAddVertex(position);
		}
		_mesh.SurfaceEnd();
	}

	private static bool Pressed(Key key, ref bool held) {
		var down = Input.IsKeyPressed(key);
		var pressed = down && !held;
		held = down;
		return pressed;
	}

	private static string Format(FVector3 v) => $"({v.X.ToFloat():0.0}, {v.Y.ToFloat():0.0}, {v.Z.ToFloat():0.0})";

	private static Vector3 ToGodot(FVector3 v) => new(v.X.ToFloat(), v.Y.ToFloat(), v.Z.ToFloat());

	private static Color ToGodot(NavDebugColor color) => color switch {
		NavDebugColor.WalkableArea => new Color(0.3f, 0.85f, 0.4f, 0.22f),
		NavDebugColor.OtherArea => new Color(0.35f, 0.55f, 1f, 0.22f),
		NavDebugColor.CostlyArea => new Color(1f, 0.85f, 0.3f, 0.25f),
		NavDebugColor.BlockedArea => new Color(1f, 0.25f, 0.25f, 0.3f),
		NavDebugColor.InteriorEdge => new Color(0.3f, 0.7f, 0.4f),
		NavDebugColor.BoundaryEdge => new Color(1f, 1f, 1f),
		NavDebugColor.WalkableSource => new Color(0.55f, 0.55f, 0.55f),
		NavDebugColor.ObstacleSource => new Color(1f, 0.55f, 0.15f),
		NavDebugColor.ObstacleClearance => new Color(1f, 0.9f, 0.3f),
		NavDebugColor.ExcludedSource => new Color(0.3f, 0.3f, 0.3f),
		NavDebugColor.Corridor => new Color(0.3f, 0.9f, 1f, 0.3f),
		NavDebugColor.CurrentTriangle => new Color(0.3f, 0.9f, 1f),
		NavDebugColor.Path => new Color(1f, 0.3f, 1f),
		NavDebugColor.Corner => new Color(1f, 0.6f, 1f),
		NavDebugColor.Steering => new Color(1f, 1f, 1f),
		NavDebugColor.Destination => new Color(1f, 0.3f, 0.3f),
		NavDebugColor.PathTarget => new Color(0.4f, 1f, 0.4f),
		NavDebugColor.FailedPath => new Color(1f, 0.2f, 0.2f),
		NavDebugColor.OpenZone => new Color(0.35f, 0.75f, 1f),
		NavDebugColor.BlockedZone => new Color(1f, 0.25f, 0.25f),
		_ => Colors.Magenta,
	};
}
