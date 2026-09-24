#nullable enable

using System;
using System.Collections.Generic;
using Godot;

namespace Space.Client;

/// <summary>
/// The client's one home for playing sound (#513). An autoload, so its players outlive every scene
/// that asks it for a sound — which is also why <c>GameEngine</c> stops the music it started.
/// <para>
/// One pool of <see cref="AudioStreamPlayer"/>s per bus, warmed at startup: battle sfx pause with
/// the tree, UI sounds and music play through a pause. Music is crossfaded — the outgoing track
/// fades out over twice the crossfade while the incoming one fades in over it, and a player whose
/// fade-out finished is stopped and handed back to its pool. Ambient sound, pitch variants and
/// volume getters are not here because nothing calls them; add them when something does.
/// </para>
/// <para>
/// Every call is main-thread and plays immediately, where the addon deferred its <c>play</c>; the
/// deferral covered players it created before they were in the tree, which these never are.
/// </para>
/// </summary>
public partial class Audio : Node {
	private const string SfxBus = "SFX";
	private const string UiBus = "UI";
	private const string MusicBus = "Music";
	private const float Silent = -80f;
	private const float Full = 0f;

	private static Audio? _instance;

	private PlayerPool _sfx = null!;
	private PlayerPool _ui = null!;
	private PlayerPool _music = null!;

	public override void _EnterTree() {
		_instance = this;
		_sfx = new PlayerPool("Sfx", SfxBus, 8, ProcessModeEnum.Pausable);
		_ui = new PlayerPool("Ui", UiBus, 8, ProcessModeEnum.Always);
		_music = new PlayerPool("Music", MusicBus, 2, ProcessModeEnum.Always);
		AddChild(_sfx);
		AddChild(_ui);
		AddChild(_music);
	}

	public override void _ExitTree() {
		if (_instance == this) {
			_instance = null;
		}
	}

	/// <param name="fromPosition">Seconds into the stream to start at, for a sound that is already late.</param>
	public static void PlaySfx(AudioStream stream, float fromPosition = 0f) => Instance._sfx.Play(stream, fromPosition);

	public static void PlayUi(AudioStream stream) => Instance._ui.Play(stream);

	/// <summary>
	/// Fades every playing track out over twice <paramref name="crossfade"/> and this one in over
	/// it. Asking for the track that is already playing fades it back to full instead of starting
	/// a second copy.
	/// </summary>
	public static void PlayMusic(AudioStream stream, float crossfade) => Instance._music.Crossfade(stream, crossfade);

	public static void StopMusic(float fade) => Instance._music.FadeOutAll(fade);

	private static Audio Instance =>
		_instance ?? throw new InvalidOperationException(
			"Audio is not in the tree; it is registered as an autoload in project.godot"
		);

	/// <summary>
	/// A fixed number of players on one bus, one per simultaneous sound. A sound past the pool's
	/// size gets a new player, which is freed again once the pool is back at its size, so a burst
	/// of sfx never drops a sound and never leaves the tree larger than it started.
	/// </summary>
	private sealed partial class PlayerPool : Node {
		private readonly string _bus;
		private readonly int _size;
		private readonly List<AudioStreamPlayer> _available = [];
		private readonly List<AudioStreamPlayer> _busy = [];
		private readonly Dictionary<AudioStreamPlayer, Tween> _fades = [];

		public PlayerPool(string name, string bus, int size, ProcessModeEnum processMode) {
			Name = name;
			ProcessMode = processMode;
			_bus = bus;
			_size = size;
			for (var i = 0; i < size; i++) {
				_available.Add(NewPlayer());
			}
		}

		public void Play(AudioStream stream, float fromPosition = 0f) {
			Rent(stream).Play(fromPosition);
		}

		public void Crossfade(AudioStream stream, float duration) {
			FadeOutAll(duration * 2f);

			var playing = FindBusy(stream);
			if (playing is not null) {
				Fade(playing, playing.VolumeDb, Full, duration);
				return;
			}

			var player = Rent(stream);
			Fade(player, Silent, Full, duration);
			player.Play();
		}

		private AudioStreamPlayer Rent(AudioStream stream) {
			if (_available.Count == 0) {
				_available.Add(NewPlayer());
			}
			var player = _available[0];
			_available.RemoveAt(0);
			_busy.Add(player);
			player.Stream = stream;
			player.VolumeDb = Full;
			return player;
		}

		private AudioStreamPlayer? FindBusy(AudioStream stream) {
			foreach (var player in _busy) {
				if (IsSameTrack(player.Stream, stream)) {
					return player;
				}
			}
			return null;
		}

		// The addon matched tracks by resource path, so a second load of the same file counts as
		// the same track; a stream built at runtime has no path and is only ever itself.
		private static bool IsSameTrack(AudioStream? playing, AudioStream wanted) =>
			ReferenceEquals(playing, wanted)
			|| (playing is not null && wanted.ResourcePath != "" && playing.ResourcePath == wanted.ResourcePath);

		public void FadeOutAll(float duration) {
			// A zero fade still goes through the tween so the player is returned the same way.
			var fade = Mathf.Max(duration, 0.01f);
			foreach (var player in _busy.ToArray()) {
				Fade(player, player.VolumeDb, Silent, fade);
			}
		}

		private void Fade(AudioStreamPlayer player, float from, float to, float duration) {
			KillFade(player);

			player.VolumeDb = from;
			var tween = CreateTween();
			var step = tween.TweenProperty(player, "volume_db", to, duration);
			if (from > to) {
				step.SetTrans(Tween.TransitionType.Circ).SetEase(Tween.EaseType.In);
			} else {
				step.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
			}
			_fades[player] = tween;
			tween.Finished += () => OnFadeFinished(player, tween);
		}

		private void OnFadeFinished(AudioStreamPlayer player, Tween tween) {
			// A fade that was replaced mid-way has already been killed and forgotten; only the one
			// still on record may act on the player.
			if (!_fades.TryGetValue(player, out var current) || current != tween) {
				return;
			}
			_fades.Remove(player);
			if (player.VolumeDb <= Silent + 1f) {
				player.Stop();
				Return(player);
			}
		}

		private void KillFade(AudioStreamPlayer player) {
			if (_fades.Remove(player, out var fade)) {
				fade.Kill();
			}
		}

		private void Return(AudioStreamPlayer player) {
			_busy.Remove(player);
			KillFade(player);
			if (_available.Count >= _size) {
				player.QueueFree();
			} else if (!_available.Contains(player)) {
				_available.Add(player);
			}
		}

		private AudioStreamPlayer NewPlayer() {
			var player = new AudioStreamPlayer { Bus = _bus };
			AddChild(player);
			player.Finished += () => Return(player);
			return player;
		}
	}
}
