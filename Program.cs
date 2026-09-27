using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using AutoPurgeService;

// "--once" runs a single purge immediately and exits, for testing a configuration from the command line.
// It is removed before the remaining arguments are read as configuration, where it would swallow the next one.
bool runOnce = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
var hostArgs = args.Where(arg => !string.Equals(arg, "--once", StringComparison.OrdinalIgnoreCase)).ToArray();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = hostArgs,
    // A one-off run is usually started from some other folder; read the configuration next to the
    // executable, as the service does.
    ContentRootPath = runOnce ? AppContext.BaseDirectory : null
});

// Configures the lifetime of the application to run as a Windows Service,
// and configures logging to write to the Windows Event Log.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "AutoPurgeService";
});

// Bind PurgeSettings configuration section to the strongly-typed options
builder.Services.Configure<PurgeSettings>(builder.Configuration.GetSection(PurgeSettings.SectionName));

// Register background service worker, or just the worker itself for a one-off run
if (runOnce)
{
    builder.Services.AddSingleton<Worker>();
}
else
{
    builder.Services.AddHostedService<Worker>();
}

// Explicitly customize EventLog settings if running on Windows
#pragma warning disable CA1416 // Validate platform compatibility
if (OperatingSystem.IsWindows())
{
    builder.Services.Configure<EventLogSettings>(settings =>
    {
        settings.SourceName = "AutoPurgeService";
    });
}
#pragma warning restore CA1416

var host = builder.Build();

if (runOnce)
{
    // Disposing the host flushes the console log before the process exits.
    using (host)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return host.Services.GetRequiredService<Worker>().RunOnce(cts.Token) ? 0 : 1;
    }
}

host.Run();
return 0;
