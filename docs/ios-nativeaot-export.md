# NativeAOT (iOS) compatibility: StaticEcs and static-rollback

**Audience:** maintainers of [StaticEcs](https://github.com/Felid-Force-Studios/StaticEcs) and
[static-rollback](https://github.com/nilpunch/static-rollback), and our future selves.
**Status:** all issues below are fixed locally (patches included) and verified on device and with a macOS NativeAOT repro.
**Versions:** StaticEcs `d06e6fe` (v2.2.8+4), static-rollback `de524da`, FFS.StaticPack 1.2.6, Godot 4.7.2 (.NET 8, `PublishAot=true`).

## Summary

Godot compiles C# for iOS with **NativeAOT**. Both libraries construct generic types or probe for methods
**through reflection** (`MakeGenericType`, `MakeGenericMethod`, `Type.GetMethods()`), and wrap those calls in
`try/catch` blocks that return `null`/`false` on failure. Under NativeAOT such calls only succeed for generic
instantiations the compiler already emitted from static code, and method metadata only exists if something made
the type reflection‑visible. The result on iOS was a `SIGSEGV`, a `NotSupportedException`, an exception on the
first network write, and — worst — snapshots that silently deserialized to zeros.

| # | Library | Location | What fails under NativeAOT | Effect |
|---|---|---|---|---|
| 1 | StaticEcs | `AutoRegistration.RegisterAll` → `EntityTypeType<T>.HasOnCreate()` | `typeof(T).GetMethods()` does not return `OnCreate<TWorld>` unless the type is reflection‑visible | `OnCreate` never runs; later `Ref<T>()` segfaults |
| 2 | StaticEcs | `AutoRegistration.GetAutoRegisterGenericMethod` | `MakeGenericType(World<W>.Components<>, W, T)` for a `T` that no static code uses in world `W` | `NotSupportedException: … is missing native code or metadata` |
| 3 | StaticEcs | `AutoRegistration.TryCreateUnmanagedPackArrayStrategy<T>` | `MakeGenericType(UnmanagedPackArrayStrategy<>, T)` + `Activator.CreateInstance` | Returns `null` for **every** component → fallback strategy writes nothing → snapshot restores default values |
| 4 | static-rollback | `TypeUtils.TryRegisterUnmanagedPacking<T>` | `MakeGenericMethod` to reach an `unmanaged`‑constrained method from a `struct`‑constrained one | Returns `false` → `Method Write not implemented for input type PlayerConnectedSignal` |

The common thread: **a reflection call that can only work under a JIT, guarded by a catch that hides the
failure.** Under NativeAOT the guard turns a loud error into silent misbehavior. Each fix below replaces the
reflection with a path that is generic over the type parameter, so the AOT compiler emits it statically.

---

## Background: what NativeAOT can and cannot do

* **No runtime code generation.** `MakeGenericType` / `MakeGenericMethod` on value‑type arguments work only if the
  exact instantiation was compiled ahead of time because some static code referenced it. Otherwise they throw
  `NotSupportedException` ("missing native code or metadata").
* **Reflection metadata is trimmed.** `Type.GetMethods()` returns only methods the compiler decided to keep. A
  method that is *called* statically (e.g. `Dummy.OnCreate<ServerWorld>` via a constrained call) is compiled but
  is **not** automatically visible to `GetMethods()`. It becomes visible when the type flows into a parameter or
  type argument annotated with `[DynamicallyAccessedMembers]`, or when a `typeof(T).GetMethods()` call on that
  concrete type is visible to the compiler's dataflow analysis.
* `RuntimeHelpers.IsReferenceOrContainsReferences<T>()` is a JIT‑time/AOT‑time constant and is the supported
  way to branch on "is this struct blittable" from a `where T : struct` context.

Docs: <https://learn.microsoft.com/dotnet/core/deploying/native-aot/> and
<https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/reflection-in-aot-mode.md>.

---

## Issue 1 + 2 — StaticEcs `RegisterAll`

### Code (`Src/Lib.cs`, `AutoRegistration`)

```csharp
foreach (var type in assembly.GetTypes()) {
    ...
    if (typeof(IComponent).IsAssignableFrom(type))
        GetAutoRegisterGenericMethod(type, componentsOpenType, tWorld).Invoke(null, new[] { (object)false });
    ...
    if (typeof(IEntityType).IsAssignableFrom(type) && type != typeof(Default)) {
        try { GetAutoRegisterGenericMethod(type, entityTypeOpenType, tWorld).Invoke(null, null); }
        catch (Exception) { throw new StaticEcsException(...); }
    }
}

internal static MethodInfo GetAutoRegisterGenericMethod(Type type, Type openType, Type tWorld) {
    var genericType = openType.MakeGenericType(tWorld, type);          // (2) needs a compiled instantiation
    return genericType.GetMethod("AutoRegister", ...);
}
```

and, reached from `EntityTypeInfo<T>.AutoRegister()` → `RegisterEntityTypeInternal<T>()`:

```csharp
internal static class EntityTypeType<[DynamicallyAccessedMembers(PublicMethods | NonPublicMethods)] T> {
    internal static bool HasOnCreate() => HasMethod(typeof(T), nameof(IEntityType.OnCreate));
    private static bool HasMethod(Type structType, string methodName) {
        var methods = structType.GetMethods(Instance | Public | DeclaredOnly);   // (1) metadata may be trimmed
        foreach (var m in methods) if (m.Name == methodName && m.IsGenericMethodDefinition) return true;
        return false;
    }
}
```

### What happens under NativeAOT

**(2)** `MakeGenericType(World<W>.Components<>, W, T)` throws for any `(W, T)` pair that no static code
instantiates. We have a world (`GameWorldPrev`) that is only ever filled through `Serializer.LoadWorldSnapshot`,
so *no* component is referenced statically in it:

```
System.NotSupportedException: 'FFS.Libraries.StaticEcs.World`1+Components`1[Space.Client.GameWorldPrev,Space.GameCore.PatrolRail]'
is missing native code or metadata. ...
   at System.Reflection.Runtime.TypeInfos.RuntimeTypeInfo.MakeGenericType(Type[])
   at FFS.Libraries.StaticEcs.AutoRegistration.GetAutoRegisterGenericMethod(Type, Type, Type) + 0x8c
   at FFS.Libraries.StaticEcs.AutoRegistration.RegisterAll[TWorld](Assembly[]) + 0x6d4
```

**(1)** Even when the instantiation exists, `HasOnCreate()` is only correct if `T`'s method metadata was kept.
The `[DynamicallyAccessedMembers]` annotation on `EntityTypeType<T>` does not help here because `T` arrives via
reflection (`MakeGenericType` from `assembly.GetTypes()`), so the compiler's dataflow never sees a concrete type.
`GetMethods()` then returns nothing for `OnCreate`, `HasOnCreate` is `false`, and `OnCreate` is never invoked.
In our game `Dummy.OnCreate` sets the `Body` component; the spawner then calls `entity.Ref<Body>()` (no
presence check in release) and dereferences a null segment:

```
Thread 1: signal SIGSEGV
frame #0: World_1_Components_1<ServerWorld, Body>.Ref(...)   at World.Components.cs:814
frame #1: World_1_Entity<ServerWorld>.Ref<Body>()            at World.Entity.cs:470
frame #2: Core_1_SpawnDummySystem<ServerWorld>.SpawnAt(int)  at SpawnDummySystem.cs:50
```

This one is a **Heisenbug**: adding a diagnostic `typeof(Dummy).GetMethods(...)` call anywhere in the app makes
the compiler keep the metadata and the bug disappears. Reproduced on macOS NativeAOT with an otherwise identical
binary:

```
RegisterAll only                          → Is<Dummy>=True  Has<Body>=False   (bug)
RegisterAll + typeof(Dummy).GetMethods()  → Is<Dummy>=True  Has<Body>=True
explicit Types().EntityType<Dummy>()      → Is<Dummy>=True  Has<Body>=True
```

### Our fix (game side, not in the library)

We stopped using `RegisterAll` and list every ECS type through the fluent API in a method that is generic over the
world, so every call site produces static instantiations for that world and `EntityType<T>()`'s
`[DynamicallyAccessedMembers]` annotation applies to a concrete `T`:

```csharp
public static class GameTypes {
    public static void Register<TWorld>() where TWorld : struct, IWorldType {
        World<TWorld>.Types()
            .Component<Body>().Component<Transform>() /* ... */
            .Tag<IsProjectile>()
            .Links<Shapes>().Link<BodyOwner>() /* ... */
            .Event<DamageEvent>() /* ... */
            .EntityType<Dummy>().EntityType<Player>().EntityType<Projectile>();
    }
}
```

plus a `#if DEBUG` reflection scan (skipped under AOT) that throws if the assembly contains an ECS type missing
from the list.

### Suggestions for StaticEcs

1. **Document `RegisterAll` as JIT‑only** (or "NativeAOT: only for worlds whose types are all referenced
   statically"). The current docs say it is "safe on NativeAOT", which is true for the stack‑walking concern but
   not for instantiation/metadata.
2. **Make `HasOnCreate`/`HasOnDestroy` not depend on `GetMethods()`.** Options:
   * Compare against the default interface implementation: `IEntityType.OnCreate<TWorld>` has a default body; the
     library could detect "overridden" without reflection by calling through a static abstract/interface hook, or
     require an explicit marker interface (`IEntityTypeWithOnCreate`) and check `default(T) is IEntityTypeWithOnCreate`.
   * At minimum, when `HasOnCreate` is computed in a reflection‑registered path and `RuntimeFeature.IsDynamicCodeSupported`
     is `false`, log/throw instead of silently assuming "no hook".
3. **Consider a source generator** that emits the equivalent of our `GameTypes.Register<TWorld>()` from the
   consuming assembly. That would keep the ergonomics of `RegisterAll` and be fully AOT‑safe.
4. The `catch (Exception)` around entity‑type registration should include the inner exception; the AOT
   `NotSupportedException` message is the only useful diagnostic and it is currently discarded.

---

## Issue 3 — StaticEcs `TryCreateUnmanagedPackArrayStrategy<T>`

### Code (`Src/Lib.cs`, before)

```csharp
internal static IPackArrayStrategy<T> TryCreateUnmanagedPackArrayStrategy<T>() where T : struct {
    if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) return null;
    try {
        var t = typeof(UnmanagedPackArrayStrategy<>).MakeGenericType(typeof(T));   // throws under NativeAOT
        return (IPackArrayStrategy<T>)Activator.CreateInstance(t);
    }
    catch (Exception) { return null; }                                            // → StructPackArrayStrategy<T>
}
```

Used by `ComponentTypeConfig<T>.DefaultComponent`, `EventTypeConfig`, and `Multi<T>` element strategies.

### What happens under NativeAOT

It returns `null` for **every** blittable component. StaticEcs then uses `StructPackArrayStrategy<T>` and takes
the non‑unmanaged branch of the chunk writer, which requires a custom `Write`/`Read` on the component. With
`FFS_ECS_DEBUG` off there is no assert (`if (!HasWrite) throw …` is inside `#if FFS_ECS_DEBUG`), so **nothing is
written and nothing is read** — the snapshot loads and every component of that type comes back as its default
value. No exception, no log line.

We noticed it only because `Transform.Rotation` came back as `(0,0,0,0)` and Godot's `Basis(Quaternion)` produced
NaNs. Reproduced on macOS NativeAOT: a snapshot round‑trip of `Transform { Position = (1,2,3) }` came back as
`(0,0,0)`.

### Fix (included in this repo's `static-ecs` submodule, `Src/Lib.cs`)

A `struct`‑constrained twin of `UnmanagedPackArrayStrategy<T>` with the **same wire format**, so snapshots
written by a JIT build (original strategy) and an AOT build (new strategy) are interchangeable:

```csharp
internal static IPackArrayStrategy<T> TryCreateUnmanagedPackArrayStrategy<T>() where T : struct {
    if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) return null;
    return new BlittablePackArrayStrategy<T>();
}

public readonly struct BlittablePackArrayStrategy<T> : IPackArrayStrategy<T> where T : struct {
    public bool IsUnmanaged() => true;

    // Layout identical to WriteArrayUnmanaged: not-null flag, int count, uint byte size, raw bytes.
    public void WriteArray(ref BinaryPackWriter writer, T[] value, int idx, int count) {
        if (!writer.WriteNotNullFlag(value)) return;
        writer.WriteInt(count);
        var sizePoint = writer.MakePoint(sizeof(uint));
        if (count > 0) {
            var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(value, idx, count));
            writer.EnsureSize((uint)bytes.Length);
            bytes.CopyTo(writer.Buffer.AsSpan((int)writer.Position));
            writer.Position += (uint)bytes.Length;
        }
        writer.WriteUintAt(sizePoint, writer.Position - (sizePoint + sizeof(uint)));
    }

    public void ReadArray(ref BinaryPackReader reader, ref T[] result, int idx) {
        if (reader.ReadNullFlag()) { result = null; return; }
        var count = reader.ReadInt();
        var byteSize = reader.ReadUint();
        if (result == null || count + idx > result.Length) result = new T[count + idx];
        if (count > 0) {
            reader.Buffer.AsSpan((int)reader.Position, (int)byteSize).CopyTo(MemoryMarshal.AsBytes(result.AsSpan(idx, count)));
            reader.Position += byteSize;
        }
    }
    // + WriteArray(T[]), ReadArray(), ReadArray(ref T[]), 2D/3D overloads, Register() — see Src/Lib.cs
}
```

Verified under NativeAOT (macOS, same ILCompiler):

```
Transform strategy: BlittablePackArrayStrategy`1
wire format identical to UnmanagedPackArrayStrategy (233 bytes): True
cross-read (unmanaged-written bytes, blittable reader, offset 2): True
snapshot round-trip: pos=(1,2,3) rot.W=1 ok=True
```

`TryCreateUnmanagedMultiPackArrayStrategy<TWorld, TValue>` has the same reflection pattern and was **not**
changed (we don't use `Multi<T>`); it needs the same treatment.

---

## Issue 4 — static-rollback `TypeUtils.TryRegisterUnmanagedPacking<T>`

### Code (`Runtime/Static/Utils/TypeUtils.cs`, before)

```csharp
internal static bool TryRegisterUnmanagedPacking<T>() where T : struct {
    if (BinaryPack.IsRegistered<T>()) return true;
    try {
        var register = typeof(TypeUtils)
            .GetMethod(nameof(RegisterUnmanagedPacking), Static | NonPublic)!
            .MakeGenericMethod(typeof(T));                     // throws under NativeAOT
        register.Invoke(null, new object[] { });
        return true;
    }
    catch (Exception) { return false; }
}
internal static void RegisterUnmanagedPacking<T>() where T : unmanaged { BinaryPack.Register(UnmanagedWrite, UnmanagedRead<T>); }
```

Called from `Signals<T>.ctor` for signal types without `Write`/`Read` — including the library's own
`PlayerConnectedSignal`.

### What happens under NativeAOT

`MakeGenericMethod` throws, the catch returns `false`, `_isUnmanagedPack` stays `false`, and the first full sync
to a client throws:

```
System.Exception: Method Write not implemented for input type Shenanicode.Rollback.PlayerConnectedSignal
   at Shenanicode.Rollback.Session`1.Signals`1.Write(Int32 tick, Int32 index, BinaryPackWriter& writer)
   at Shenanicode.Rollback.MessageSerializer`1.WriteFullSyncInputsAndSignals(Int32, BinaryPackWriter&)
   at Shenanicode.Rollback.Server`1.PrepareFullSync(BinaryPackWriter&)
```

### Fix (included in this repo's `static-rollback` submodule)

```csharp
internal static bool TryRegisterUnmanagedPacking<T>() where T : struct {
    if (BinaryPack.IsRegistered<T>()) return true;
    if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) return false;
    BinaryPack.Register<T>(BlittableWrite, BlittableRead<T>);
    return true;
}

// Same byte layout as UnmanagedWrite/UnmanagedRead, but only requires `struct`.
internal static void BlittableWrite<T>(ref BinaryPackWriter writer, in T value) where T : struct {
    var copy = value;
    var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref copy, 1));
    writer.EnsureSize((uint)bytes.Length);
    bytes.CopyTo(writer.Buffer.AsSpan((int)writer.Position));
    writer.Position += (uint)bytes.Length;
}

internal static T BlittableRead<T>(ref BinaryPackReader reader) where T : struct {
    var value = default(T);
    var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
    reader.Buffer.AsSpan((int)reader.Position, bytes.Length).CopyTo(bytes);
    reader.Position += (uint)bytes.Length;
    return value;
}
```

`UnmanagedWrite`/`UnmanagedRead` (used by the input path, which is `unmanaged`‑constrained) are unchanged.
Builds on all target frameworks including `netstandard2.1`.

Related: `AutoRegistration.RegisterAll<TSessionType>` in static-rollback has the same `MakeGenericType` pattern as
StaticEcs's (Issue 2). It works for us only because every `IInput`/`ISignal` type is used statically; the same
"document as JIT‑only / provide a static path" suggestion applies.

---

## How to reproduce without a device

A console project referencing the game assembly, published with NativeAOT on macOS, uses the same ILCompiler as
the iOS export and reproduces every issue in ~1 minute per build:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <AssemblyName>Test</AssemblyName>   <!-- StaticEcs has InternalsVisibleTo("Test") -->
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="../GameCore/GameCore.csproj" /></ItemGroup>
</Project>
```

```csharp
struct PrevWorld : IWorldType { }          // a world nothing else references
abstract class WP : World<PrevWorld> { }

WP.Create(WorldConfig.Default());
WP.Types().RegisterAll(typeof(SomeComponent).Assembly);   // Issue 1/2
WP.Initialize();
var e = WP.NewEntity<Dummy>();
Console.WriteLine(e.Has<Body>());                          // False under AOT, True under JIT

Console.WriteLine(AutoRegistration.TryCreateUnmanagedPackArrayStrategy<Transform>() is null);  // Issue 3: True before fix
```

```sh
dotnet publish -c Release -r osx-arm64 -o out && ./out/Test
```

Important: any `typeof(X).GetMethods()` in the repro changes the outcome of Issue 1 (see Heisenbug note), so keep
diagnostics out of the binary you are measuring.

---

## Debugging NativeAOT on iOS — notes for next time

* **`SIGUSR1` stops in lldb ("the game froze").** The .NET runtime suspends managed threads for GC with
  `SIGUSR1` on Apple platforms (`ThreadStore::SuspendAllThreads → PalHijack → pthread_kill`). lldb stops on every
  one. In the lldb console, or permanently in `~/.lldbinit`:
  `process handle SIGUSR1 -n false -p true -s false`
* **`SIGSEGV` first‑chance stops.** NativeAOT implements null checks with hardware faults; lldb stops before the
  runtime turns them into `NullReferenceException`. Continue once to get the managed exception and stack.
* **`___lldb_unnamed_symbol` frames.** Godot writes the NativeAOT dSYM in a non‑standard layout
  (`<app>.framework.dSYM/<app>.dylib`) that lldb ignores. The proper dSYM for the last export is
  `Client/.godot/mono/temp/bin/ExportDebug/ios-arm64/native/<app>.dylib.dSYM`; load it with
  `target symbols add <path>`.
* **Device console without Xcode:**
  `xcrun devicectl device process launch --console --terminate-existing --device <UDID> <bundle-id>`
  (`Console.WriteLine` appears there; Godot's own prints don't.)
* **Godot's `ExportDebug` configuration defines neither `DEBUG` nor `FFS_ECS_ENABLE_DEBUG`**, so StaticEcs asserts
  are off in "debug" iOS builds. Issue 1 would have been a readable `StaticEcsException` instead of a `SIGSEGV`,
  and Issue 3 would have thrown "Method Write not implemented" instead of losing data. Worth a
  `Directory.Build.props` that defines `FFS_ECS_ENABLE_DEBUG` for `ExportDebug`.

---

## Files changed in this repository

| File | Change |
|---|---|
| `GameCore/GameTypes.cs` (new) | Explicit, AOT‑safe ECS registration generic over the world + DEBUG coverage check |
| `GameCore/GameWorldSetup.cs`, `Client/setup/GameInterpolationSetup.cs` | `RegisterAll` → `GameTypes.Register<…>()` |
| `static-ecs/Src/Lib.cs` (submodule, on top of `d06e6fe`) | `BlittablePackArrayStrategy<T>`; reflection‑free `TryCreateUnmanagedPackArrayStrategy` |
| `static-rollback/Runtime/Static/Utils/TypeUtils.cs` (submodule, on top of `de524da`) | Reflection‑free `TryRegisterUnmanagedPacking` |

The two submodule diffs are self‑contained and are offered as‑is for upstream.
