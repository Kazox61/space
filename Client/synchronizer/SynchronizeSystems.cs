using FFS.Libraries.StaticEcs;
using Shenanicode.Rollback;
using Space.GameCore;
using static Space.GameCore.Core<Space.Client.ClientWorld>;

namespace Space.Client;

public struct SynchronizeSystems : ISystemsType { }

public abstract class SynchronizeSys : W.Systems<SynchronizeSystems> { }
