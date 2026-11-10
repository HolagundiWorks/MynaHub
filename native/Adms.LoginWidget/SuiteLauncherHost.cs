using Osas.Core;

namespace Adms.LoginWidget;

/// <summary>
/// Starts the suite apps and MynaHub's sign-in for the launcher. The launcher has no company login any more: MynaHub owns it (contracts/session-broker.md), and
/// each app asks MynaHub for a session when it needs the database. Apps start with the AI and database addresses in their environment and nothing else.
/// </summary>
internal sealed class SuiteLauncherHost
{
    internal static SuiteLauncherHost Instance { get; } = new();

    /// <summary>Start an app of the suite (brought to the front when it already runs).</summary>
    internal void LaunchApp(SuiteAppDescriptor app)
    {
        var exe = SuiteCatalog.FindExecutable(app) ?? throw new InvalidOperationException($"{app.Title} is not installed.");
        SuiteCatalog.StartApp(exe);
    }

    /// <summary>Start the app that asked for the launcher (<see cref="LaunchRequest"/>): at once, since nothing has to be signed in here first.</summary>
    internal void RunPendingLaunch()
    {
        var request = LaunchRequest.Read();
        if (request == null)
            return;
        LaunchRequest.Clear();
        var descriptor = SuiteCatalog.Apps.FirstOrDefault(a =>
            string.Equals(a.Title, request.App, StringComparison.OrdinalIgnoreCase) || string.Equals(a.TicketApp, request.App, StringComparison.OrdinalIgnoreCase));
        if (descriptor == null)
            return;
        try
        {
            LaunchApp(descriptor);
        }
        catch (Exception ex)
        {
            MynaWindow.ShowError(ex.Message);
        }
    }

    internal static string TrayTooltip()
    {
        var s = SuiteStatus.Instance;
        return $"Myna — AI: {Word(s.Ai)}";
    }

    /// <summary>The first word of an AI line (<c>ready</c>, <c>starting</c>, …); <c>stopped</c> for anything that is not up.</summary>
    internal static string Word(string ai)
    {
        if (ai.StartsWith("ready", StringComparison.Ordinal))
            return "ready";
        if (ai.StartsWith("starting", StringComparison.Ordinal) || ai.StartsWith("checking", StringComparison.Ordinal))
            return "starting";
        return "stopped";
    }
}
