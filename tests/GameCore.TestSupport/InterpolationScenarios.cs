using System;
using System.Collections.Generic;
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

	/// <summary>
	/// A late remote input moves the remote player on the corrected timeline; the probe reports that
	/// move for the remote player only, as "old minus corrected" position.
	/// </summary>
	private static void CorrectionProbeMeasuresMispredictionTest() {
		Console.WriteLine("--- CorrectionProbeMeasuresMispredictionTest ---");
		StartFxSession(SimulationType.AutomaticRollbacks);
		var probe = new PlayerCorrectionProbe();
		RollbackObserver = probe;
		var corrections = new List<PositionCorrection>();

		Advance(30);
		probe.BeginUpdate();
		Advance(1);
		Check("an update without a misprediction reports no corrections", probe.EndUpdate(corrections) && corrections.Count == 0);

		// The remote player started walking right ten ticks ago; we predicted them standing still.
		S.SetApprovedInputAt(21, RemoteChannel, new PlayerInput { MoveX = Fixed64.FP.One });
		probe.BeginUpdate();
		Advance(1);
		probe.EndUpdate(corrections);

		Check("only the mispredicted player is corrected", corrections.Count == 1 && corrections[0].Channel == RemoteChannel);
		if (corrections.Count == 1) {
			var error = corrections[0].Error;
			Console.WriteLine($"correction error: {Fixed64.FConversions.ToFloat(error.X)}, {Fixed64.FConversions.ToFloat(error.Y)}, {Fixed64.FConversions.ToFloat(error.Z)}");
			// About ten ticks of walking at MoveSpeed, pointing back to where the player was drawn.
			Check("the error points from the corrected position back to the old one", error.X < Fixed64.FP.Zero);
			Check("the error is about ten ticks of walking", Fixed64.FConversions.ToFloat(error.X) is < -1f and > -1.5f);
			Check("a walk is not a teleport", !corrections[0].Teleported);
		}

		RollbackObserver = null;
	}

	/// <summary>A full sync makes the probe report "drop everything" instead of an error across timelines.</summary>
	private static void CorrectionProbeFullSyncTest() {
		Console.WriteLine("--- CorrectionProbeFullSyncTest ---");
		StartFxSession(SimulationType.ForwardOnly);
		var probe = new PlayerCorrectionProbe();
		RollbackObserver = probe;
		var corrections = new List<PositionCorrection>();

		Advance(5);
		probe.BeginUpdate();
		var buffer = FFS.Libraries.StaticPack.BinaryPackWriter.Create(new byte[GameWorldRollback.WorldSnapshotLength]);
		var handler = new GameWorldFullSyncHandler();
		handler.WriteFullSync(ref buffer);
		var reader = buffer.AsReader();
		handler.ReadFullSync(ref reader);
		Advance(1);

		Check("a full sync during the update is reported", !probe.EndUpdate(corrections) && corrections.Count == 0);
		RollbackObserver = null;
	}

	/// <summary>Offsets accumulate, fade to zero, and are cut on a teleport or when too long.</summary>
	private static void CorrectionSmootherTest() {
		Console.WriteLine("--- CorrectionSmootherTest ---");
		var smoother = new CorrectionSmoother();
		var corrections = new List<PositionCorrection> {
			new() { Channel = RemoteChannel, Error = new Fixed64.FVector3(-Fixed64.FP.One, Fixed64.FP.Zero, Fixed64.FP.Zero) },
		};

		smoother.Apply(corrections);
		Check("a correction starts as the full offset", MathF.Abs(smoother.Offset(RemoteChannel).X + 1f) < 1e-4f);
		Check("other players have no offset", smoother.Offset(LocalChannel) == System.Numerics.Vector3.Zero);

		smoother.Advance(CorrectionSmoother.HalfLifeSeconds);
		Check("the offset halves every half-life", MathF.Abs(smoother.Offset(RemoteChannel).X + 0.5f) < 1e-3f);

		smoother.Apply(corrections);
		Check("a second correction adds to the remaining offset", MathF.Abs(smoother.Offset(RemoteChannel).X + 1.5f) < 1e-3f);

		smoother.Apply(corrections);
		Check("an offset longer than the cut distance is dropped", smoother.Offset(RemoteChannel) == System.Numerics.Vector3.Zero);

		smoother.Apply(corrections);
		smoother.Apply(new List<PositionCorrection> { new() { Channel = RemoteChannel, Teleported = true } });
		Check("a teleport drops the offset", smoother.Offset(RemoteChannel) == System.Numerics.Vector3.Zero);

		smoother.Apply(corrections);
		smoother.Advance(1f);
		Check("offsets fade out completely", smoother.ActiveCount == 0);
	}
}
