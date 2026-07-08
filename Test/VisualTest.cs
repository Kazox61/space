using System.Numerics;
using FFS.Libraries.StaticEcs;
using Raylib_cs;
using Space.GameCore;
using Fixed32;
using Fixed;
using static Space.GameCore.Core<PhysicsSmokeTest.TestWorld>;

namespace PhysicsSmokeTest;

/// <summary>
/// Interactive raylib visualization of the physics pipeline, run via `dotnet run -- visual`.
/// Not part of the pass/fail console test suite in <see cref="Program"/> — this is for visually
/// eyeballing behavior (settling, rolling, tunneling) that's hard to judge from printed numbers alone.
/// </summary>
public static class VisualTest {
	private const double GroundRadius = 50;

	public static void Run() {
		Program.Bootstrap();

		// A large sphere approximates a flat floor near the origin (its surface is tangent to y=0),
		// while still reusing the same Sphere shape everything else uses.
		var groundBody = W.NewEntity<Default>();
		groundBody.Set(new Body {
			Type = BodyType.Static,
			Transform = new FWorldTransform(new FPos(Fixed64.FP.Zero, Fixed64.FP.FromRatio(-(int)GroundRadius, 1), Fixed64.FP.Zero), FQuaternion.Identity),
		});
		ShapeFactory.CreateShape(groundBody, Shape.MakeSphere(FVector3.Zero, FP.FromRatio((int)GroundRadius, 1)));

		SpawnSphere(new FVector3(FP.Zero, FP.FromRatio(6, 1), FP.Zero));

		Raylib.InitWindow(1280, 720, "Physics Visual Test");
		Raylib.SetTargetFPS(60);

		var camera = new Camera3D {
			Position = new Vector3(12, 10, 12),
			Target = new Vector3(0, 2, 0),
			Up = new Vector3(0, 1, 0),
			FovY = 45,
			Projection = CameraProjection.Perspective,
		};

		var random = new Random();

		while (!Raylib.WindowShouldClose()) {
			Raylib.UpdateCamera(ref camera, CameraMode.Orbital);

			if (Raylib.IsKeyPressed(KeyboardKey.Space)) {
				SpawnSphere(RandomDropPosition(random));
			}

			if (Raylib.IsKeyPressed(KeyboardKey.C)) {
				SpawnCapsule(RandomDropPosition(random));
			}

			if (Raylib.IsKeyPressed(KeyboardKey.B)) {
				SpawnBox(RandomDropPosition(random));
			}

			W.Tick();
			Systems.Update();

			Raylib.BeginDrawing();
			Raylib.ClearBackground(Color.RayWhite);

			Raylib.BeginMode3D(camera);
			DrawBodies();
			Raylib.EndMode3D();

			Raylib.DrawText("SPACE: drop sphere   C: drop capsule   B: drop box   mouse: orbit camera   ESC: quit", 10, 10, 20, Color.Black);
			Raylib.DrawFPS(1200, 10);
			Raylib.EndDrawing();
		}

		Raylib.CloseWindow();
		Program.Shutdown();
	}

	private static FVector3 RandomDropPosition(Random random) {
		var x = FP.FromRatio(random.Next(-6, 7), 1);
		var z = FP.FromRatio(random.Next(-6, 7), 1);
		return new FVector3(x, FP.FromRatio(15, 1), z);
	}

	private static void SpawnSphere(FVector3 position) {
		var body = W.NewEntity<Default>();
		body.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(FPos.FromLocal(position), FQuaternion.Identity),
		});

		// See the console tests' remarks: box3d's default density (1000, water) overflows Fixed32's
		// inertia math for a shape this size, corrupting rotation response. Keep it sane.
		var shape = Shape.MakeSphere(FVector3.Zero, FP.One);
		shape.Density = FP.One;
		ShapeFactory.CreateShape(body, shape);
	}

	private static void SpawnCapsule(FVector3 position) {
		var body = W.NewEntity<Default>();
		body.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(FPos.FromLocal(position), FQuaternion.Identity),
		});

		var shape = Shape.MakeCapsule(
			new FVector3(FP.Zero, -FP.Half, FP.Zero),
			new FVector3(FP.Zero, FP.Half, FP.Zero),
			FP.One);
		shape.Density = FP.One;
		ShapeFactory.CreateShape(body, shape);
	}

	private static void SpawnBox(FVector3 position) {
		var body = W.NewEntity<Default>();
		body.Set(new Body {
			Type = BodyType.Dynamic,
			GravityScale = FP.One,
			Transform = new FWorldTransform(FPos.FromLocal(position), FQuaternion.Identity),
		});

		var shape = Shape.MakeBox(FVector3.Zero, new FVector3(FP.One, FP.One, FP.One));
		shape.Density = FP.One;
		ShapeFactory.CreateShape(body, shape);
	}

	// Edges of a box hull's 8 corners, matching Hull.MakeBox's corner ordering (0-3 the +z face
	// loop, 4-7 the -z face loop, and the four verticals connecting them).
	private static readonly (int, int)[] BoxEdges = {
		(0, 1), (1, 2), (2, 3), (3, 0),
		(4, 5), (5, 6), (6, 7), (7, 4),
		(0, 4), (1, 5), (2, 6), (3, 7),
	};

	private static void DrawBodies() {
		foreach (var bodyEntity in W.Query<All<Body>>().Entities()) {
			ref readonly var body = ref bodyEntity.Read<Body>();
			var color = body.Type == BodyType.Static ? Color.DarkGray : Color.SkyBlue;

			if (!bodyEntity.Has<W.Links<Shapes>>()) {
				continue;
			}

			ref readonly var shapeLinks = ref bodyEntity.Read<W.Links<Shapes>>();
			for (var i = 0; i < shapeLinks.Length; i++) {
				if (!shapeLinks[i].Value.TryUnpack<TestWorld>(out var shapeEntity)) {
					continue;
				}

				ref readonly var shape = ref shapeEntity.Read<Shape>();
				switch (shape.Type) {
					case ShapeType.Sphere: {
							var worldCenter = FWorldTransform.TransformPoint(body.Transform, shape.SphereShape.Center);

							DrawSphereGizmo(
								ToVector3(worldCenter),
								(float)shape.SphereShape.Radius.ToDouble(),
								color);

							break;
						}

					case ShapeType.Capsule: {
							var c1 = FWorldTransform.TransformPoint(body.Transform, shape.CapsuleShape.Center1);
							var c2 = FWorldTransform.TransformPoint(body.Transform, shape.CapsuleShape.Center2);
							DrawCapsuleWire(
								ToVector3(c1),
								ToVector3(c2),
								(float)shape.CapsuleShape.Radius.ToDouble(),
								color
							);
							break;
						}

					case ShapeType.Hull: {
							var points = shape.HullShape.GetCorners();
							var corners = new Vector3[points.Length];
							for (var p = 0; p < points.Length; p++) {
								corners[p] = ToVector3(FWorldTransform.TransformPoint(body.Transform, points[p]));
							}

							foreach (var (a, b) in BoxEdges) {
								Raylib.DrawLine3D(corners[a], corners[b], color);
							}

							break;
						}
				}
			}
		}
	}

	private static void DrawCircle3D(
	Vector3 center,
	Vector3 u,
	Vector3 v,
	float radius,
	Color color) {
		const int segments = 32;

		Vector3 prev = center + u * radius;

		for (int i = 1; i <= segments; i++) {
			float t = MathF.Tau * i / segments;

			Vector3 p =
				center +
				(u * MathF.Cos(t) + v * MathF.Sin(t)) * radius;

			Raylib.DrawLine3D(prev, p, color);
			prev = p;
		}
	}

	private static void DrawCapsuleWire(
	Vector3 a,
	Vector3 b,
	float radius,
	Color color) {
		Vector3 axis = Vector3.Normalize(b - a);

		Vector3 temp =
			MathF.Abs(axis.Y) > 0.99f
				? Vector3.UnitX
				: Vector3.UnitY;

		Vector3 right = Vector3.Normalize(Vector3.Cross(temp, axis));
		Vector3 forward = Vector3.Normalize(Vector3.Cross(axis, right));

		// Top sphere
		DrawCircle3D(a, right, forward, radius, color);
		DrawCircle3D(a, right, axis, radius, color);
		DrawCircle3D(a, forward, axis, radius, color);

		// Bottom sphere
		DrawCircle3D(b, right, forward, radius, color);
		DrawCircle3D(b, right, axis, radius, color);
		DrawCircle3D(b, forward, axis, radius, color);

		// Cylinder edges
		Raylib.DrawLine3D(a + right * radius, b + right * radius, color);
		Raylib.DrawLine3D(a - right * radius, b - right * radius, color);
		Raylib.DrawLine3D(a + forward * radius, b + forward * radius, color);
		Raylib.DrawLine3D(a - forward * radius, b - forward * radius, color);
	}

	private static void DrawSphereGizmo(Vector3 center, float radius, Color color) {
		DrawCircle3D(center, Vector3.UnitX, Vector3.UnitY, radius, color);
		DrawCircle3D(center, Vector3.UnitX, Vector3.UnitZ, radius, color);
		DrawCircle3D(center, Vector3.UnitY, Vector3.UnitZ, radius, color);
	}

	private static Vector3 ToVector3(FPos p) => new(
		(float)Fixed64.FConversions.ToDouble(p.X),
		(float)Fixed64.FConversions.ToDouble(p.Y),
		(float)Fixed64.FConversions.ToDouble(p.Z));
}
