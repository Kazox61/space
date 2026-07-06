using Godot;
using ILogger = Shenanicode.Rollback.ILogger;

namespace Game.Client {
	public class GodotLogger : ILogger {
		private readonly string _prefix;

		public GodotLogger(string prefix = "") {
			_prefix = string.IsNullOrEmpty(prefix) ? "" : $"[{prefix}] ";
		}

		public void Log(string message) => GD.Print($"{_prefix}{message}");

		public void Warn(string message) => GD.PushWarning($"{_prefix}{message}");

		public void Error(string message) => GD.PushError($"{_prefix}{message}");
	}
}
