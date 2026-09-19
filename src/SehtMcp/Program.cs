using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SehtMcp;

var config = SehtConfig.Load(args);
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<PluginWorkspace>();
builder.Services.AddSingleton<AssetService>();
builder.Services.AddSingleton<ExternalTools>();
builder.Services.AddMcpServer(o =>
{
    o.ServerInfo = new() { Name = "SehtMCP", Version = "0.1.0" };
    o.ServerInstructions = "Skyrim SE authoring. Start with seht_status and seht_guide. Discover record_types and record_schema before editing. Use canonical FormKeys (000800:Example.esp), not load-order FormIDs. Changes live in a session until plugin_save. Keep the returned revision for optimistic concurrency. plugin_validate is structural validation, not an in-game playtest. NIF previews are untextured geometry diagnostics.";
}).WithStdioServerTransport().WithToolsFromAssembly().WithResourcesFromAssembly().WithPromptsFromAssembly();
await builder.Build().RunAsync();
