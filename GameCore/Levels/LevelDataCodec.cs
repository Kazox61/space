using System.Security.Cryptography;
using System.Text;
using Fixed;
using Fixed32;

namespace Space.GameCore;

public static class LevelDataCodec {
	private const uint Magic = 0x4C564C53; // "SLVL" in little-endian byte order.
	private const ushort Version = 2;
	private const int HashSize = 32;
	private const int MaximumSourcePathLength = 1024;
	private const int MaximumEntityCount = 100_000;
	private const int MaximumStaticBoxCount = 100_000;
	private const int MaximumPayloadSize = 64 * 1024 * 1024;

	public static byte[] Serialize(LevelData level) {
		var ordered = level.Entities.OrderBy(static entity => entity.SourcePath, StringComparer.Ordinal).ToArray();
		if (ordered.Length > MaximumEntityCount) {
			throw new InvalidDataException($"Level contains {ordered.Length} entities; maximum is {MaximumEntityCount}.");
		}
		var orderedBoxes = level.StaticBoxes.OrderBy(static box => box.SourcePath, StringComparer.Ordinal).ToArray();
		if (orderedBoxes.Length > MaximumStaticBoxCount) {
			throw new InvalidDataException($"Level contains {orderedBoxes.Length} static boxes; maximum is {MaximumStaticBoxCount}.");
		}

		using var payloadStream = new MemoryStream();
		using (var writer = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true)) {
			writer.Write(ordered.Length);
			var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
			foreach (var placement in ordered) {
				if (string.IsNullOrWhiteSpace(placement.SourcePath)
					|| placement.SourcePath.Length > MaximumSourcePathLength
					|| !sourcePaths.Add(placement.SourcePath)) {
					throw new InvalidDataException($"Level entity source path '{placement.SourcePath}' is empty, too long, or duplicated.");
				}
				LevelDataValidation.Validate(placement);
				WritePlacement(writer, placement);
			}

			writer.Write(orderedBoxes.Length);
			sourcePaths.Clear();
			foreach (var box in orderedBoxes) {
				if (string.IsNullOrWhiteSpace(box.SourcePath)
					|| box.SourcePath.Length > MaximumSourcePathLength
					|| !sourcePaths.Add(box.SourcePath)) {
					throw new InvalidDataException($"Level static box source path '{box.SourcePath}' is empty, too long, or duplicated.");
				}
				LevelDataValidation.Validate(box);
				WriteStaticBox(writer, box);
			}
		}

		var payload = payloadStream.ToArray();
		if (payload.Length > MaximumPayloadSize) {
			throw new InvalidDataException($"Level payload exceeds the {MaximumPayloadSize}-byte limit.");
		}
		var hash = SHA256.HashData(payload);
		using var output = new MemoryStream(payload.Length + 42);
		using var envelope = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
		envelope.Write(Magic);
		envelope.Write(Version);
		envelope.Write(payload.Length);
		envelope.Write(hash);
		envelope.Write(payload);
		return output.ToArray();
	}

	public static LevelData Deserialize(ReadOnlySpan<byte> bytes) {
		if (bytes.Length > MaximumPayloadSize + 42) {
			throw new InvalidDataException($"Level data exceeds the {MaximumPayloadSize}-byte payload limit.");
		}
		try {
			using var stream = new MemoryStream(bytes.ToArray(), writable: false);
			using var reader = new BinaryReader(stream, Encoding.UTF8);
			if (reader.ReadUInt32() != Magic) {
				throw new InvalidDataException("Level has an invalid magic header.");
			}
			var version = reader.ReadUInt16();
			if (version != Version) {
				throw new InvalidDataException($"Level version {version} is unsupported; expected {Version}.");
			}

			var payloadLength = reader.ReadInt32();
			if (payloadLength is < 0 or > MaximumPayloadSize || payloadLength != stream.Length - stream.Position - HashSize) {
				throw new InvalidDataException("Level payload length is invalid.");
			}
			var expectedHash = reader.ReadBytes(HashSize);
			if (expectedHash.Length != HashSize) {
				throw new InvalidDataException("Level hash is truncated.");
			}
			var payload = reader.ReadBytes(payloadLength);
			if (payload.Length != payloadLength || !CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(payload))) {
				throw new InvalidDataException("Level content hash does not match its payload.");
			}

			using var payloadStream = new MemoryStream(payload, writable: false);
			using var payloadReader = new BinaryReader(payloadStream, Encoding.UTF8);
			var count = payloadReader.ReadInt32();
			if (count is < 0 or > MaximumEntityCount) {
				throw new InvalidDataException($"Level entity count {count} is invalid.");
			}

			var placements = new EntityPlacement[count];
			string? previousSourcePath = null;
			for (var i = 0; i < count; i++) {
				placements[i] = ReadPlacement(payloadReader, i);
				if (previousSourcePath is not null
					&& StringComparer.Ordinal.Compare(previousSourcePath, placements[i].SourcePath) >= 0) {
					throw new InvalidDataException("Level entity source paths are duplicated or not canonically ordered.");
				}
				previousSourcePath = placements[i].SourcePath;
				LevelDataValidation.Validate(placements[i]);
			}

			var boxCount = payloadReader.ReadInt32();
			if (boxCount is < 0 or > MaximumStaticBoxCount) {
				throw new InvalidDataException($"Level static box count {boxCount} is invalid.");
			}
			var boxes = new StaticBox[boxCount];
			previousSourcePath = null;
			for (var i = 0; i < boxCount; i++) {
				boxes[i] = ReadStaticBox(payloadReader, i);
				if (previousSourcePath is not null
					&& StringComparer.Ordinal.Compare(previousSourcePath, boxes[i].SourcePath) >= 0) {
					throw new InvalidDataException("Level static box source paths are duplicated or not canonically ordered.");
				}
				previousSourcePath = boxes[i].SourcePath;
				LevelDataValidation.Validate(boxes[i]);
			}
			if (payloadStream.Position != payloadStream.Length) {
				throw new InvalidDataException("Level payload contains trailing data.");
			}
			return new LevelData(placements, boxes);
		} catch (EndOfStreamException exception) {
			throw new InvalidDataException("Level data is truncated.", exception);
		}
	}

	public static string ContentHash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

	private static void WritePlacement(BinaryWriter writer, in EntityPlacement placement) {
		writer.Write(placement.SourcePath);
		writer.Write((byte)placement.Type);
		WriteTransform(writer, placement.Transform);
		writer.Write(placement.Crate.Health);
		writer.Write((byte)placement.Crate.Loot);
		writer.Write((byte)placement.Crate.BodyType);
		writer.Write(placement.Crate.BoxHalfExtents.X.RawValue);
		writer.Write(placement.Crate.BoxHalfExtents.Y.RawValue);
		writer.Write(placement.Crate.BoxHalfExtents.Z.RawValue);
		writer.Write(placement.Crate.Density.RawValue);
		writer.Write((int)placement.Crate.View);
	}

	private static EntityPlacement ReadPlacement(BinaryReader reader, int index) {
		var sourcePath = ReadSourcePath(reader, "entity", index);
		return new EntityPlacement(
			sourcePath,
			(LevelEntityType)reader.ReadByte(),
			ReadTransform(reader),
			new CratePlacementData(
				reader.ReadInt32(),
				(LootKind)reader.ReadByte(),
				(BodyType)reader.ReadByte(),
				new FVector3(FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32())),
				FP.FromRaw(reader.ReadInt32()),
				(ViewAsset)reader.ReadInt32()
			)
		);
	}

	private static void WriteStaticBox(BinaryWriter writer, in StaticBox box) {
		writer.Write(box.SourcePath);
		WriteTransform(writer, box.Transform);
		writer.Write(box.HalfExtents.X.RawValue);
		writer.Write(box.HalfExtents.Y.RawValue);
		writer.Write(box.HalfExtents.Z.RawValue);
	}

	private static StaticBox ReadStaticBox(BinaryReader reader, int index) => new(
		ReadSourcePath(reader, "static box", index),
		ReadTransform(reader),
		new FVector3(FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32()))
	);

	private static string ReadSourcePath(BinaryReader reader, string kind, int index) {
		var sourcePath = reader.ReadString();
		if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath.Length > MaximumSourcePathLength) {
			throw new InvalidDataException($"Level {kind} {index} has an invalid source path.");
		}
		return sourcePath;
	}

	private static void WriteTransform(BinaryWriter writer, in FWorldTransform transform) {
		writer.Write(transform.Position.X.RawValue);
		writer.Write(transform.Position.Y.RawValue);
		writer.Write(transform.Position.Z.RawValue);
		writer.Write(transform.Rotation.X.RawValue);
		writer.Write(transform.Rotation.Y.RawValue);
		writer.Write(transform.Rotation.Z.RawValue);
		writer.Write(transform.Rotation.W.RawValue);
	}

	private static FWorldTransform ReadTransform(BinaryReader reader) => new(
		new FPos(
			Fixed64.FP.FromRaw(reader.ReadInt64()),
			Fixed64.FP.FromRaw(reader.ReadInt64()),
			Fixed64.FP.FromRaw(reader.ReadInt64())
		),
		new FQuaternion(
			FP.FromRaw(reader.ReadInt32()),
			FP.FromRaw(reader.ReadInt32()),
			FP.FromRaw(reader.ReadInt32()),
			FP.FromRaw(reader.ReadInt32())
		)
	);
}
