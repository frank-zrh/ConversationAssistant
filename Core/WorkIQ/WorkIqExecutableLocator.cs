using ConversationAssistant.Core.Conversation;

namespace ConversationAssistant.Core.WorkIQ;

public static class WorkIqExecutableLocator
{
    public static string Resolve(string? configured, string? legacyConfigured,
        IEnumerable<string> searchDirectories, string? applicationData, string? localApplicationData)
    {
        var explicitPath = string.IsNullOrWhiteSpace(configured) ? legacyConfigured : configured;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            explicitPath = explicitPath.Trim().Trim('"');
            if (Path.IsPathFullyQualified(explicitPath) && File.Exists(explicitPath))
            {
                if (Path.GetExtension(explicitPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    return explicitPath;
                if (Path.GetFileName(explicitPath).Equals("workiq.cmd", StringComparison.OrdinalIgnoreCase))
                {
                    var native = FindNpmExecutable(Path.GetDirectoryName(explicitPath)!);
                    if (native is not null) return native;
                }
            }
            throw new WorkIqException(
                "CONVERSATIONASSISTANT_WORKIQ_PATH must point to an existing workiq.exe or an npm workiq.cmd " +
                "with its bundled node_modules/@microsoft/workiq/bin/win-x64/workiq.exe. " +
                "Repair the installation with npm.cmd install --global @microsoft/workiq; do not rename the .cmd file.");
        }

        var directories = searchDirectories.Select(directory => directory.Trim().Trim('"'))
            .Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var directory in directories)
        {
            var executable = Path.Combine(directory, "workiq.exe");
            if (File.Exists(executable)) return executable;
        }
        foreach (var directory in directories)
        {
            var native = FindNpmExecutable(directory);
            if (native is not null) return native;
        }
        if (!string.IsNullOrWhiteSpace(applicationData) && Path.IsPathFullyQualified(applicationData))
        {
            var native = FindNpmExecutable(Path.Combine(applicationData, "npm"));
            if (native is not null) return native;
        }
        if (!string.IsNullOrWhiteSpace(localApplicationData) && Path.IsPathFullyQualified(localApplicationData))
        {
            var scout = Path.Combine(localApplicationData, "Programs", "scout", "resources",
                "app.asar.unpacked", "node_modules", "@microsoft", "workiq", "bin", "win-x64", "workiq.exe");
            if (File.Exists(scout)) return scout;
        }
        throw new WorkIqException(
            "Work IQ CLI was not found. Install Node.js LTS, then run npm.cmd install --global @microsoft/workiq. " +
            "An npm workiq.cmd launcher is normal; the app locates its bundled Windows x64 workiq.exe. " +
            "For a custom installation, set CONVERSATIONASSISTANT_WORKIQ_PATH to workiq.cmd or workiq.exe and restart the app.");
    }

    private static string? FindNpmExecutable(string directory)
    {
        var executable = Path.Combine(directory, "node_modules", "@microsoft", "workiq", "bin", "win-x64", "workiq.exe");
        if (File.Exists(executable)) return executable;
        directory = Path.TrimEndingDirectorySeparator(directory);
        if (Path.GetFileName(directory).Equals(".bin", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(directory) is { } nodeModules)
        {
            executable = Path.Combine(nodeModules, "@microsoft", "workiq", "bin", "win-x64", "workiq.exe");
            if (File.Exists(executable)) return executable;
        }
        return null;
    }
}
