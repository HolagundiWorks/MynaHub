namespace Osas.Core;

/// <summary>Suite launch and shared local state (Kivo hub).</summary>
public static class SuiteWire
{
    public const string LocalAppFolder = "O-Studio";

    public const string EnvCompany = "OSTUDIO_COMPANY";
    public const string EnvTicket = "OSTUDIO_TICKET";
    public const string EnvUser = "OSTUDIO_USER";
    public const string EnvOfficeDll = "OSTUDIO_OFFICE_DLL";
    public const string EnvProjectId = "OSTUDIO_PROJECT_ID";
    public const string EnvProjectOsp = "OSTUDIO_PROJECT_OSP";
    public const string EnvProjectName = "OSTUDIO_PROJECT_NAME";

    public const string EnvAiRole = "OSTUDIO_AI_ROLE";
    public const string EnvAiHost = "OSTUDIO_AI_HOST";
    public const string EnvAiPort = "OSTUDIO_AI_PORT";
    public const string EnvAiToken = "OSTUDIO_AI_TOKEN";

    /// <summary>Where the launcher's database service listens (contracts/session-broker.md section 2); set for every app the launcher starts, next to the AI variables.</summary>

    public const string EnvUpdateChannel = "OSTUDIO_UPDATE_CHANNEL_URL";
    public const string EnvNeonEnvFile = "OSTUDIO_NEON_ENV_FILE";

    public const string ProjectFileFormat = "osp";
    public const string ActiveProjectFormat = "studio-active-project";
    public const string SelectedProjectFormat = "studio-selected-project";
    public const string SelectedProjectFile = "selected-project.json";

    public static string LocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LocalAppFolder);

    public static string LocalPath(string fileName) =>
        Path.Combine(LocalDirectory, fileName);
}
