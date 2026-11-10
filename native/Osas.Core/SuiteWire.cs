namespace Osas.Core;

/// <summary>Suite launch and shared local state (MynaHub hub).</summary>
public static class SuiteWire
{
    public const string LocalAppFolder = "O-Studio";

    public const string EnvOfficeDll = "OSTUDIO_OFFICE_DLL";

    public const string EnvAiRole = "OSTUDIO_AI_ROLE";
    public const string EnvAiHost = "OSTUDIO_AI_HOST";
    public const string EnvAiPort = "OSTUDIO_AI_PORT";
    public const string EnvAiToken = "OSTUDIO_AI_TOKEN";

    public const string EnvUpdateChannel = "OSTUDIO_UPDATE_CHANNEL_URL";
    public const string EnvNeonEnvFile = "OSTUDIO_NEON_ENV_FILE";

    public const string ProjectFileFormat = "osp";
    public const string ActiveProjectFormat = "studio-active-project";

    public static string LocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LocalAppFolder);

    public static string LocalPath(string fileName) =>
        Path.Combine(LocalDirectory, fileName);
}
