using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FFS.Libraries.StaticEcs;
using Fixed64;
using Godot;
using Shenanicode.Rollback;
using Space.GameCore;
using ClientCore = Space.GameCore.Core<Space.Client.ClientWorld>;
using ServerCore = Space.GameCore.Core<Space.GameCore.ServerWorld>;

namespace Space.Client;

/// <summary>
/// Opt-in wall-clock capture outside rollback state. --physics-lab-profile PATH records a manual
/// run; --physics-lab-replay additionally drives three fresh-world traversals in the same process.
/// Timings never control ticks or simulation state. The scripted pose change is replayed by tick.
/// </summary>
public static class ClientPerformanceCapture {
	public enum Section { ServerUpdate, ClientUpdate, Interpolation, Views, PhysicsDebug, NavDebug, GameProcess }
	public const int RouteStartTick = 60;
	private const int RouteTicks = 480;
	private static List<Frame> s_frames;
	private static List<object> s_runs;
	private static List<object> s_completedRuns;
	private static List<string> s_errors;
	private static string s_path;
	private static Frame s_frame;
	private static long s_frameStart;
	private static int s_run = -1;
	private static Rid s_viewport;
	private static bool s_written;
	private static bool s_transitionPending;
	private static int s_serverRouteStarts;
	private static int s_clientRouteStarts;
	private static double s_firstEnterMs;
	private static bool s_machClock;
	[DllImport("/usr/lib/libSystem.B.dylib")]
	private static extern ulong mach_absolute_time();
	public static bool Enabled => s_path != null && !s_written;
	public static bool Replay { get; private set; }
	public static bool DebugDrawing { get; private set; }

	public static void Configure() {
		if (s_path != null)
			return;
		var args = OS.GetCmdlineUserArgs();
		for (var i = 0; i < args.Length; i++) {
			if (args[i] == "--physics-lab-profile" && i + 1 < args.Length)
				s_path = Path.GetFullPath(args[++i]);
			else if (args[i] == "--physics-lab-replay")
				Replay = true;
			else if (args[i] == "--physics-lab-debug")
				DebugDrawing = true;
		}
		if (s_path == null) {
			Replay = DebugDrawing = false;
			return;
		}
		s_firstEnterMs = Time.GetTicksUsec() / 1000.0;
		s_machClock = OS.GetName() == "macOS";
		s_frames = new List<Frame>(12000);
		s_runs = new List<object>(3);
		s_completedRuns = new List<object>(3);
		s_errors = new List<string>();
		RenderingServer.FramePostDraw += OnFramePostDraw;
	}

	public static void StartRun(ClientGame game, LevelFile level, double setupMs) {
		if (!Enabled)
			return;
		if (Replay && (!OfflineServer.IsRunning || level.Name != "level_pipeline_test"))
			throw new InvalidOperationException("The scripted lab capture requires an offline level_pipeline_test world.");
		s_run++;
		s_frameStart = 0;
		s_frame = null;
		s_transitionPending = false;
		s_serverRouteStarts = s_clientRouteStarts = 0;
		s_viewport = game.GetViewport().GetViewportRid();
		RenderingServer.ViewportSetMeasureRenderTime(s_viewport, true);
		// Wrap the existing production root, retaining correction/FX observers and all systems.
		ClientCore.S.SetUpdateRoot(new TimedRoot<ClientWorld>(false));
		ClientCore.RollbackObserver = new CaptureObserver(ClientCore.RollbackObserver);
		if (OfflineServer.IsRunning)
			ServerCore.S.SetUpdateRoot(new TimedRoot<ServerWorld>(true));
		s_runs.Add(new {
			run = s_run,
			setupMs,
			engineUptimeMs = Time.GetTicksUsec() / 1000.0,
			level = level.Name,
			hash = level.ContentHash
		});
	}

	public static void BeginFrame() {
		if (!Enabled)
			return;
		var now = Stopwatch.GetTimestamp();
		var machNow = s_machClock ? mach_absolute_time() : 0;
		if (s_frame != null) {
			// The previous sample owns the interval until this frame starts, including VSync/waiting.
			s_frame.WallMs = Stopwatch.GetElapsedTime(s_frameStart, now).TotalMilliseconds;
			s_frame.MachEnd = machNow;
			s_frames.Add(s_frame);
		}
		s_frameStart = now;
		s_frame = new Frame {
			Run = s_run,
			FrameNumber = Engine.GetProcessFrames(),
			MachStart = machNow,
			ServerHeadBefore = OfflineServer.IsRunning ? ServerCore.S.CurrentTick : -1,
			ClientHeadBefore = ClientCore.S.CurrentTick
		};
	}

	public static Scope Measure(Section section) => new(section, Enabled ? Stopwatch.GetTimestamp() : 0);

	public static void RecordPhysicsOverlay(bool enabled, int flags) {
		if (!Enabled || s_frame == null)
			return;
		s_frame.PhysicsOverlay = enabled;
		s_frame.PhysicsOverlayFlags = flags;
	}

	public static void RecordNavOverlay(bool enabled, int flags, int selectedAgent) {
		if (!Enabled || s_frame == null)
			return;
		s_frame.NavOverlay = enabled;
		s_frame.NavOverlayFlags = flags;
		s_frame.NavSelectedAgent = selectedAgent;
	}

	public readonly struct Scope(Section section, long start) : IDisposable {
		public void Dispose() {
			if (start != 0 && s_frame != null)
				s_frame.Sections[(int)section] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
		}
	}

	public static void EndGameProcess(ClientGame game) {
		if (!Enabled || s_frame == null)
			return;
		s_frame.ServerHeadAfter = OfflineServer.IsRunning ? ServerCore.S.CurrentTick : -1;
		s_frame.ClientHeadAfter = ClientCore.S.CurrentTick;
		foreach (var player in ClientCore.W.Query<All<PlayerInfo, Space.GameCore.Transform>>().Entities()) {
			if (player.Read<PlayerInfo>().InputChannel != CLNT.Channel)
				continue;
			var p = player.Read<Space.GameCore.Transform>().Position;
			s_frame.PlayerX = p.X.ToDouble();
			s_frame.PlayerY = p.Y.ToDouble();
			s_frame.PlayerZ = p.Z.ToDouble();
		}
		if (!Replay || s_transitionPending || Math.Min(s_frame.ServerHeadAfter, s_frame.ClientHeadAfter) < RouteStartTick + RouteTicks)
			return;
		s_transitionPending = true;
		s_completedRuns.Add(new { run = s_run, serverHead = s_frame.ServerHeadAfter, clientHead = s_frame.ClientHeadAfter });
		if (s_serverRouteStarts == 0 || s_clientRouteStarts == 0)
			s_errors.Add($"run {s_run}: route start not simulated by both worlds (server={s_serverRouteStarts}, client={s_clientRouteStarts})");
		// Let the current callbacks finish before teardown; exclude the incomplete final sample.
		game.CallDeferred(nameof(ClientGame.FinishPerformanceRun));
	}

	public static bool FinishRun() {
		if (!Enabled)
			return true;
		if (s_run < 2)
			return false;
		WriteCapture();
		return true;
	}

	public static void OnExit() {
		if (Enabled && !s_transitionPending)
			WriteCapture();
	}

	private static void OnFramePostDraw() {
		if (!Enabled || s_frame == null)
			return;
		s_frame.RenderCpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(s_viewport);
		s_frame.RenderGpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(s_viewport);
		s_frame.RenderSetupMs = RenderingServer.GetFrameSetupTimeCpu();
	}

	private sealed class TimedRoot<TWorld>(bool server) : IUpdateRoot where TWorld : struct, IWorldType, ISessionType {
		private readonly Core<TWorld>.GameUpdateRoot _inner = new();
		public void Update(int tick) {
			if (Replay && tick == RouteStartTick) {
				foreach (var player in Core<TWorld>.W.Query<All<PlayerInfo, Space.GameCore.Transform, Mover>>().Entities()) {
					ref var pose = ref player.Ref<Space.GameCore.Transform>();
					pose.Position = new Fixed64.FVector3(Fixed64.FP.FromRatio(9, 1), Fixed64.FP.FromRatio(3, 2), Fixed64.FP.FromRatio(-33, 1));
					pose.TeleportTick = tick;
					ref var mover = ref player.Ref<Mover>();
					mover.Velocity = mover.SupportVelocity = mover.InheritedVelocity = Fixed32.FVector3.Zero;
					mover.PogoVelocity = mover.JumpCooldown = Fixed32.FP.Zero;
					mover.Grounded = false;
					if (server)
						s_serverRouteStarts++;
					else
						s_clientRouteStarts++;
				}
			}
			var start = Stopwatch.GetTimestamp();
			_inner.Update(tick);
			if (s_frame == null)
				return;
			var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
			var stats = Core<TWorld>.PhysicsDiagnostics.LastStep;
			double physics = 0;
			for (var p = 0; p < (int)PhysicsPhase.Count; p++)
				physics += stats.Milliseconds((PhysicsPhase)p);
			if (server) {
				s_frame.ServerTicks++;
				s_frame.ServerSimulationMs += ms;
				s_frame.ServerPhysicsMs += physics;
				s_frame.ServerNarrowphaseMs += stats.Milliseconds(PhysicsPhase.Narrowphase);
				s_frame.ServerSolveMs += stats.Milliseconds(PhysicsPhase.Solve);
			} else {
				// Count callbacks, not head advancement: snapshot-alignment replay is real work too.
				if (!s_frame.ClientFullSync && tick < s_frame.ClientHeadBefore) { s_frame.ClientReplayTicks++; s_frame.ClientReplayMs += ms; } else { s_frame.ClientForwardTicks++; s_frame.ClientForwardMs += ms; }
				s_frame.ClientPhysicsMs += physics;
				s_frame.ClientNarrowphaseMs += stats.Milliseconds(PhysicsPhase.Narrowphase);
				s_frame.ClientSolveMs += stats.Milliseconds(PhysicsPhase.Solve);
			}
		}
	}

	private sealed class CaptureObserver(IRollbackObserver inner) : IRollbackObserver {
		public void OnTickSimulated(int tick) => inner?.OnTickSimulated(tick);
		public void OnFullSync() {
			// A replacement timeline cannot be classified against the old head tick.
			if (s_frame != null)
				s_frame.ClientFullSync = true;
			inner?.OnFullSync();
		}
	}

	private static void WriteCapture() {
		if (s_written)
			return;
		s_written = true;
		RenderingServer.FramePostDraw -= OnFramePostDraw;
		// The unfinished final frame has no complete inter-frame interval and is excluded.
		var metadata = new {
			configuration = typeof(ClientGame).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
			assemblies = new[] { typeof(ClientGame).Assembly, typeof(CoreRoot).Assembly, typeof(Fixed64.FP).Assembly,
	 typeof(World<>).Assembly, typeof(IUpdateRoot).Assembly,
	 typeof(Shenanicode.Rollback.LiteNetLib.LiteNetLibServerConnection).Assembly }.Select(a => new {
		 name = a.GetName().Name,
		 configuration = a.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
		 jitOptimizationDisabled = a.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled ?? false
	 }),
			engine = Engine.GetVersionInfo()["string"].AsString(),
			runtime = RuntimeInformation.FrameworkDescription,
			os = OS.GetName(),
			gpu = RenderingServer.GetVideoAdapterName(),
			renderer = RenderingServer.GetCurrentRenderingMethod(),
			window = DisplayServer.WindowGetSize().ToString(),
			vsync = DisplayServer.WindowGetVsyncMode().ToString(),
			maxFps = Engine.MaxFps,
			replay = Replay,
			debugDrawing = DebugDrawing,
			firstEnterMs = s_firstEnterMs,
			processId = System.Environment.ProcessId,
			nativeClock = s_machClock ? "mach_absolute_time" : null,
			routeStartTick = RouteStartTick,
			routeMovementTicks = 160,
			routeTicks = RouteTicks,
			runs = s_runs,
			completedRuns = s_completedRuns,
			errors = s_errors,
			notes = "wall_ms is start-to-next-start, includes waiting; rendering CPU/GPU counters are latest available engine samples, may lag. Zero GPU samples do not establish zero GPU cost. Client update includes interpolation and snapshot work; game_process includes server/client update. Replay uses normal networked input, so movement transitions can lag the nominal tick. Diagnostic frame storage allocates only while capture is enabled; the last incomplete frame of each run is excluded."
		};
		File.WriteAllText(s_path + ".json", JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
		using var writer = new StreamWriter(s_path);
		writer.WriteLine("run,frame,server_head_before,server_head_after,client_head_before,client_head_after,wall_ms,server_update_ms,client_update_ms,interpolation_ms,views_ms,physics_debug_ms,nav_debug_ms,game_process_ms,server_ticks,client_forward_ticks,client_replay_ticks,server_simulation_ms,client_forward_ms,client_replay_ms,server_physics_ms,client_physics_ms,server_narrowphase_ms,client_narrowphase_ms,server_solve_ms,client_solve_ms,render_cpu_ms,render_gpu_ms,render_setup_ms,player_x,player_y,player_z,client_full_sync,mach_start,mach_end,physics_overlay,physics_overlay_flags,nav_overlay,nav_overlay_flags,nav_selected_agent");
		foreach (var f in s_frames) {
			writer.WriteLine(string.Join(",", new object[] { f.Run, f.FrameNumber, f.ServerHeadBefore, f.ServerHeadAfter,
	f.ClientHeadBefore, f.ClientHeadAfter, f.WallMs }.Concat(f.Sections.Cast<object>()).Concat(new object[] {
	f.ServerTicks, f.ClientForwardTicks, f.ClientReplayTicks, f.ServerSimulationMs, f.ClientForwardMs, f.ClientReplayMs,
	f.ServerPhysicsMs, f.ClientPhysicsMs, f.ServerNarrowphaseMs, f.ClientNarrowphaseMs, f.ServerSolveMs, f.ClientSolveMs,
	  f.RenderCpuMs, f.RenderGpuMs, f.RenderSetupMs, f.PlayerX, f.PlayerY, f.PlayerZ, f.ClientFullSync ? 1 : 0,
	  f.MachStart, f.MachEnd, f.PhysicsOverlay ? 1 : 0, f.PhysicsOverlayFlags,
	  f.NavOverlay ? 1 : 0, f.NavOverlayFlags, f.NavSelectedAgent
   }).Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))));
		}
		GD.Print($"[PhysicsLabCapture] wrote {s_frames.Count} frames to {s_path}; errors={s_errors.Count}");
	}

	private sealed class Frame {
		public int Run, ServerHeadBefore, ServerHeadAfter, ClientHeadBefore, ClientHeadAfter;
		public ulong FrameNumber;
		public ulong MachStart, MachEnd;
		public bool PhysicsOverlay, NavOverlay;
		public int PhysicsOverlayFlags, NavOverlayFlags, NavSelectedAgent = -1;
		public readonly double[] Sections = new double[7];
		public int ServerTicks, ClientForwardTicks, ClientReplayTicks;
		public bool ClientFullSync;
		public double WallMs, ServerSimulationMs, ClientForwardMs, ClientReplayMs, ServerPhysicsMs, ClientPhysicsMs;
		public double ServerNarrowphaseMs, ClientNarrowphaseMs, ServerSolveMs, ClientSolveMs;
		public double RenderCpuMs, RenderGpuMs, RenderSetupMs, PlayerX = double.NaN, PlayerY = double.NaN, PlayerZ = double.NaN;
	}
}
