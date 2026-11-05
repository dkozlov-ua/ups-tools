using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UpsTools.Services;

/// <summary>
/// Hands a script's environment variables (<see cref="Core.ScriptEnvironment"/>) to its administrator task: the task
/// starts <c>UpsTools.exe --run-script</c> with a fixed command line, so UPS Tools writes them to a file named
/// after the script and its arguments just before starting the task, and <c>--run-script</c> reads it.
/// </summary>
/// <remarks>
/// The task runs as the same user, so it finds the file in the same %LOCALAPPDATA%. It doesn't know the app's
/// profile (<see cref="AppProfile"/>), so the folder is the same for every profile. A file older than
/// <see cref="MaxAge"/> is ignored: running the task from Task Scheduler yourself gives the script no UPS state.
/// </remarks>
internal static class ScriptEnvironmentFile
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpsTools", "script-env");

    private sealed record Contents(DateTimeOffset Written, Dictionary<string, string?> Variables);

    public static string PathFor(string script, string arguments)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(script + "\n" + arguments));
        return Path.Combine(Folder, Convert.ToHexString(hash, 0, 12).ToLowerInvariant() + ".json");
    }

    public static void Write(string script, string arguments, IReadOnlyDictionary<string, string?> variables) =>
        Write(script, arguments, variables, DateTimeOffset.Now);

    /// <param name="written">When it was written: now, unless a test says otherwise.</param>
    internal static void Write(string script, string arguments, IReadOnlyDictionary<string, string?> variables, DateTimeOffset written)
    {
        Directory.CreateDirectory(Folder);
        var file = PathFor(script, arguments);
        var temp = file + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Contents(written, new(variables))));
        File.Move(temp, file, overwrite: true);
    }

    /// <returns>The variables written for this script in the last <see cref="MaxAge"/>, or null.</returns>
    public static IReadOnlyDictionary<string, string?>? Read(string script, string arguments)
    {
        try
        {
            var contents = JsonSerializer.Deserialize<Contents>(File.ReadAllText(PathFor(script, arguments)));
            var age = DateTimeOffset.Now - contents!.Written;
            return age < MaxAge && age > -MaxAge ? contents.Variables : null;
        }
        catch
        {
            return null; // none, or unreadable
        }
    }

    /// <summary>For the uninstaller.</summary>
    public static void DeleteAll()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
    }
}
