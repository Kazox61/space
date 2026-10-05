using System.Security.Cryptography;
using System.Text;
using Fixed;
using Fixed32;

namespace Space.GameCore;

public static class LevelDataCodec {
	private static readonly Encoding s_strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	private const uint Magic = 0x4C564C53; // "SLVL" in little-endian byte order.
	private const ushort Version = 9;
	private const ushort PreviousVersion = 7;
	internal const int HashSize = 32;
	internal const int HashOffset = sizeof(uint) + sizeof(ushort) + sizeof(int);
	internal const int EnvelopeSize = HashOffset + HashSize;
	private const int MaximumSourcePathLength = 1024;
	private const int MaximumEntityCount = 100_000;
	private const int MaximumNavZoneCount = NavZoneData.MaxZones;
	private const int MaximumPayloadSize = 64 * 1024 * 1024;
	private const int ComponentKindCount = (int)LevelEntityComponentKind.SurfaceProperties + 1;
	private const int KnownComponentMask = (1 << ComponentKindCount) - 1;
	private const int PreviousKnownComponentMask = (1 << (int)LevelEntityComponentKind.RailMotion) - 1;

	public static byte[] Serialize(LevelData level) {
		var ordered = level.Entities.OrderBy(static entity => entity.SourcePath, StringComparer.Ordinal).ToArray();
		if (ordered.Length > MaximumEntityCount) {
			throw new InvalidDataException($"Level contains {ordered.Length} entities; maximum is {MaximumEntityCount}.");
		}
		var orderedZones = OrderZones(level.NavZones);
		ValidateNavigationZones(orderedZones, level.Navigation);
		LevelDataValidation.ValidateZoneLinks(ordered, orderedZones);

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

			writer.Write(orderedZones.Length);
			foreach (var zone in orderedZones) {
				WriteNavZone(writer, zone);
			}

			LevelNavigationCodec.Write(writer, level.Navigation);
		}

		var payload = payloadStream.ToArray();
		if (payload.Length > MaximumPayloadSize) {
			throw new InvalidDataException($"Level payload exceeds the {MaximumPayloadSize}-byte limit.");
		}
		var hash = SHA256.HashData(payload);
		using var output = new MemoryStream(payload.Length + EnvelopeSize);
		using var envelope = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
		envelope.Write(Magic);
		envelope.Write(Version);
		envelope.Write(payload.Length);
		envelope.Write(hash);
		envelope.Write(payload);
		return output.ToArray();
	}

	public static LevelData Deserialize(ReadOnlySpan<byte> bytes) {
		if (bytes.Length > MaximumPayloadSize + EnvelopeSize) {
			throw new InvalidDataException($"Level data exceeds the {MaximumPayloadSize}-byte payload limit.");
		}
		try {
			using var stream = new MemoryStream(bytes.ToArray(), writable: false);
			using var reader = new BinaryReader(stream, Encoding.UTF8);
			if (reader.ReadUInt32() != Magic) {
				throw new InvalidDataException("Level has an invalid magic header.");
			}
			var version = reader.ReadUInt16();
			if (version < PreviousVersion || version > Version) {
				throw new InvalidDataException($"Level version {version} is unsupported; expected {PreviousVersion} or {Version}.");
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
				placements[i] = ReadPlacement(payloadReader, i, version);
				if (previousSourcePath is not null
					&& StringComparer.Ordinal.Compare(previousSourcePath, placements[i].SourcePath) >= 0) {
					throw new InvalidDataException("Level entity source paths are duplicated or not canonically ordered.");
				}
				previousSourcePath = placements[i].SourcePath;
				LevelDataValidation.Validate(placements[i]);
			}

			var zoneCount = payloadReader.ReadInt32();
			if (zoneCount is < 0 or > MaximumNavZoneCount) {
				throw new InvalidDataException($"Level navigation zone count {zoneCount} is invalid.");
			}
			var zones = new NavZoneVolume[zoneCount];
			for (var i = 0; i < zoneCount; i++) {
				zones[i] = ReadNavZone(payloadReader);
				if (i > 0 && StringComparer.Ordinal.Compare(zones[i - 1].Id, zones[i].Id) >= 0) {
					throw new InvalidDataException("Level navigation zone ids are duplicated or not canonically ordered.");
				}
				LevelDataValidation.Validate(zones[i]);
			}

			var navigation = LevelNavigationCodec.Read(payloadReader);
			if (payloadStream.Position != payloadStream.Length) {
				throw new InvalidDataException("Level payload contains trailing data.");
			}
			ValidateNavigationZones(zones, navigation);
			LevelDataValidation.ValidateZoneLinks(placements, zones);
			return new LevelData(placements, navigation, zones);
		} catch (EndOfStreamException exception) {
			throw new InvalidDataException("Level data is truncated.", exception);
		} catch (FormatException exception) {
			throw new InvalidDataException("Level data contains malformed encoded data.", exception);
		} catch (DecoderFallbackException exception) {
			throw new InvalidDataException("Level data contains invalid UTF-8.", exception);
		} catch (IOException exception) {
			throw new InvalidDataException("Level data could not be decoded.", exception);
		}
	}

	public static string ContentHash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

	/// <remarks>
	/// Source path, type, transform, then a 16-bit mask with bit <c>1 &lt;&lt; kind</c> set per present
	/// <see cref="LevelEntityComponentKind"/>, then each present component in kind order.
	/// </remarks>
	private static void WritePlacement(BinaryWriter writer, in EntityPlacement placement) {
		writer.Write(placement.SourcePath);
		writer.Write((byte)placement.Type);
		WriteTransform(writer, placement.Transform);
		var components = placement.Components;
		ushort mask = 0;
		for (var kind = 0; kind < ComponentKindCount; kind++) {
			if (components.Has((LevelEntityComponentKind)kind)) {
				mask |= (ushort)(1 << kind);
			}
		}
		writer.Write(mask);
		if (components.Health is { } health) {
			writer.Write(health);
		}
		if (components.Loot is { } loot) {
			writer.Write((byte)loot);
		}
		if (components.Body is { } body) {
			writer.Write((byte)body);
		}
		if (components.BoxShape is { } box) {
			writer.Write(box.HalfExtents.X.RawValue);
			writer.Write(box.HalfExtents.Y.RawValue);
			writer.Write(box.HalfExtents.Z.RawValue);
			writer.Write(box.Density.RawValue);
		}
		if (components.View is { } view) {
			writer.Write((int)view);
		}
		if (components.Navigation is { } navigation) {
			writer.Write((byte)navigation);
		}
		if (components.ZoneLink is { } zoneLink) {
			writer.Write(zoneLink);
		}
		if (components.DoorMotion is { } motion) {
			writer.Write(motion.OpenOffset.X.RawValue);
			writer.Write(motion.OpenOffset.Y.RawValue);
			writer.Write(motion.OpenOffset.Z.RawValue);
			writer.Write(motion.Speed.RawValue);
			writer.Write(motion.StartsOpen);
		}
		if (components.RailMotion is { } rail) {
			writer.Write(rail.TravelOffset.X.RawValue);
			writer.Write(rail.TravelOffset.Y.RawValue);
			writer.Write(rail.TravelOffset.Z.RawValue);
			writer.Write(rail.Speed.RawValue);
			writer.Write(rail.StartsAtEnd);
		}
		if (components.SphereShape is { } sphere) {
			writer.Write(sphere.Radius.RawValue);
			writer.Write(sphere.Density.RawValue);
		}
		if (components.SurfaceProperties is { } surface) {
			writer.Write(surface.Material.Friction.RawValue);
			writer.Write(surface.Material.Restitution.RawValue);
			writer.Write(surface.Material.RollingResistance.RawValue);
			writer.Write(surface.Material.TangentVelocity.X.RawValue);
			writer.Write(surface.Material.TangentVelocity.Y.RawValue);
			writer.Write(surface.Material.TangentVelocity.Z.RawValue);
			writer.Write(surface.Material.UserMaterialId);
			writer.Write(surface.Material.CustomColor);
			writer.Write(surface.CharacterBounceSpeed.RawValue);
		}
	}

	private static EntityPlacement ReadPlacement(BinaryReader reader, int index, ushort version) {
		var sourcePath = ReadSourcePath(reader, "entity", index);
		var type = (LevelEntityType)reader.ReadByte();
		var transform = ReadTransform(reader);
		var mask = reader.ReadUInt16();
		var knownMask = version switch {
			7 => PreviousKnownComponentMask,
			8 => (1 << ((int)LevelEntityComponentKind.RailMotion + 1)) - 1,
			_ => KnownComponentMask,
		};
		if ((mask & ~knownMask) != 0) {
			throw new InvalidDataException($"{sourcePath}: unknown entity components in mask 0x{mask:X4}.");
		}
		bool Has(LevelEntityComponentKind kind) => (mask & (1 << (int)kind)) != 0;
		return new EntityPlacement(sourcePath, type, transform, new PlacementComponents(
			Health: Has(LevelEntityComponentKind.Health) ? reader.ReadInt32() : null,
			Loot: Has(LevelEntityComponentKind.Loot) ? (LootKind)reader.ReadByte() : null,
			Body: Has(LevelEntityComponentKind.Body) ? (BodyType)reader.ReadByte() : null,
			BoxShape: Has(LevelEntityComponentKind.BoxShape)
				? new BoxShapeData(
					new FVector3(FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32())),
					FP.FromRaw(reader.ReadInt32())
				)
				: null,
			View: Has(LevelEntityComponentKind.View) ? (ViewAsset)reader.ReadInt32() : null,
			Navigation: Has(LevelEntityComponentKind.Navigation) ? (NavContribution)reader.ReadByte() : null,
			ZoneLink: Has(LevelEntityComponentKind.ZoneLink) ? ReadBoundedString(reader, NavZoneData.MaxIdLength, $"{sourcePath} zone link") : null,
			DoorMotion: Has(LevelEntityComponentKind.DoorMotion)
				? new DoorMotionData(
					new FVector3(FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32())),
					FP.FromRaw(reader.ReadInt32()),
					reader.ReadByte() switch {
						0 => false,
						1 => true,
						var value => throw new InvalidDataException($"{sourcePath}: invalid door start state {value}."),
					}
				)
				: null,
			RailMotion: Has(LevelEntityComponentKind.RailMotion)
				? new RailMotionData(
					new FVector3(FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32())),
					FP.FromRaw(reader.ReadInt32()),
					reader.ReadByte() switch {
						0 => false,
						1 => true,
						var value => throw new InvalidDataException($"{sourcePath}: invalid rail start state {value}."),
					}
				)
				: null,
			SphereShape: Has(LevelEntityComponentKind.SphereShape)
				? new SphereShapeData(FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32())) : null,
			SurfaceProperties: Has(LevelEntityComponentKind.SurfaceProperties)
				? new SurfacePropertiesData(new SurfaceMaterial {
					Friction = FP.FromRaw(reader.ReadInt32()),
					Restitution = FP.FromRaw(reader.ReadInt32()),
					RollingResistance = FP.FromRaw(reader.ReadInt32()),
					TangentVelocity = new FVector3(FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32())),
					UserMaterialId = reader.ReadUInt64(),
					CustomColor = reader.ReadUInt32(),
				}, FP.FromRaw(reader.ReadInt32())) : null
		));
	}

	private static NavZoneVolume[] OrderZones(IReadOnlyList<NavZoneVolume> zones) {
		var ordered = zones.OrderBy(static zone => zone.Id, StringComparer.Ordinal).ToArray();
		if (ordered.Length > MaximumNavZoneCount) {
			throw new InvalidDataException($"Level contains {ordered.Length} navigation zones; maximum is {MaximumNavZoneCount}.");
		}
		for (var i = 0; i < ordered.Length; i++) {
			LevelDataValidation.Validate(ordered[i]);
			if (i > 0 && StringComparer.Ordinal.Equals(ordered[i - 1].Id, ordered[i].Id)) {
				throw new InvalidDataException($"Navigation zone id '{ordered[i].Id}' is duplicated.");
			}
		}
		return ordered;
	}

	/// <summary>A baked navmesh carries exactly the level's zones, in the same order.</summary>
	private static void ValidateNavigationZones(IReadOnlyList<NavZoneVolume> zones, LevelNavigation? navigation) {
		if (navigation is null) {
			return;
		}
		var matches = navigation.Zones.Count == zones.Count;
		for (var i = 0; matches && i < zones.Count; i++) {
			matches = StringComparer.Ordinal.Equals(navigation.Zones[i].Id, zones[i].Id);
		}
		if (!matches) {
			throw new InvalidDataException("Level navigation zones do not match the level's navigation zone volumes; bake the level again.");
		}
	}

	private static void WriteNavZone(BinaryWriter writer, in NavZoneVolume zone) {
		writer.Write(zone.Id);
		WriteTransform(writer, zone.Transform);
		writer.Write(zone.HalfExtents.X.RawValue);
		writer.Write(zone.HalfExtents.Y.RawValue);
		writer.Write(zone.HalfExtents.Z.RawValue);
	}

	private static NavZoneVolume ReadNavZone(BinaryReader reader) => new(
		ReadBoundedString(reader, NavZoneData.MaxIdLength, "Level navigation zone id"),
		ReadTransform(reader),
		new FVector3(FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32()), FP.FromRaw(reader.ReadInt32()))
	);

	private static string ReadSourcePath(BinaryReader reader, string kind, int index) {
		var sourcePath = ReadBoundedString(reader, MaximumSourcePathLength, $"Level {kind} {index} source path");
		if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath.Length > MaximumSourcePathLength) {
			throw new InvalidDataException($"Level {kind} {index} has an invalid source path.");
		}
		return sourcePath;
	}

	internal static string ReadBoundedString(BinaryReader reader, int maximumCharacters, string context) {
		var byteLength = 0;
		for (var shift = 0; shift < 35; shift += 7) {
			var value = reader.ReadByte();
			if (shift == 28 && (value & 0xF8) != 0) {
				throw new InvalidDataException($"{context} has a malformed length prefix.");
			}
			byteLength |= (value & 0x7F) << shift;
			if ((value & 0x80) == 0) {
				var maximumBytes = Encoding.UTF8.GetMaxByteCount(maximumCharacters);
				if (byteLength > maximumBytes || byteLength > reader.BaseStream.Length - reader.BaseStream.Position) {
					throw new InvalidDataException($"{context} length is invalid.");
				}
				var bytes = reader.ReadBytes(byteLength);
				var text = s_strictUtf8.GetString(bytes);
				if (text.Length > maximumCharacters) {
					throw new InvalidDataException($"{context} exceeds {maximumCharacters} characters.");
				}
				return text;
			}
		}
		throw new InvalidDataException($"{context} has a malformed length prefix.");
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
