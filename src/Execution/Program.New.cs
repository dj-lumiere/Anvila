namespace Builder.Execution;

internal partial class Program
{
    /// <summary>
    /// <c>new [folder]</c>: starts a project in <paramref name="args"/>' folder (the current one when none is given, made
    /// when missing) for the language of the command that runs it. Writes <c>config.toml</c> (the package named after the
    /// folder), a first source that prints a greeting, a <c>.gitignore</c> for build output, and the VS Code files that
    /// recommend the language's extension and bind run and build. A file that is already there is kept as it is.
    /// </summary>
    private static int RunNewCommand(string[] args)
    {
        string folder = Path.GetFullPath(path: args.Length > 1 ? args[1] : Directory.GetCurrentDirectory());
        Directory.CreateDirectory(path: folder);
        string name = Path.GetFileName(path: folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string tool = CliRules.ToolName;
        string main = "main" + CliRules.FileExtension;

        var files = new List<(string Path, string Text)>
        {
            ("config.toml", $"""
                [package]
                name = "{name}"

                [target]
                executable = "{main}"
                mode = "debug"

                """),
            // Suflae brings IO/Console in every file and ends a routine without `return`.
            (main, CliRules.FileExtension == ".sf"
                ? """
                  routine start()
                      show("Hello, world!")

                  """
                : """
                  import IO/Console

                  routine start()
                      show("Hello, world!")
                      return

                  """),
            (".gitignore", """
                build/
                *.exe
                *.pdb
                *.ll
                /main

                """),
            (Path.Combine(".vscode", "extensions.json"), $$"""
                {
                  "recommendations": ["lumifoundry.{{tool}}"]
                }

                """),
            (Path.Combine(".vscode", "tasks.json"), $$"""
                {
                  "version": "2.0.0",
                  "tasks": [
                    {
                      "label": "run",
                      "type": "shell",
                      "command": "{{tool}} run",
                      "group": { "kind": "build", "isDefault": true },
                      "problemMatcher": []
                    },
                    {
                      "label": "build",
                      "type": "shell",
                      "command": "{{tool}} build",
                      "problemMatcher": []
                    }
                  ]
                }

                """)
        };

        WriteNewProjectFiles(folder: folder, files: files);
        Console.WriteLine(value: args.Length > 1
            ? $"Run it with: cd {args[1]} && {tool} run"
            : $"Run it with: {tool} run");
        return 0;
    }

    /// <summary>
    /// Writes each file under <paramref name="folder"/>, making its directory, and says which were made and which were
    /// already there (and kept).
    /// </summary>
    private static void WriteNewProjectFiles(string folder, List<(string Path, string Text)> files)
    {
        foreach ((string relative, string text) in files)
        {
            string path = Path.Combine(path1: folder, path2: relative);
            if (File.Exists(path: path))
            {
                Console.WriteLine(value: $"  kept     {relative} (already there)");
                continue;
            }

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
            File.WriteAllText(path: path, contents: text);
            Console.WriteLine(value: $"  created  {relative}");
        }
    }
}
