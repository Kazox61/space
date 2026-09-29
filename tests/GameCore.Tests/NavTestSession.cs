using System.Runtime.InteropServices;
using FFS.Libraries.StaticEcs;
using FFS.Libraries.StaticPack;
using NUnit.Framework;
using Shenanicode.Rollback;

namespace Space.GameCore.Tests;

/// <summary>Game worlds on the committed sample level, set up the way client and server run them.</summary>
public static class NavTestSession {
	private static LevelData? s_level;
	private static LevelData? s_doorLevel;

	public static void Step<TWorld>() where TWorld : struct, IWorldType, ISessionType {
		Core<TWorld>.S.FastForwardToTick(Core<TWorld>.S.CurrentTick + 1);
	}

	public static World<TWorld>.Entity FindCharacter<TWorld>() where TWorld : struct, IWorldType {
		foreach (var entity in World<TWorld>.Query<All<NavAgent>>().Entities()) {
			return entity;
		}
		throw new AssertionException("no navigation character was spawned");
	}

	public static byte[] Bytes<T>(T value) where T : unmanaged {
		return MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray();
	}

	/// <summary>The committed sample level's static geometry, zones and navmesh, without its crates, door and plate.</summary>
	public static LevelData Level() {
		return s_level ??= Sample(static entity => entity.Type == LevelEntityType.StaticGeometry);
	}

	/// <summary>The committed sample level without its crates: static geometry, the gate door and its plate.</summary>
	public static LevelData DoorLevel() {
		return s_doorLevel ??= Sample(static entity => entity.Type != LevelEntityType.Crate);
	}

	private static LevelData Sample(Func<EntityPlacement, bool> keep) {
		for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent) {
			var candidate = Path.Combine(directory.FullName, "Client", "maps", "level_pipeline_test.level.bytes");
			if (File.Exists(candidate)) {
				var data = LevelFile.ReadFromDisk(candidate).Data;
				return new LevelData(data.Entities.Where(keep), data.Navigation, data.NavZones);
			}
		}
		throw new FileNotFoundException("Could not find the sample level file.");
	}

	/// <summary>The same world setup client and server run.</summary>
	public static void CreateWorld<TWorld>(LevelData level, SimulationType simulationType = SimulationType.ForwardOnly, Action? registerSystems = null) where TWorld : struct, IWorldType, ISessionType {
		Core<TWorld>.S.Create(simulationType, Core<TWorld>.GameSessionSetup.SessionConfig);
		Core<TWorld>.S.Types().Signal<PlayerConnectedSignal>().Signal<PlayerDisconnectedSignal>();
		Core<TWorld>.GameSessionSetup.Register();
		Core<TWorld>.S.Initialize();
		Core<TWorld>.GameWorldSetup.CreateAndInitialize(level, registerSystems);
	}

	public static void DestroyIfCreated<TWorld>() where TWorld : struct, IWorldType, ISessionType {
		Core<TWorld>.RollbackObserver = null;
		if (World<TWorld>.Status == WorldStatus.Initialized) {
			Core<TWorld>.GameWorldSetup.Destroy();
		}
		if (Core<TWorld>.S.IsSessionCreated) {
			Core<TWorld>.S.Destroy();
		}
	}

	/// <summary>A snapshot-sized buffer for <see cref="WorldHash{TWorld}"/>.</summary>
	public static BinaryPackWriter SnapshotWriter<TWorld>() where TWorld : struct, IWorldType, ISessionType {
		return BinaryPackWriter.Create(new byte[Core<TWorld>.GameWorldRollback.WorldSnapshotLength]);
	}

	/// <summary>FNV-1a 64 over the whole world snapshot: everything rollback restores.</summary>
	public static ulong WorldHash<TWorld>(ref BinaryPackWriter writer) where TWorld : struct, IWorldType, ISessionType {
		writer.Position = 0;
		World<TWorld>.Serializer.CreateWorldSnapshot(ref writer);
		var hash = 14695981039346656037UL;
		foreach (var b in writer.Buffer.AsSpan(0, (int)writer.Position)) {
			hash ^= b;
			hash *= 1099511628211UL;
		}
		return hash;
	}
}
