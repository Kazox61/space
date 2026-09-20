# EditorConfig pre-commit check

Install the local .NET tool and Git hook from the repository root after cloning:

```sh
dotnet tool restore
dotnet husky install
```

The pre-commit hook checks staged C# files with `dotnet format Space.sln`
using the repository's `.editorconfig`. It includes suggestion-level rules
(`--severity info`) supported by `dotnet format` and blocks the commit if
formatting changes are needed. Husky handles partially staged files.
Commits without staged C# files skip the check.

The check requires the .NET SDK and the solution's dependencies (including
its submodules and Godot SDK). `dotnet format` restores dependencies as needed.
Non-C# files are not checked by this hook.
Files under `static-*` and `FixedPoint` are explicitly excluded because they
belong to separate Git submodules.

To fix a reported file, run this from the repository root, review the changes,
and stage the file again:

```sh
dotnet format Space.sln --severity info --include GameCore/path/to/File.cs
```

Run the staged-file check manually:

```sh
dotnet husky run --group pre-commit
```

GitHub Actions runs the same formatting check on pull requests via
`.github/workflows/editorconfig.yml`. CI checks all C# files in `Client`,
`GameCore`, `Server`, and `Test`, with the same submodule exclusions.
The check is named `EditorConfig / Formatting check` and can be made required
in the repository's branch protection settings.
