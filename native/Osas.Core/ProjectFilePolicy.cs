namespace Osas.Core;

/// <summary>
/// Where project files (<c>.osp</c>) may go. They stay on this PC: Tren and Falcon share the local file, and only Kivo is synced to the
/// cloud, with Falcon's exported CSV as the one way project data reaches the company database. Pushing the project file itself to
/// Neon is off; setting <c>OSAS_PUSH_PROJECT_FILES=1</c> turns it back on for a machine that should do it.
/// </summary>
public static class ProjectFilePolicy
{
    public const string EnvVar = "OSAS_PUSH_PROJECT_FILES";

    /// <summary>False unless the environment variable is exactly <c>1</c>.</summary>
    public static bool PushToCloud => Environment.GetEnvironmentVariable(EnvVar) == "1";

    /// <summary>The sentence the logs and the Cloud sync page show when the push is off.</summary>
    public const string LocalOnlyNotice = "Project files stay on this PC. Only Kivo is synced; Falcon's exported CSV is how project data reaches the company database.";
}
