using System.Text.RegularExpressions;

namespace UpsTools.Services;

/// <summary>
/// Which copy of UPS Tools this is. Normally there is one; <c>--profile name</c> on the command line, or the
/// environment variable <c>UPSTOOLS_PROFILE=name</c>, runs a separate one (e.g. for the UI tests) with its own
/// settings and log, single-instance signals, Start with Windows entry, notification registration and
/// administrator tasks, so it neither touches nor talks to the installed copy.
/// </summary>
public static partial class AppProfile
{
    public const string Argument = "--profile";
    public const string EnvironmentVariable = "UPSTOOLS_PROFILE";

    /// <summary>The profile's name, or null for the normal copy.</summary>
    public static string? Name { get; } = Parse(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>"" for the normal copy, otherwise "-name": appended to the names of what the copy owns.</summary>
    public static string Suffix => Name is null ? "" : "-" + Name;

    /// <summary>Passes the profile on to another UpsTools.exe: "" for the normal copy, otherwise " --profile name".</summary>
    public static string CommandLine => Name is null ? "" : $" {Argument} {Name}";

    /// <summary>The profile named on the command line, else by the environment variable; null when there is none or it isn't a valid name.</summary>
    internal static string? Parse(IReadOnlyList<string> args, string? environment)
    {
        var name = environment;
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i].Equals(Argument, StringComparison.OrdinalIgnoreCase))
                name = args[i + 1];
        return name is not null && ValidName().IsMatch(name) ? name : null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex ValidName();
}
