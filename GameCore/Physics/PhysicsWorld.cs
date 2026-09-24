using System;
using System.IO;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using Fixed32;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public class PhysicsWorld : IResource {
		public PhysicsWorld() => B3Config.Freeze();

		public FVector3 Gravity = new(FP.Zero, -10 * B3Config.GetLengthUnitsPerMeter(), FP.Zero);

		public FP ContactHertz = FP.FromRatio(30, 1);
		public FP ContactDampingRatio = FP.FromRatio(10, 1);
		public FP ContactSpeed = 3 * B3Config.GetLengthUnitsPerMeter();
		public FP RestitutionThreshold = B3Config.GetLengthUnitsPerMeter();
		public FP HitEventThreshold = B3Config.GetLengthUnitsPerMeter();
		public FP MaximumLinearSpeed = 60 * B3Config.GetLengthUnitsPerMeter();
		public bool EnableWarmStarting = true;

		/// <summary>Number of solver sub-steps per full step. Box3d's usual default is 4.</summary>
		public int SubStepCount = 4;

		public Guid? Guid() => new("74401a12-82f2-4a68-9f03-f4e70640cbe7");
		public byte Version() => 1;

		public void Write(ref BinaryPackWriter writer) {
			PhysicsValidation.ValidateWorld(this);
			writer.WriteInt(Gravity.X.RawValue);
			writer.WriteInt(Gravity.Y.RawValue);
			writer.WriteInt(Gravity.Z.RawValue);
			writer.WriteInt(ContactHertz.RawValue);
			writer.WriteInt(ContactDampingRatio.RawValue);
			writer.WriteInt(ContactSpeed.RawValue);
			writer.WriteInt(RestitutionThreshold.RawValue);
			writer.WriteInt(HitEventThreshold.RawValue);
			writer.WriteInt(MaximumLinearSpeed.RawValue);
			writer.WriteBool(EnableWarmStarting);
			writer.WriteInt(SubStepCount);
			writer.WriteInt(B3Config.GetLengthUnitsPerMeter().RawValue);
		}

		public void Read(ref BinaryPackReader reader, byte version) {
			if (version != Version()) {
				throw new InvalidDataException($"Unsupported PhysicsWorld snapshot version {version}.");
			}

			var restored = new PhysicsWorld {
				Gravity = new FVector3(FP.FromRaw(reader.ReadInt()), FP.FromRaw(reader.ReadInt()), FP.FromRaw(reader.ReadInt())),
				ContactHertz = FP.FromRaw(reader.ReadInt()),
				ContactDampingRatio = FP.FromRaw(reader.ReadInt()),
				ContactSpeed = FP.FromRaw(reader.ReadInt()),
				RestitutionThreshold = FP.FromRaw(reader.ReadInt()),
				HitEventThreshold = FP.FromRaw(reader.ReadInt()),
				MaximumLinearSpeed = FP.FromRaw(reader.ReadInt()),
				EnableWarmStarting = reader.ReadBool(),
				SubStepCount = reader.ReadInt(),
			};
			var lengthUnits = FP.FromRaw(reader.ReadInt());
			if (lengthUnits != B3Config.GetLengthUnitsPerMeter()) {
				throw new InvalidDataException("Physics snapshot length scale does not match the initialized process configuration.");
			}
			PhysicsValidation.ValidateWorld(restored);
			Gravity = restored.Gravity;
			ContactHertz = restored.ContactHertz;
			ContactDampingRatio = restored.ContactDampingRatio;
			ContactSpeed = restored.ContactSpeed;
			RestitutionThreshold = restored.RestitutionThreshold;
			HitEventThreshold = restored.HitEventThreshold;
			MaximumLinearSpeed = restored.MaximumLinearSpeed;
			EnableWarmStarting = restored.EnableWarmStarting;
			SubStepCount = restored.SubStepCount;
		}
	}
}
