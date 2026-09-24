using System;
using FFS.Libraries.StaticEcs;
using Fixed;
using Fixed32;
using Shenanicode.Rollback;
using Space.GameCore;
using static Space.GameCore.Core<PhysicsSmokeTest.TestWorld>;

namespace PhysicsSmokeTest;

/// <summary>
/// The simulation-side contract render interpolation relies on (Client: <c>TransformViewBehavior.TrySamplePose</c>):
/// the previous-tick world copy holds every entity under the same GID one tick behind, and
/// discontinuous moves are stamped in <see cref="Transform.TeleportTick"/>.
/// </summary>
public static partial class Program {
	private static bool _prevWorldCreated;

	/// <summary>Mirrors the client's <c>GameInterpolationReceiver</c>: copies the world into <see cref="WPrev"/> right before the final tick of each update.</summary>
	private sealed class PrevWorldReceiver : IInterpolationReceiver {
		public void SaveInterpolationState() {
			var snapshot = W.Serializer.CreateWorldSnapshot();
			WPrev.Serializer.LoadWorldSnapshot(snapshot, hardReset: true);
		}
	}

	private static void StartPrevWorld() {
		WPrev.Create(GameWorldSetup.WorldConfig);
		GameTypes.Register<TestWorldPrev>();
		WPrev.Initialize();
		_prevWorldCreated = true;
		S.SetInterpolationReceiver(new PrevWorldReceiver());
	}

	private static void DestroyPrevWorld() {
		if (_prevWorldCreated) {
			WPrev.Destroy();
			_prevWorldCreated = false;
		}
	}

	/// <summary>
	/// Every arrow is found in the previous-tick world under its current GID, exactly one tick of
	/// flight behind -- also right after a rollback re-created it under a new GID.
	/// </summary>
	private static void PrevWorldTracksEntitiesOneTickBehindTest() {
		Console.WriteLine("--- PrevWorldTracksEntitiesOneTickBehindTest ---");
		StartFxSession(SimulationType.AutomaticRollbacks);
		StartPrevWorld();
		Systems.GetResource<CharacterRes>().AttackDelay = Space.GameCore.Const.DeltaTime;

		Advance(22);
		S.SetPredictionInput(LocalChannel, AttackRight);
		Advance(6);
		Check("the arrow is one tick behind in the previous-tick world", ArrowIsOneTickBehind(out var gidBefore));

		S.SetApprovedInputAt(21, RemoteChannel, new PlayerInput { MoveX = Fixed64.FP.One });
		Advance(1);
		Check("after a rollback the arrow is still one tick behind under its current GID", ArrowIsOneTickBehind(out var gidAfter));
		Console.WriteLine($"arrow GID before rollback {gidBefore}, after {gidAfter}");

		foreach (var player in W.Query<All<PlayerInfo, Transform>>().Entities()) {
			Check("players are found in the previous-tick world", player.GID.TryUnpack<TestWorldPrev>(out var previousPlayer) && previousPlayer.Has<Transform>());
		}

		DestroyPrevWorld();

		static bool ArrowIsOneTickBehind(out EntityGID gid) {
			gid = default;
			var arrows = 0;
			var behind = false;
			foreach (var arrow in W.Query<All<ProjectileOrigin, Transform, Body>>().Entities()) {
				arrows++;
				gid = arrow.GID;
				if (!arrow.GID.TryUnpack<TestWorldPrev>(out var previousArrow) || !previousArrow.Has<Transform>()) {
					continue;
				}
				var step = arrow.Read<Transform>().Position - previousArrow.Read<Transform>().Position;
				var expected = arrow.Read<Body>().LinearVelocity.To64() * Space.GameCore.Const.DeltaTime;
				behind = Fixed64.FVector3.DistanceSqr(step, expected) < Fixed64.FP.FromRatio(1, 10000);
			}
			return arrows == 1 && behind;
		}
	}

	/// <summary>
	/// <c>BodyOperations.SetTransform</c> stamps the tick; the per-tick body sync and the
	/// previous-tick copy keep the stamp, so a view sees the two ticks around a teleport differ.
	/// </summary>
	private static void TeleportStampTest() {
		Console.WriteLine("--- TeleportStampTest ---");
		StartFxSession(SimulationType.ForwardOnly, static () => {
			var ball = W.NewEntity<Default>();
			ball.Set(new Transform());
			BodyOperations.CreateBody(ball, BodyType.Dynamic, new FWorldTransform(new FPos(10.ToFP().To64(), 3.ToFP().To64(), Fixed64.FP.Zero), FQuaternion.Identity));
			ShapeFactory.CreateShape(ball, Shape.MakeSphere(FVector3.Zero, FP.Half));
		});
		StartPrevWorld();

		Advance(10);
		var ball = FindBall();
		Check("a body that never teleported has no stamp", ball.Read<Transform>().TeleportTick == 0);

		S.SaveInterpolationState();
		BodyOperations.SetTransform(ball, new FWorldTransform(new FPos((-10).ToFP().To64(), 3.ToFP().To64(), Fixed64.FP.Zero), FQuaternion.Identity));
		Check("SetTransform stamps the current tick", ball.Read<Transform>().TeleportTick == 10);
		Check("the tick before the teleport carries a different stamp",
			ball.GID.TryUnpack<TestWorldPrev>(out var beforeTeleport) && beforeTeleport.Read<Transform>().TeleportTick != ball.Read<Transform>().TeleportTick);

		Advance(3);
		ball = FindBall();
		Check("the body sync keeps the stamp", ball.Read<Transform>().TeleportTick == 10);
		Check("ticks after the teleport share the stamp, so they blend",
			ball.GID.TryUnpack<TestWorldPrev>(out var previous) && previous.Read<Transform>().TeleportTick == 10);

		DestroyPrevWorld();

		static W.Entity FindBall() {
			foreach (var body in W.Query<All<Body, Transform>, None<ViewId>>().Entities()) {
				return body;
			}
			throw new InvalidOperationException("The test ball is missing.");
		}
	}
}
