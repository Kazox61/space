using System;
using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;

namespace Space.GameCore;

public abstract partial class Core<TWorld> where TWorld : struct, ISessionType, IWorldType {
	public abstract class W : World<TWorld> { }

	public abstract class S : Session<TWorld> { }

	public struct GameSystemsType : ISystemsType { }
	public static readonly Guid GameSystemsSnapshotGuid = new("76e5ae3f-ce79-4689-8334-d7aca56540af");

	public abstract class Systems : W.Systems<GameSystemsType> { }
}
