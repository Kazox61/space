namespace Space.GameCore;

/// <summary>
/// A level read from a <c>.level.bytes</c> file whose embedded payload hash has been verified.
/// Server and client must load byte-identical files; <see cref="ConnectionKey"/> enforces that at connect time.
/// </summary>
public sealed class LevelFile {
	public const string DefaultName = "level_pipeline_test";
	public const string Extension = ".level.bytes";

	private const string ConnectionKeyPrefix = "space-level:";

	public string Name { get; }
	public LevelData Data { get; }

	/// <summary>SHA-256 of the whole file (header, version and payload), as uppercase hex.</summary>
	public string ContentHash { get; }

	/// <summary>Sent by the client and required by the server; a mismatch rejects the connection.</summary>
	public string ConnectionKey => ConnectionKeyPrefix + ContentHash;

	private LevelFile(string name, LevelData data, string contentHash) {
		Name = name;
		Data = data;
		ContentHash = contentHash;
	}

	public static LevelFile Read(string name, ReadOnlySpan<byte> bytes) {
		LevelData data;
		try {
			data = LevelDataCodec.Deserialize(bytes);
		} catch (InvalidDataException exception) {
			throw new InvalidDataException($"Level '{name}' is invalid: {exception.Message}", exception);
		}
		return new LevelFile(name, data, LevelDataCodec.ContentHash(bytes));
	}

	public static LevelFile ReadFromDisk(string path) {
		if (!File.Exists(path)) {
			throw new FileNotFoundException($"Level file '{path}' does not exist.", path);
		}
		return Read(NameFromPath(path), File.ReadAllBytes(path));
	}

	public static string NameFromPath(string path) {
		var fileName = Path.GetFileName(path);
		return fileName.EndsWith(Extension, StringComparison.Ordinal) ? fileName[..^Extension.Length] : fileName;
	}

	public override string ToString() => $"{Name} ({ContentHash[..12]})";
}
