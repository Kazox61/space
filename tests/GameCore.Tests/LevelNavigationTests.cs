using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FFS.Libraries.StaticEcs;
using NUnit.Framework;
using Shenanicode.Rollback;
using Shenanicode.Rollback.LiteNetLib;
using Space.NavBuilder;

namespace Space.GameCore.Tests;

public struct NavigationTestWorldA : IWorldType, ISessionType;

public struct NavigationTestWorldB : IWorldType, ISessionType;

[TestFixture]
[NonParallelizable]
public sealed class LevelNavigationTests {
	private static LevelData? s_baked;

	/// <summary>The sample level's static geometry and zones with navigation baked by the offline pipeline.</summary>
	private static LevelData Baked() {
		var sample = NavTestSession.Level();
		return s_baked ??= NavMeshBaker.BakeLevel(new LevelData(sample.Entities, navZones: sample.NavZones), NavBakeSettings.Default, out _);
	}

	[Test]
	public void NavigationRoundTripsByteIdentically() {
		var bytes = LevelDataCodec.Serialize(Baked());
		var decoded = LevelDataCodec.Deserialize(bytes);

		Assert.Multiple(() => {
			Assert.That(LevelDataCodec.Serialize(decoded), Is.EqualTo(bytes));
			Assert.That(decoded.Navigation, Is.Not.Null);
			Assert.That(decoded.Navigation!.Settings, Is.EqualTo(NavBakeSettings.Default));
			Assert.That(NavMeshBytes.Of(decoded.Navigation.Mesh.CreateMesh()), Is.EqualTo(NavMeshBytes.Of(Baked().Navigation!.Mesh.CreateMesh())));
		});
	}

	[Test]
	public void NavigationZonesMustMatchTheLevelsZoneVolumes() {
		var baked = Baked();
		var withoutVolumes = new LevelData(baked.Entities, baked.Navigation);
		var renamed = new LevelData(baked.Entities, baked.Navigation, baked.NavZones.Select(static zone => zone with { Id = "other" }));

		Assert.Multiple(() => {
			Assert.That(() => LevelDataCodec.Serialize(withoutVolumes), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("bake the level again"));
			Assert.That(() => LevelDataCodec.Serialize(renamed), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("bake the level again"));
		});
	}

	[Test]
	public void ZoneVolumesRoundTripWithoutNavigation() {
		var zone = Baked().NavZones[0];
		var level = new LevelData(TestLevels(), navZones: [zone with { Id = "b" }, zone with { Id = "a" }]);
		var bytes = LevelDataCodec.Serialize(level);
		var decoded = LevelDataCodec.Deserialize(bytes);

		Assert.Multiple(() => {
			Assert.That(decoded.NavZones.Select(static z => z.Id), Is.EqualTo(new[] { "a", "b" }), "ordinal by id");
			Assert.That(decoded.NavZones[0] with { Id = zone.Id }, Is.EqualTo(zone));
			Assert.That(LevelDataCodec.Serialize(decoded), Is.EqualTo(bytes));
			Assert.That(() => LevelDataCodec.Serialize(new LevelData([], navZones: [zone, zone])), Throws.InstanceOf<InvalidDataException>());
		});
	}

	[Test]
	public void ZoneTablesAreValidated() {
		var mesh = Baked().Navigation!.Mesh;
		var settings = NavBakeSettings.Default;

		Assert.Multiple(() => {
			Assert.That(() => new NavZoneData("a", []), Throws.ArgumentException, "empty");
			Assert.That(() => new NavZoneData("a", [2, 1]), Throws.ArgumentException, "unordered");
			Assert.That(() => new NavZoneData("a", [1, 1]), Throws.ArgumentException, "repeated");
			Assert.That(() => new NavZoneData(" ", [1]), Throws.ArgumentException, "blank id");
			Assert.That(() => new LevelNavigation(settings, mesh, [new NavZoneData("a", [mesh.TriangleCount])]), Throws.ArgumentException, "out of range");
			Assert.That(() => new LevelNavigation(settings, mesh, [new NavZoneData("a", [1]), new NavZoneData("b", [0, 1])]), Throws.ArgumentException, "shared triangle");
			Assert.That(() => new LevelNavigation(settings, mesh, [new NavZoneData("b", [1]), new NavZoneData("a", [0])]), Throws.ArgumentException, "unordered ids");
			Assert.That(() => new LevelNavigation(settings, mesh, [new NavZoneData("a", [1]), new NavZoneData("a", [0])]), Throws.ArgumentException, "repeated ids");
		});
	}

	[Test]
	public void LevelNavigationRejectsInvalidBakeSettingsAtConstruction() {
		Assert.That(
			() => new LevelNavigation(default, Baked().Navigation!.Mesh),
			Throws.InstanceOf<ArgumentOutOfRangeException>());
	}

	[Test]
	public void MalformedZoneStringLengthIsReportedAsInvalidData() {
		var zone = Baked().NavZones[0];
		var bytes = LevelDataCodec.Serialize(new LevelData([], navZones: [zone]));
		var zoneIdOffset = LevelDataCodec.EnvelopeSize + 2 * sizeof(int);
		new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x10 }.CopyTo(bytes, zoneIdOffset);
		Rehash(bytes);

		Assert.That(() => LevelDataCodec.Deserialize(bytes), Throws.TypeOf<InvalidDataException>());
	}

	[Test]
	public void LevelWithoutNavigationRoundTrips() {
		var level = new LevelData(TestLevels());
		var bytes = LevelDataCodec.Serialize(level);

		Assert.That(LevelDataCodec.Deserialize(bytes).Navigation, Is.Null);
		Assert.That(LevelDataCodec.Serialize(LevelDataCodec.Deserialize(bytes)), Is.EqualTo(bytes));
	}

	[Test]
	public void CommittedSampleLevelCarriesTheCurrentBake() {
		var navigation = NavTestSession.Level().Navigation;

		Assert.That(navigation, Is.Not.Null, "re-export the sample level");
		Assert.Multiple(() => {
			Assert.That(navigation!.Settings, Is.EqualTo(NavBakeSettings.Default));
			Assert.That(NavMeshBytes.Of(navigation.Mesh.CreateMesh()), Is.EqualTo(NavMeshBytes.Of(Baked().Navigation!.Mesh.CreateMesh())),
				"the committed navmesh differs from a fresh bake of the committed colliders; re-export the sample level");
			Assert.That(ZoneTable(navigation), Is.EqualTo(ZoneTable(Baked().Navigation!)), "the committed zone table differs from a fresh bake");
			Assert.That(navigation.Zones.Select(static zone => zone.Id), Is.EqualTo(new[] { "gate" }));
		});
	}

	[Test]
	public void ChangingOnlyTheNavmeshChangesTheConnectionKey() {
		var level = Baked();
		var coarser = NavMeshBaker.BakeLevel(level, NavBakeSettings.Default with { LookupCellSize = Fixed64.FP.FromRatio(8, 1) }, out _);

		var key = LevelFile.Read("a", LevelDataCodec.Serialize(level)).ConnectionKey;
		var otherKey = LevelFile.Read("b", LevelDataCodec.Serialize(coarser)).ConnectionKey;

		Assert.That(otherKey, Is.Not.EqualTo(key));
	}

	[TestCase(false, ExpectedResult = true, TestName = "PeersWithTheSameNavmeshConnect")]
	[TestCase(true, ExpectedResult = false, TestName = "PeersWithDifferentNavmeshesCannotConnect")]
	public bool LevelConnectionKeyGatesTheHandshake(bool clientNavmeshDiffers) {
		var level = Baked();
		var serverKey = LevelFile.Read("server", LevelDataCodec.Serialize(level)).ConnectionKey;
		var clientLevel = clientNavmeshDiffers
			? NavMeshBaker.BakeLevel(level, NavBakeSettings.Default with { LookupCellSize = Fixed64.FP.FromRatio(8, 1) }, out _)
			: level;
		var clientKey = LevelFile.Read("client", LevelDataCodec.Serialize(clientLevel)).ConnectionKey;

		LiteNetLibRemoteClientListener? listener = null;
		var port = 0;
		for (var attempt = 0; ; attempt++) {
			using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) {
				port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
			}
			listener = new LiteNetLibRemoteClientListener(port, serverKey);
			try {
				listener.Start();
				break;
			} catch (InvalidOperationException) {
				listener.Stop();
				if (attempt >= 3) {
					throw;
				}
			}
		}
		var client = new LiteNetLibServerConnection();
		try {
			client.Connect("127.0.0.1", port, clientKey);
			// A rejected handshake never completes, so wait out a generous window for the accepted case.
			var accepted = false;
			var deadline = DateTime.UtcNow.AddSeconds(3);
			while (DateTime.UtcNow < deadline && !(accepted && client.IsConnected)) {
				listener!.Poll();
				client.Poll();
				accepted |= listener.TryAccept(out _);
				Thread.Sleep(5);
			}
			return accepted && client.IsConnected;
		} finally {
			client.Close();
			listener!.Stop();
		}
	}

	[Test]
	public void EveryCorruptedNavmeshByteIsRejectedCleanlyOrStillValid() {
		var bytes = LevelDataCodec.Serialize(Baked());
		AssertEnvelope(bytes);
		// Without navigation the payload ends in the navigation flag byte, which is where the section starts.
		var navigationStart = LevelDataCodec.Serialize(Baked().WithNavigation(null)).Length - 1;
		var accepted = 0;
		var rejected = 0;

		for (var offset = navigationStart; offset < bytes.Length; offset++) {
			// One flip per offset keeps this fast; alternating low-bit and whole-byte flips still hits
			// both small value changes and wild ones in every field.
			foreach (var flip in new[] { offset % 2 == 0 ? (byte)0x01 : (byte)0xFF }) {
				var corrupted = (byte[])bytes.Clone();
				corrupted[offset] ^= flip;
				Rehash(corrupted);
				try {
					var decoded = LevelDataCodec.Deserialize(corrupted);
					Assert.That(LevelDataCodec.Serialize(decoded), Is.EqualTo(corrupted), $"offset {offset} flip {flip:X2} decoded but did not round-trip");
					accepted++;
				} catch (InvalidDataException) {
					rejected++;
				}
			}
		}

		Assert.That(rejected, Is.GreaterThan(0));
		Assert.That(accepted, Is.GreaterThan(0), "some bytes (coordinates) may legitimately change");
	}

	[TestCase(0, "vertex count")]
	[TestCase(1, "triangle count")]
	[TestCase(2, "grid triangle count")]
	public void OversizedCountsFailBeforeAllocation(int field, string description) {
		var bytes = LevelDataCodec.Serialize(Baked());
		AssertEnvelope(bytes);
		var navigation = Baked().Navigation!.Mesh;
		var navigationStart = LevelDataCodec.Serialize(Baked().WithNavigation(null)).Length - 1;
		var vertexCountOffset = navigationStart + 1 + LevelNavigationCodec.SettingsSize;
		var triangleCountOffset = vertexCountOffset + sizeof(int) + navigation.Vertices.Length * LevelNavigationCodec.VertexSize;
		var gridTriangleCountOffset = triangleCountOffset + sizeof(int)
			+ navigation.TriangleCount * LevelNavigationCodec.TriangleSize
			+ LevelNavigationCodec.MeshMetadataSize
			+ navigation.GridCells.Length * sizeof(int);
		var offset = field switch { 0 => vertexCountOffset, 1 => triangleCountOffset, _ => gridTriangleCountOffset };
		Assert.That(BitConverter.ToInt32(bytes, offset), Is.EqualTo(field switch {
			0 => navigation.Vertices.Length,
			1 => navigation.TriangleCount,
			_ => navigation.GridTriangles.Length,
		}), $"test layout for {description}");

		BitConverter.TryWriteBytes(bytes.AsSpan(offset), 999_999);
		Rehash(bytes);

		Assert.That(() => LevelDataCodec.Deserialize(bytes), Throws.TypeOf<InvalidDataException>().With.Message.Contains("truncated"));
	}

	[Test]
	public void EachWorldGetsItsOwnMeshFromTheSameLevel() {
		var level = LevelDataCodec.Deserialize(LevelDataCodec.Serialize(Baked()));
		try {
			NavTestSession.CreateWorld<NavigationTestWorldA>(level);
			NavTestSession.CreateWorld<NavigationTestWorldB>(level);
			var a = Core<NavigationTestWorldA>.Systems.GetResource<NavigationRes>();
			var b = Core<NavigationTestWorldB>.Systems.GetResource<NavigationRes>();

			Assert.That(a.HasMesh && b.HasMesh, Is.True);
			Assert.That(NavMeshBytes.Of(a.Mesh!), Is.EqualTo(NavMeshBytes.Of(b.Mesh!)));

			a.Mesh!.Areas[0].IsBlocked = true;

			Assert.Multiple(() => {
				Assert.That(b.Mesh!.Areas[0].IsBlocked, Is.False);
				Assert.That(level.Navigation!.Mesh.Areas[0].IsBlocked, Is.False);
				Assert.That(a.Pathfinder, Is.Not.Null);
				Assert.That(a.Funnel, Is.Not.Null);
			});
		} finally {
			NavTestSession.DestroyIfCreated<NavigationTestWorldA>();
			NavTestSession.DestroyIfCreated<NavigationTestWorldB>();
		}
	}

	[Test]
	public void WorldWithoutNavigationHasAnEmptyResource() {
		try {
			NavTestSession.CreateWorld<NavigationTestWorldA>(new LevelData(TestLevels()));

			Assert.That(Core<NavigationTestWorldA>.Systems.GetResource<NavigationRes>().HasMesh, Is.False);
		} finally {
			NavTestSession.DestroyIfCreated<NavigationTestWorldA>();
		}
	}

	private static string[] ZoneTable(LevelNavigation navigation) {
		return navigation.Zones.Select(static zone => $"{zone.Id}: {string.Join(',', zone.Triangles.ToArray())}").ToArray();
	}

	private static void Rehash(byte[] level) {
		SHA256.HashData(level.AsSpan(LevelDataCodec.EnvelopeSize)).CopyTo(level, LevelDataCodec.HashOffset);
	}

	private static void AssertEnvelope(byte[] level) {
		Assert.Multiple(() => {
			Assert.That(BitConverter.ToInt32(level, sizeof(uint) + sizeof(ushort)), Is.EqualTo(level.Length - LevelDataCodec.EnvelopeSize), "payload length offset");
			Assert.That(level.AsSpan(LevelDataCodec.HashOffset, LevelDataCodec.HashSize).SequenceEqual(SHA256.HashData(level.AsSpan(LevelDataCodec.EnvelopeSize))), Is.True, "hash offset");
		});
	}

	private static EntityPlacement[] TestLevels() {
		return PhysicsSmokeTest.TestLevels.Arena.Entities.ToArray();
	}

}
