using Brutal.Logging;
using Microsoft.Extensions.Logging;

namespace Prospect.Engine.Diagnostics;

/// <summary>Log categories shared by the client and the server, on top of BRUTAL's logging.</summary>
public static class Logs
{
    /// <summary>
    /// Sets up logging to the console from <paramref name="minimumLevel"/> up. <paramref name="configure"/>
    /// can add to that: the tests use it to count what is logged without printing it.
    /// </summary>
    public static void Initialize(LogLevel minimumLevel, Action<ILoggingBuilder>? configure = null)
    {
        LogSystem.Initialize(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
            configure?.Invoke(builder);
        });

        App = LogCategory.Make("App");
        Net = LogCategory.Make("Net");
        Sim = LogCategory.Make("Sim");
        Render = LogCategory.Make("Render");
    }

    public static LogCategory App { get; private set; } = null!;
    public static LogCategory Net { get; private set; } = null!;
    public static LogCategory Sim { get; private set; } = null!;
    public static LogCategory Render { get; private set; } = null!;
}
