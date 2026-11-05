using System.Xml.Linq;
using Microsoft.Win32;
using UpsTools.AppTests.Support;
using UpsTools.Core;
using UpsTools.Services;

namespace UpsTools.AppTests;

/// <summary>What the app registers in Windows. The test run is its own profile, so this is never the installed copy's.</summary>
[Collection("Registry")]
public sealed class StartupAndNotifierTests : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public StartupAndNotifierTests() => SetRunValue(null);

    public void Dispose()
    {
        SetRunValue(null);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{Notifier.AppUserModelId}", false);
    }

    private static string? RunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(StartupRegistration.ValueName) as string;
    }

    private static void SetRunValue(string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (value is null) key.DeleteValue(StartupRegistration.ValueName, throwOnMissingValue: false);
        else key.SetValue(StartupRegistration.ValueName, value);
    }

    // ---- Start with Windows

    [Fact]
    public void Enabling_adds_a_run_entry_that_starts_this_exe_in_the_background()
    {
        Assert.False(StartupRegistration.IsEnabled);

        StartupRegistration.SetEnabled(true);

        Assert.True(StartupRegistration.IsEnabled);
        Assert.Equal($"\"{Environment.ProcessPath}\" --background --profile {TestProfile.Name}", RunValue());

        StartupRegistration.SetEnabled(false);
        Assert.False(StartupRegistration.IsEnabled);
        Assert.Null(RunValue());
        StartupRegistration.SetEnabled(false); // nothing to remove: fine
    }

    [Fact]
    public void An_entry_of_an_older_location_is_pointed_at_this_exe()
    {
        SetRunValue("\"C:\\Old\\UpsTools.exe\" --background");
        StartupRegistration.RefreshPathIfEnabled();
        Assert.Equal(StartupRegistration.Command, RunValue());
    }

    [Fact]
    public void Refreshing_adds_nothing_when_disabled()
    {
        StartupRegistration.RefreshPathIfEnabled();
        Assert.Null(RunValue());
    }

    [Fact]
    public void Uninstalling_removes_only_this_exes_entry()
    {
        SetRunValue("\"C:\\Other copy\\UpsTools.exe\" --background");
        Assert.False(StartupRegistration.RemoveIfThisExe());
        Assert.NotNull(RunValue());

        StartupRegistration.SetEnabled(true);
        Assert.True(StartupRegistration.RemoveIfThisExe());
        Assert.Null(RunValue());
        Assert.False(StartupRegistration.RemoveIfThisExe());
    }

    // ---- notifications

    [Fact]
    public void A_toast_has_title_body_footnote_attribution_and_logo()
    {
        var xml = Notifier.ToastXml("Grid lost — running on battery", "Battery 87 %", "Started a.ps1", "Bluetti EL100V2",
            "file:///C:/logo.png", important: true);

        Assert.Equal("open", (string?)xml.Attribute("launch"));
        Assert.Equal("long", (string?)xml.Attribute("duration"));
        var binding = xml.Element("visual")!.Element("binding")!;
        Assert.Equal("ToastGeneric", (string?)binding.Attribute("template"));
        Assert.Equal(["Grid lost — running on battery", "Battery 87 %", "Started a.ps1", "Bluetti EL100V2"],
            binding.Elements("text").Select(t => t.Value));
        Assert.Equal("attribution", (string?)binding.Elements("text").Last().Attribute("placement"));
        Assert.Equal("file:///C:/logo.png", (string?)binding.Element("image")!.Attribute("src"));
        Assert.Equal("appLogoOverride", (string?)binding.Element("image")!.Attribute("placement"));
    }

    [Fact]
    public void A_plain_toast_has_only_title_and_body()
    {
        var xml = Notifier.ToastXml("Test", "Body & <stuff>", null, null, null, important: false);
        Assert.Null(xml.Attribute("duration"));
        Assert.Equal(["Test", "Body & <stuff>"], xml.Descendants("text").Select(t => t.Value));
        Assert.Empty(xml.Descendants("image"));
        Assert.Contains("Body &amp; &lt;stuff&gt;", xml.ToString(SaveOptions.DisableFormatting));
    }

    [Theory]
    [InlineData(NoticeTone.Good, TrayState.GridOnline)]
    [InlineData(NoticeTone.Warning, TrayState.OnBattery)]
    [InlineData(NoticeTone.Problem, TrayState.Problem)]
    [InlineData(NoticeTone.Neutral, TrayState.Neutral)]
    public void Notice_tones_pick_the_icon_colour(NoticeTone tone, TrayState state)
    {
        Assert.Equal(state, Notifier.ToTray(tone));
    }

    [Fact]
    public void Unregistering_removes_the_registration_and_the_images_but_nothing_else()
    {
        using var temp = new TempFolder();
        var log = new AppLog(temp.Path);
        Directory.CreateDirectory(Notifier.CacheFolder);
        foreach (var file in new[] { "app.png", "logo-neutral.png", "logo-problem.png" })
            File.WriteAllText(Path.Combine(Notifier.CacheFolder, file), "png");
        File.WriteAllText(Path.Combine(Notifier.CacheFolder, "mine.png"), "the user's");
        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Notifier.AppUserModelId}"))
            key.SetValue("DisplayName", "UPS Tools");

        Notifier.Unregister(log);

        Assert.Null(Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{Notifier.AppUserModelId}"));
        Assert.Equal(["mine.png"], Directory.GetFiles(Notifier.CacheFolder).Select(Path.GetFileName));
        Assert.Contains(log.Recent, e => e.Message == "Removed the notification registration");

        File.Delete(Path.Combine(Notifier.CacheFolder, "mine.png"));
        Notifier.Unregister(log);
        Assert.False(Directory.Exists(Notifier.CacheFolder)); // empty: gone
        Assert.Single(log.Recent, e => e.Message == "Removed the notification registration");
        Notifier.Unregister(log); // nothing left: fine
    }
}
