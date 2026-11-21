using System.IO;

internal static class HostedStartupVerification
{
    public static int Run()
    {
        if (!Allow("Startup verification"))
            return 2;

        ScannerOwnershipVerification.RunStartupCases();
        Console.WriteLine("PASS Startup ownership for a localized installer in a custom folder");
        StartupValueVerification.Run();
        Console.WriteLine("PASS Startup command recheck before quarantine");
        return 0;
    }

    internal static bool Allow(string operation)
    {
        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if (OperatingSystem.IsWindows() &&
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
            Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows" &&
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
            Environment.GetEnvironmentVariable("UNINSTALLTRACE_DISPOSABLE_RUNNER") == "github-hosted" &&
            !string.IsNullOrWhiteSpace(runnerTemp) && Path.IsPathFullyQualified(runnerTemp) && Directory.Exists(runnerTemp))
            return true;

        Console.Error.WriteLine($"{operation} requires a disposable GitHub-hosted Windows runner.");
        return false;
    }
}
