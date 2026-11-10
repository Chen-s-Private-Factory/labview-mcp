using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Cli;

internal sealed class ToolRouter(IServiceProvider services, Assembly assembly)
{
    private sealed record Entry(MethodInfo Method, Tool Descriptor);
    private sealed class RequestError(string kind, string message) : Exception(message)
    {
        public string Kind { get; } = kind;
    }

    private static readonly string[] Modes = ["--list-tools", "--describe-tool", "--invoke"];
    private static readonly HashSet<string> ValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--describe-tool", "--invoke", "--args-file", "--out", "--timeout", "--port",
        "--lvversion", "--aixml-backend",
    };

    private readonly Dictionary<string, Entry> _tools = Discover(services, assembly);

    public static bool IsRequested(string[] args) =>
        args.Any(arg => Modes.Contains(arg, StringComparer.OrdinalIgnoreCase)
                        || arg.Equals("--args-file", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, Entry> Discover(IServiceProvider services, Assembly assembly)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var type in assembly.GetTypes().Where(type => type.IsDefined(typeof(McpServerToolTypeAttribute))))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                     .Where(method => method.IsDefined(typeof(McpServerToolAttribute))))
        {
            // Only metadata comes from the SDK. No MCP registration, server or request is created.
            var options = new McpServerToolCreateOptions { Services = services };
            var metadata = method.IsStatic
                ? McpServerTool.Create(method, target: null, options)
                : McpServerTool.Create(method, _ => ActivatorUtilities.CreateInstance(services, type), options);
            var descriptor = new DiagnosingTool(metadata).ProtocolTool;
            if (!entries.TryAdd(descriptor.Name, new Entry(method, descriptor)))
                throw new InvalidOperationException($"Duplicate tool name '{descriptor.Name}'.");
        }
        return entries;
    }

    internal IReadOnlyList<Tool> Catalogue() =>
        _tools.Values.Select(entry => entry.Descriptor).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();

    internal async Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var entry))
            throw new RequestError("unknownTool", $"No tool named '{name}'. Use --list-tools.");
        var values = Bind(entry, arguments, ct);
        var previous = AixmlBackend.ClientTool.Value;
        AixmlBackend.ClientTool.Value = name;
        try
        {
            ct.ThrowIfCancellationRequested();
            var log = services.GetRequiredService<ILogger<ToolRouter>>();
            if (CacheDirectory.MigrateLegacy() is > 0 and var moved)
                log.LogInformation("Migrated {Count} legacy cache files.", moved);
            var version = LvaiVersion.Check();
            if (version.Changed) log.LogWarning("{Message}", version.Describe());
            else log.LogInformation("{Message}", version.Describe());
            if (AixmlExportStore.Reap() is > 0 and var reaped)
                log.LogInformation("Reaped {Count} stray export cache files.", reaped);
            var target = entry.Method.IsStatic ? null
                : ActivatorUtilities.GetServiceOrCreateInstance(services, entry.Method.DeclaringType!);
            object? returned;
            try { returned = entry.Method.Invoke(target, values); }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }

            var result = returned switch
            {
                Task<string> task => await task.ConfigureAwait(false),
                ValueTask<string> task => await task.ConfigureAwait(false),
                string text => text,
                _ => throw new InvalidOperationException($"Tool '{name}' returned an unsupported result type."),
            };
            ct.ThrowIfCancellationRequested();
            return AsJson(result);
        }
        finally { AixmlBackend.ClientTool.Value = previous; }
    }

    private static object?[] Bind(Entry entry, JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new RequestError("badArguments", "The arguments file must contain one JSON object.");
        var supplied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            if (!supplied.TryAdd(property.Name, property.Value))
                throw new RequestError("badArguments", $"Duplicate argument '{property.Name}'.");

        var (properties, required) = ToolArguments.Shape(entry.Descriptor.InputSchema);
        var received = supplied.Keys.ToArray();
        foreach (var (from, to) in ToolArguments.Renames(properties, received))
        {
            supplied[to] = supplied[from];
            supplied.Remove(from);
        }
        // Even a zero-argument tool must reject unknown keys, not silently ignore them.
        var unknown = ToolArguments.Unrecognised(properties, received);
        if (properties.Count == 0) unknown = received.ToList();
        if (unknown.Count > 0)
            throw new RequestError("badArguments",
                $"Unknown arguments for '{entry.Descriptor.Name}': {string.Join(", ", unknown)}. " +
                $"Accepted: {string.Join(", ", properties)}.");
        var missing = ToolArguments.Missing(required, supplied.Keys.ToList());
        if (missing.Count > 0)
            throw new RequestError("badArguments", $"Missing required arguments: {string.Join(", ", missing)}.");

        var nullability = new NullabilityInfoContext();
        return entry.Method.GetParameters().Select(parameter =>
        {
            if (parameter.ParameterType == typeof(CancellationToken)) return (object)ct;
            if (!supplied.TryGetValue(parameter.Name!, out var value))
            {
                if (parameter.HasDefaultValue) return parameter.DefaultValue;
                throw new RequestError("badArguments", $"Missing argument '{parameter.Name}'.");
            }
            if (value.ValueKind == JsonValueKind.Null &&
                nullability.Create(parameter).ReadState == NullabilityState.NotNull)
                throw new RequestError("badArguments", $"Argument '{parameter.Name}' cannot be null.");
            try
            {
                // Match the existing diagnostic binder for JSON-document-as-string parameters.
                if (parameter.ParameterType == typeof(string) &&
                    value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    return value.GetRawText();
                return value.Deserialize(parameter.ParameterType);
            }
            catch (JsonException e)
            {
                throw new RequestError("badArguments", $"Argument '{parameter.Name}': {e.Message}");
            }
        }).ToArray();
    }

    private static string AsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return text;
        }
        catch (JsonException)
        {
            // Reference and pane tools intentionally return prose, not JSON.
            return Json.Object(new { text });
        }
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var parsed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var flag = args[i];
            if (!ValueFlags.Contains(flag) && !flag.Equals("--list-tools", StringComparison.OrdinalIgnoreCase))
                throw new RequestError("badArguments", $"Unknown or incompatible CLI option '{flag}'.");
            string? value = null;
            if (ValueFlags.Contains(flag))
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(args[i]))
                    throw new RequestError("badArguments", $"Option '{flag}' needs a value.");
                value = args[i];
            }
            if (!parsed.TryAdd(flag, value))
                throw new RequestError("badArguments", $"Option '{flag}' was supplied more than once.");
        }
        if (Modes.Count(parsed.ContainsKey) != 1)
            throw new RequestError("badArguments", "Choose exactly one of --list-tools, --describe-tool or --invoke.");
        if (parsed.ContainsKey("--args-file") && !parsed.ContainsKey("--invoke"))
            throw new RequestError("badArguments", "--args-file requires --invoke.");
        if (parsed.ContainsKey("--invoke") && !parsed.ContainsKey("--args-file"))
            throw new RequestError("badArguments", "--invoke requires --args-file (use {} for no arguments).");
        return parsed;
    }

    private static int PositiveNumber(Dictionary<string, string?> options, string flag, int fallback, int maximum)
    {
        if (!options.TryGetValue(flag, out var value)) return fallback;
        if (!int.TryParse(value, out var number) || number < 1 || number > maximum)
            throw new RequestError("badArguments", $"{flag} must be an integer from 1 to {maximum}.");
        return number;
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        try
        {
            var options = Parse(args);
            var port = PositiveNumber(options, "--port", 0, 65535);
            var timeout = PositiveNumber(options, "--timeout", 300, 86400);
            if (options.TryGetValue("--lvversion", out var version))
            {
                if (LabViewVersionStamp.ParseOverride(version!) is not { } release)
                    throw new RequestError("badArguments", $"'{version}' is not a LabVIEW year or version stamp.");
                LabViewVersionStamp.SetOverride(release);
            }
            if (options.TryGetValue(AixmlBackend.Flag, out var backend))
            {
                if (AixmlBackend.NormaliseSetting(backend) is not { } normalised)
                    throw new RequestError("badArguments", $"Unknown AIXML backend '{backend}'.");
                AixmlBackend.SetFromCommandLine(normalised);
            }

            var registrations = new ServiceCollection();
            registrations.AddLogging(logging => logging.AddConsole(console =>
                console.LogToStandardErrorThreshold = LogLevel.Trace));
            registrations.AddSingleton(provider => new LvaiConnection(
                provider.GetRequiredService<ILogger<LvaiConnection>>(), port == 0 ? null : port));
            await using var provider = registrations.BuildServiceProvider();
            var router = new ToolRouter(provider, typeof(ToolRouter).Assembly);
            string answer;
            if (options.ContainsKey("--list-tools"))
                answer = Json.Object(new { tools = router.Catalogue() });
            else if (options.TryGetValue("--describe-tool", out var name))
            {
                if (!router._tools.TryGetValue(name!, out var entry))
                    throw new RequestError("unknownTool", $"No tool named '{name}'. Use --list-tools.");
                answer = Json.Object(entry.Descriptor);
            }
            else
            {
                string content;
                try { content = await File.ReadAllTextAsync(options["--args-file"]!, ct); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new RequestError("badArguments", $"Cannot read --args-file: {e.Message}");
                }
                using var arguments = ParseArguments(content);
                ServerSettings.EnsureExists();
                if (ServerSettings.MigrateDefaultOnce() == ServerSettings.Migration.Migrated)
                    Console.Error.WriteLine($"info: {ServerSettings.FilePath}: default backend migrated to pylabview.");
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeSpan.FromSeconds(timeout));
                ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; budget.Cancel(); };
                Console.CancelKeyPress += cancel;
                try { answer = await router.InvokeAsync(options["--invoke"]!, arguments.RootElement, budget.Token); }
                finally { Console.CancelKeyPress -= cancel; }
            }

            if (options.TryGetValue("--out", out var output))
                await File.WriteAllTextAsync(output!, answer, new UTF8Encoding(false), ct);
            Console.WriteLine(answer);
            return JsonNode.Parse(answer) is JsonObject obj && Json.LooksFailed(obj)
                ? obj["errorKind"]?.GetValue<string>() == "cancelled" ? 130 : 1
                : 0;
        }
        catch (RequestError e)
        {
            Console.WriteLine(Json.Error(e.Kind, e.Message));
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(Json.Error("cancelled", "The CLI call was cancelled or exceeded --timeout."));
            return 130;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Console.WriteLine(Json.Error("invocationFailed", e.Message, new { exception = e.GetType().Name }));
            return 1;
        }
    }

    private static JsonDocument ParseArguments(string content)
    {
        try { return JsonDocument.Parse(content); }
        catch (JsonException e) { throw new RequestError("badArguments", $"Invalid arguments JSON: {e.Message}"); }
    }
}
