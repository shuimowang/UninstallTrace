using Microsoft.Win32;
using static DeepUninstaller.Localization;

namespace DeepUninstaller.Services;

internal sealed record RegistrationLabel(string Name, string Evidence, bool IsDuplicate, AppRegistrationRole Role = AppRegistrationRole.Unknown);

internal static class RegistrationLabels
{
    public static IReadOnlyDictionary<string, RegistrationLabel> Create(IReadOnlyList<InstalledApp> inventory,
        IReadOnlyDictionary<string, AppRoleAssessment>? roles = null)
    {
        var labels = new Dictionary<string, RegistrationLabel>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in inventory.GroupBy(app => app.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var siblings = group.ToArray();
            if (siblings.Length == 1)
            {
                var app = siblings[0];
                labels[app.Id] = new(app.Name, "", false);
                continue;
            }

            var suffixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in siblings)
            {
                var role = roles?.GetValueOrDefault(app.Id);
                suffixes[app.Id] = role is { Role: not AppRegistrationRole.Unknown } ? role.Label : RegistryScope(app);
            }

            // Only lengthen labels that still collide after the previous qualifier.
            Disambiguate(app => app.Version);
            Disambiguate(RegistryScope);
            Disambiguate(app => app.KeyName);
            Disambiguate(app => app.Id);
            foreach (var app in siblings)
            {
                var assessment = roles?.GetValueOrDefault(app.Id);
                var roleEvidence = assessment?.Evidence;
                var evidence = !string.IsNullOrWhiteSpace(roleEvidence) ? roleEvidence :
                    Text($"无法确认角色。登记项为 {app.KeyName}。", $"Role undetermined. Registration key: {app.KeyName}.");
                labels[app.Id] = new($"{app.Name} · {suffixes[app.Id]}", evidence, true, assessment?.Role ?? AppRegistrationRole.Unknown);
            }

            void Disambiguate(Func<InstalledApp, string> qualifier)
            {
                var collisions = siblings.GroupBy(app => suffixes[app.Id], StringComparer.OrdinalIgnoreCase)
                    .Where(collision => collision.Select(qualifier).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
                    .SelectMany(collision => collision).ToArray();
                foreach (var app in collisions)
                {
                    var detail = qualifier(app);
                    if (!string.IsNullOrWhiteSpace(detail))
                        suffixes[app.Id] += " · " + detail;
                }
            }
        }
        return labels;
    }

    private static string RegistryScope(InstalledApp app)
    {
        if (app.View == RegistryView.Registry32 || app.RegistryPath.StartsWith(@"Software\WOW6432Node\", StringComparison.OrdinalIgnoreCase))
            return app.Scope + Text(" · 32 位登记", " · 32-bit registration");
        return app.View == RegistryView.Registry64 ? app.Scope + Text(" · 64 位登记", " · 64-bit registration") : app.Scope;
    }

    public static string NameFor(InstalledApp app, IReadOnlyDictionary<string, RegistrationLabel> labels) =>
        labels.GetValueOrDefault(app.Id)?.Name ?? app.Name;

    public static string ConfirmationTarget(InstalledApp app, IReadOnlyDictionary<string, RegistrationLabel> labels, bool includeCommand = false)
    {
        var label = labels.GetValueOrDefault(app.Id);
        if (!includeCommand && label?.IsDuplicate != true)
            return app.Name;
        var command = UninstallCommand.TryCreate(app, out var parsed, out _) ? parsed.DisplayText : app.UninstallString;
        return (label?.Name ?? app.Name) + Environment.NewLine + command;
    }
}
