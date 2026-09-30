using System.ComponentModel;
using System.Diagnostics;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Authentication;

namespace ConversationAssistant_App.WorkIQ;

public sealed class WorkIqProcess
{
    public string? Account { get; set; }
    public static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CONVERSATIONASSISTANT_WORKIQ_PATH");
        // Keep the legacy override working for existing installations.
        if (string.IsNullOrWhiteSpace(configured))
            configured = Environment.GetEnvironmentVariable("MEETINGCOPILOT_WORKIQ_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathFullyQualified(configured) &&
                Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(configured)) return configured;
            throw new WorkIqException("CONVERSATIONASSISTANT_WORKIQ_PATH must be an absolute path to an existing workiq.exe.");
        }
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(dir => Path.IsPathFullyQualified(dir.Trim('"')))
            .Select(dir => Path.Combine(dir.Trim('"'), "workiq.exe"))
            .FirstOrDefault(File.Exists);
        if (onPath is not null) return onPath;
        var scout = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "scout", "resources", "app.asar.unpacked", "node_modules",
            "@microsoft", "workiq", "bin", "win-x64", "workiq.exe");
        if (File.Exists(scout)) return scout;
        throw new WorkIqException("Install the official Work IQ CLI and set CONVERSATIONASSISTANT_WORKIQ_PATH to workiq.exe.");
    }

    public async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ResolveExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in WorkIqAccountArguments.WithAccount(arguments, Account))
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var result = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new WorkIqException(
                    error.Contains("EULA", StringComparison.OrdinalIgnoreCase)
                        ? "Accept the official Work IQ evaluation terms before using Work IQ."
                        : $"Work IQ failed (exit code {process.ExitCode}). Check sign-in, consent, and network.");
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Win32Exception ex)
        {
            throw new WorkIqException("Cannot start Work IQ CLI. Check the executable path.", ex);
        }
    }
}
