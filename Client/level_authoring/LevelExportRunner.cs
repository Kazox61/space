using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Space.GameCore;

namespace Space.Client.LevelAuthoring;

public partial class LevelExportRunner : Node {
	public override void _Ready() {
		try {
			var arguments = ParseArguments(OS.GetCmdlineUserArgs());
			var input = Require(arguments, "input");
			var output = arguments.TryGetValue("output", out var explicitOutput) && !string.IsNullOrWhiteSpace(explicitOutput)
				? explicitOutput
				: LevelExporter.OutputPathFor(input);
			var scene = GD.Load<PackedScene>(input) ?? throw new InvalidDataException($"Could not load level scene '{input}'.");
			var root = scene.Instantiate();
			AddChild(root);

			GD.Print(LevelExporter.ExportToFile(root, output));
			GetTree().Quit();
		} catch (Exception exception) {
			GD.PushError(exception.ToString());
			GetTree().Quit(1);
		}
	}

	private static Dictionary<string, string> ParseArguments(string[] arguments) {
		var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var argument in arguments) {
			var separator = argument.IndexOf('=');
			if (argument.StartsWith("--", StringComparison.Ordinal) && separator > 2) {
				parsed[argument[2..separator]] = argument[(separator + 1)..];
			}
		}
		return parsed;
	}

	private static string Require(IReadOnlyDictionary<string, string> arguments, string name) =>
		arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
			? value
			: throw new ArgumentException($"Missing required --{name}=... argument.");
}
