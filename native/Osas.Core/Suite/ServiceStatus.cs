namespace Osas.Core.Suite;

/// <summary>How a service the launcher looks after is doing: shown as a coloured marker.</summary>
public enum ServiceLevel
{
    /// <summary>Up and working (green).</summary>
    Running,

    /// <summary>Not up yet, or waiting for something it needs, such as the sign-in (amber).</summary>
    Waiting,

    /// <summary>Should be up and is not (red).</summary>
    Stopped,

    /// <summary>Not set up, or switched off on purpose (grey).</summary>
    Off,
}

/// <summary>Reads the status lines the launcher writes (<c>launcher-status.txt</c>) as a <see cref="ServiceLevel"/>.</summary>
public static class ServiceStatus
{
    public static ServiceLevel ForAi(string text)
    {
        text = (text ?? "").TrimStart();
        if (text.StartsWith("ready", StringComparison.OrdinalIgnoreCase))
            return ServiceLevel.Running;
        if (text.StartsWith("starting", StringComparison.OrdinalIgnoreCase) || text.StartsWith("checking", StringComparison.OrdinalIgnoreCase))
            return ServiceLevel.Waiting;
        return ServiceLevel.Stopped;
    }

    /// <summary>The database server of the suite is the cloud sync (Neon).</summary>
    public static ServiceLevel ForCloud(string text)
    {
        text = (text ?? "").TrimStart();
        if (text.StartsWith("running", StringComparison.OrdinalIgnoreCase))
            return ServiceLevel.Running;
        if (text.StartsWith("could not connect", StringComparison.OrdinalIgnoreCase) || text.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            return ServiceLevel.Stopped;
        if (text.StartsWith("credentials found", StringComparison.OrdinalIgnoreCase) || text.StartsWith("checking", StringComparison.OrdinalIgnoreCase))
            return ServiceLevel.Waiting;
        return ServiceLevel.Off;
    }

    /// <summary>The company file: open and signed in is up; open but waiting for sign-in is waiting; not found is a fault; closed or none is off.</summary>
    public static ServiceLevel ForCompanyFile(string text)
    {
        text = text ?? "";
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return ServiceLevel.Stopped;
        if (text.Contains("signed in as", StringComparison.OrdinalIgnoreCase))
            return ServiceLevel.Running;
        if (text.Contains("sign-in needed", StringComparison.OrdinalIgnoreCase) || text.Contains("locked", StringComparison.OrdinalIgnoreCase))
            return ServiceLevel.Waiting;
        return ServiceLevel.Off;
    }

    public static string Word(ServiceLevel level) => level switch
    {
        ServiceLevel.Running => "Running",
        ServiceLevel.Waiting => "Waiting",
        ServiceLevel.Stopped => "Not running",
        _ => "Off",
    };
}
