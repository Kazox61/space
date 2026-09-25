using System.CommandLine;
using System.Diagnostics;
using Shenanicode.Rollback;
using Shenanicode.Rollback.LiteNetLib;
using Space.GameCore;

internal class Program {
	private static async Task<int> Main(string[] args) {
		Option<ushort> portOption = new("--port") {
			Description = "The port to listen on",
			Arity = ArgumentArity.ExactlyOne,
			Required = true,
		};

		Option<FileInfo> fileOption = new("--file") {
			Description = "The save file to populate the world",
			Arity = ArgumentArity.ZeroOrOne,
		};

		Option<FileInfo> levelOption = new("--level") {
			Description = $"The .level.bytes file to load (default: levels/{LevelFile.DefaultName}{LevelFile.Extension} next to the server)",
			Arity = ArgumentArity.ExactlyOne,
		};

		RootCommand rootCommand = new("Game server.");
		rootCommand.Options.Add(fileOption);
		rootCommand.Options.Add(levelOption);
		rootCommand.Options.Add(portOption);
		rootCommand.SetAction(RunProgram);

		return await rootCommand.Parse(args).InvokeAsync();

		async Task<int> RunProgram(ParseResult parseResult, CancellationToken arg2) {
			var running = true;
			Console.CancelKeyPress += (_, e) => {
				e.Cancel = true;
				running = false;
			};
			AppDomain.CurrentDomain.ProcessExit += (_, _) => running = false;

			var levelPath = parseResult.GetValue(levelOption)?.FullName
				?? Path.Combine(AppContext.BaseDirectory, "levels", LevelFile.DefaultName + LevelFile.Extension);
			LevelFile level;
			LiteNetLibRemoteClientListener clientListener;
			try {
				level = LevelFile.ReadFromDisk(levelPath);
				Console.WriteLine($"Level: {level.Name} entities={level.Data.Entities.Count} sha256={level.ContentHash}");

				clientListener = new LiteNetLibRemoteClientListener(parseResult.GetValue(portOption), level.ConnectionKey);
				ServerSetup.CreateAndInitialize(clientListener, level, new ConsoleLogger("Server"));
			} catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException) {
				Console.Error.WriteLine($"Failed to start server with level file '{levelPath}': {exception.Message}");
				return 1;
			}

			if (parseResult.GetValue(fileOption) is { } parsedFile) {
				Console.WriteLine($"WorldFile: {parsedFile.Name}");
			}

			clientListener.Start();
			Console.WriteLine($"Started listening on port: {parseResult.GetValue(portOption)}");

			var stopwatch = Stopwatch.StartNew();
			while (running) {
				SRVR.Update(stopwatch.Elapsed.TotalSeconds);
				await Task.Yield();
			}

			clientListener.Stop();
			Console.WriteLine();

			ServerSetup.Destroy();
			Console.WriteLine($"Exited after {stopwatch.Elapsed.TotalSeconds:F0} seconds.");

			return 0;
		}
	}
}
