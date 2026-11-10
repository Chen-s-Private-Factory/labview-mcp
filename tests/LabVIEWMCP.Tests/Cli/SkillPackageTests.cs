using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Cli;

public sealed class SkillPackageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lvmcp-skill-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Source(string relative)
    {
        var path = Res.FindRepoFile(relative);
        Assert.NotNull(path);
        return path!;
    }

    private static Task<(int ExitCode, string Output, string Error)> PowerShell(params string[] args)
    {
        var start = new ProcessStartInfo("powershell")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, UseShellExecute = false,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" }.Concat(args))
            start.ArgumentList.Add(argument);
        return ToolRouterTests.RunProcessAsync(start);
    }

    private static string Exe
    {
        get
        {
            var configuration = typeof(SkillPackageTests).Assembly
                .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
                ?? throw new InvalidOperationException("The test assembly has no build configuration.");
            return Source(Path.Combine("src", "LabVIEWMCP", "bin", configuration, "net8.0", "LabVIEWMCP.exe"));
        }
    }

    [Fact]
    public async Task Launcher_without_a_mode_refuses_instead_of_starting_MCP()
    {
        var result = await PowerShell(Source(@".github\skills\labview\scripts\labview.ps1"), "-Executable", Exe);
        Assert.Equal(2, result.ExitCode);
        using var error = JsonDocument.Parse(result.Output);
        Assert.Equal("launcher", error.RootElement.GetProperty("errorKind").GetString());
    }

    [Fact]
    public async Task Launcher_runs_a_read_only_tool_from_an_args_file_with_spaces()
    {
        var file = Path.Combine(_root, "pattern args.json");
        File.WriteAllText(file, """{"pattern":4833}""");
        var result = await PowerShell(Source(@".github\skills\labview\scripts\labview.ps1"),
            "-Executable", Exe, "-Invoke", "lvai_connector_pane", "-ArgsFile", file);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        using var answer = JsonDocument.Parse(result.Output);
        Assert.Contains("4833", answer.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Export_is_portable_contains_all_schemas_and_never_installs_MCP_assets()
    {
        var package = Path.Combine(_root, "portable skill");
        var result = await PowerShell(Source(@"scripts\Export-LabviewSkill.ps1"),
            "-Executable", Exe, "-Destination", package);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.True(File.Exists(Path.Combine(package, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(package, "references", "workflows.md")));
        using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "references", "tools.json")));
        var tools = catalogue.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.True(tools.Length >= 118);
        var index = File.ReadAllText(Path.Combine(package, "references", "tool-index.md"));
        Assert.All(tools, tool => Assert.Contains(tool.GetProperty("name").GetString()!, index));
        Assert.Empty(Directory.GetFiles(package, ".mcp.json", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(package, "agents")));
        Assert.True(File.Exists(Path.Combine(package, "bin", "pylabview", "python.exe")));
        Assert.True(File.Exists(Path.Combine(package, "bin", "scripts", "pylv-conpane.py")));
        Assert.True(File.Exists(Path.Combine(package, "bin", "docs", "connector-pane-patterns.tsv")));

        var described = await PowerShell(Path.Combine(package, "scripts", "labview.ps1"),
            "-DescribeTool", "lvai_generate_vi");
        Assert.True(described.ExitCode == 0, described.Error + described.Output);
        using var schema = JsonDocument.Parse(described.Output);
        Assert.Equal("lvai_generate_vi", schema.RootElement.GetProperty("name").GetString());

        var repeated = await PowerShell(Source(@"scripts\Export-LabviewSkill.ps1"),
            "-Executable", Exe, "-Destination", package);
        Assert.NotEqual(0, repeated.ExitCode);
    }
}
