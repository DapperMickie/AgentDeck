namespace AgentDeck.Runner.Services;

/// <summary>Builds platform-appropriate shell launches for interactive terminals and orchestration commands.</summary>
public static class ShellLaunchBuilder
{
    public static string ResolveDefaultShell(string? configuredShell)
    {
        if (!string.IsNullOrWhiteSpace(configuredShell))
        {
            return configuredShell;
        }

        if (OperatingSystem.IsWindows())
        {
            return "powershell.exe";
        }

        return File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
    }

    public static (string Command, IReadOnlyList<string> Arguments) BuildInteractiveLaunch(
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool commandWasSpecified)
    {
        if (!commandWasSpecified || IsShellCommand(command))
        {
            return (command, arguments);
        }

        if (OperatingSystem.IsWindows())
        {
            return ("powershell.exe",
            [
                "-NoExit",
                "-Command",
                $"Set-Location -LiteralPath {QuotePowerShell(workingDirectory)}; {BuildShellCommand(command, arguments)}"
            ]);
        }

        var shellPath = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        return (shellPath,
        [
            "-lc",
            $"cd {QuotePosix(workingDirectory)} && {BuildShellCommand(command, arguments)}; exec {shellPath}"
        ]);
    }

    public static (string Command, IReadOnlyList<string> Arguments) BuildBatchLaunch(string script, string workingDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return ("powershell.exe",
            [
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                $"Set-Location -LiteralPath {QuotePowerShell(workingDirectory)}; {script}"
            ]);
        }

        var shellPath = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        return (shellPath,
        [
            "-lc",
            $"cd {QuotePosix(workingDirectory)} && {script}"
        ]);
    }

    public static bool IsShellCommand(string command)
    {
        var commandName = Path.GetFileNameWithoutExtension(command.Trim()).ToLowerInvariant();
        return commandName is "pwsh" or "powershell" or "bash" or "sh" or "cmd";
    }

    private static string BuildShellCommand(string command, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return command;
        }

        if (OperatingSystem.IsWindows())
        {
            return $"& {QuotePowerShell(command)} {string.Join(" ", arguments.Select(QuotePowerShell))}";
        }

        return $"{QuotePosix(command)} {string.Join(" ", arguments.Select(QuotePosix))}";
    }

    private static string QuotePowerShell(string value) =>
        $"'{value.Replace("'", "''")}'";

    private static string QuotePosix(string value) =>
        $"'{value.Replace("'", "'\"'\"'")}'";
}
