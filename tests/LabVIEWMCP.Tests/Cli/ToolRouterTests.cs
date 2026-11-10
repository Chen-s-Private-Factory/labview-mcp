using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Xunit;

namespace LabVIEWMcp.Tests.Cli;

public sealed class ToolRouterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lvmcp-router-" + Guid.NewGuid().ToString("N"));

    public ToolRouterTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Arguments(string content)
    {
        var path = Path.Combine(_root, "arguments with spaces.json");
        File.WriteAllText(path, content, new UTF8Encoding(true));
        return path;
    }

    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LabVIEWMCP.Tests.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LabVIEWMCP.Tests.deps.json"));
        start.ArgumentList.Add(typeof(StatusTools).Assembly.Location);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return await RunProcessAsync(start);
    }

    internal static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            // Stdin stays OPEN: accidentally starting an MCP server must not look like CLI success.
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("CLI did not exit while stdin remained open.");
        }
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task Catalogue_matches_every_registered_MCP_tool_and_schema()
    {
        var result = await RunAsync("--list-tools");
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var actual = document.RootElement.GetProperty("tools").EnumerateArray().ToArray();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(provider => new LvaiConnection(provider.GetRequiredService<ILogger<LvaiConnection>>(), 1));
        services.AddMcpServer().WithToolsFromAssembly(typeof(StatusTools).Assembly).WithArgumentDiagnostics();
        await using var provider = services.BuildServiceProvider();
        var expected = provider.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
        Assert.True(expected.Length >= 118);
        Assert.Equal(expected.Select(tool => tool.Name), actual.Select(tool => tool.GetProperty("name").GetString()));
        foreach (var (tool, json) in expected.Zip(actual))
        {
            Assert.Equal(tool.Description, json.GetProperty("description").GetString());
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(tool.InputSchema.GetRawText()),
                JsonNode.Parse(json.GetProperty("inputSchema").GetRawText())), tool.Name);
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(tool.Annotations),
                JsonNode.Parse(json.GetProperty("annotations").GetRawText())), tool.Name);
        }
    }

    [Fact]
    public async Task Describe_exposes_schema_without_opening_LabVIEW()
    {
        var result = await RunAsync("--describe-tool", "lvai_describe_vi");
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("lvai_describe_vi", document.RootElement.GetProperty("name").GetString());
        var schema = document.RootElement.GetProperty("inputSchema");
        Assert.Equal("viPath", schema.GetProperty("required")[0].GetString());
        Assert.False(schema.GetProperty("properties").TryGetProperty("ct", out _));
    }

    [Fact]
    public async Task Invoke_calls_a_real_tool_with_defaults_and_folded_argument_names()
    {
        var result = await RunAsync("--invoke", "lvai_connector_pane", "--args-file",
            Arguments("""{"pattern":4833}"""));
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Contains("4833", document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Invoke_preserves_tool_errors_and_sets_failure_exit_code()
    {
        var missing = Path.Combine(_root, "does not exist.vi");
        var result = await RunAsync("--invoke", "lvai_describe_vi", "--args-file",
            Arguments(JsonSerializer.Serialize(new { vi_path = missing, backend = "pylabview", getNodesInfo = false })));
        Assert.Equal(1, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("viNotFound", document.RootElement.GetProperty("errorKind").GetString());
        Assert.Contains(missing, document.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("--invoke", "lvai_describe_vi", "{}", "badArguments")]
    [InlineData("--invoke", "lvai_describe_vi", """{"viPath":null}""", "badArguments")]
    [InlineData("--invoke", "lvai_describe_vi", """{"viPath":"x","getNodesInfo":"no"}""", "badArguments")]
    [InlineData("--invoke", "lvai_describe_vi", """{"viPath":"x","unexpected":true}""", "badArguments")]
    [InlineData("--invoke", "lvai_connector_pane", """{"unexpected":true}""", "badArguments")]
    [InlineData("--invoke", "lvai_describe_vi", """{"viPath":"x","viPath":"y"}""", "badArguments")]
    [InlineData("--invoke", "not_a_tool", "{}", "unknownTool")]
    [InlineData("--invoke", "lvai_describe_vi", "[]", "badArguments")]
    [InlineData("--invoke", "lvai_describe_vi", "{", "badArguments")]
    public async Task Invalid_requests_are_JSON_errors_before_execution(string mode, string tool, string json, string kind)
    {
        var result = await RunAsync(mode, tool, "--args-file", Arguments(json));
        Assert.Equal(2, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(kind, document.RootElement.GetProperty("errorKind").GetString());
    }

    [Theory]
    [InlineData("--invoke")]
    [InlineData("--describe-tool")]
    [InlineData("--args-file", "missing.json")]
    [InlineData("--invoke", "lvai_describe_vi", "--args-file", "missing.json")]
    [InlineData("--list-tools", "--invoke", "lvai_describe_vi")]
    [InlineData("--list-tools", "--timeout", "oops")]
    [InlineData("--list-tools", "--port", "oops")]
    [InlineData("--list-tools", "--validate", "x.xml")]
    [InlineData("--list-tools", "--unknown")]
    [InlineData("--list-tools", "unexpected")]
    public async Task Invalid_router_flags_never_fall_through_to_MCP(params string[] args)
    {
        var result = await RunAsync(args);
        Assert.Equal(2, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Catalogue_can_be_written_as_UTF8_JSON()
    {
        var path = Path.Combine(_root, "tools.json");
        var result = await RunAsync("--list-tools", "--out", path);
        Assert.Equal(0, result.ExitCode);
        using var file = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(file.RootElement.GetProperty("tools").GetArrayLength() >= 118);
    }

    [Fact]
    public async Task Static_reference_tool_accepts_a_numeric_section_as_text()
    {
        var result = await RunAsync("--invoke", "lvai_aixml_reference", "--args-file",
            Arguments("""{"section":2}"""));
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(KnowledgeTools.AixmlReference(section: "2"), document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Direct_call_reaches_real_gRPC_with_DI_defaults_and_explicit_parameters()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.StreamCount = 1;
        var result = await RunAsync("--invoke", "lvai_describe_vi", "--port", server.Port.ToString(),
            "--args-file", Arguments("""{"viPath":"C:\\input.vi","backend":"grpc","getNodesInfo":false}"""));
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(1, document.RootElement.GetProperty("messageCount").GetInt32());
        var request = server.Service.Last<GetDescribeVIPromptInfoRequest>("GetDescribeVIPromptInfo");
        Assert.Equal(@"C:\input.vi", request.ViPath);
        Assert.False(request.GetNodesInfo);
        Assert.Equal("", request.ViName);
        Assert.DoesNotContain("Connected to", result.Output);
    }

    [Fact]
    public async Task Whole_call_timeout_cancels_the_stream_without_starting_MCP()
    {
        await using var server = await LvaiTestServer.StartAsync();
        server.Service.StreamForever = true;
        var result = await RunAsync("--invoke", "lvai_describe_vi", "--port", server.Port.ToString(),
            "--timeout", "1", "--args-file", Arguments("""{"viPath":"C:\\input.vi","backend":"grpc"}"""));
        Assert.Equal(130, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("cancelled", document.RootElement.GetProperty("errorKind").GetString());
    }
}
