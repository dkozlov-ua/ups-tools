using System.IO;
using System.Net.Http;
using UpsTools.Services;

namespace UpsTools.ViewModels;

/// <summary>
/// The update button in Configuration → About: "Check for updates", then "Update to X.Y.Z" when GitHub has a newer
/// release, which downloads its setup and runs it (the setup exits UPS Tools, updates it and opens it again).
/// </summary>
public sealed class UpdateViewModel(Updater updater, AppLog log, Version? current = null) : ObservableObject
{
    private readonly Version _current = current ?? Updater.CurrentVersion;
    private AppRelease? _available;

    public string ActionText { get; private set => Set(ref field, value); } = "Check for updates";
    public bool IsBusy
    {
        get;
        private set
        {
            if (Set(ref field, value)) OnPropertyChanged(nameof(CanAct));
        }
    }

    /// <summary>The button can be clicked: nothing is under way.</summary>
    public bool CanAct => !IsBusy;
    public string Status { get; private set => Set(ref field, value); } = "";

    /// <summary>The newer release's page, for "What's new"; null until one is found.</summary>
    public string? ReleasePage
    {
        get;
        private set
        {
            if (Set(ref field, value)) OnPropertyChanged(nameof(HasRelease));
        }
    }

    public bool HasRelease => ReleasePage is { Length: > 0 };

    /// <summary>Checks for a newer release, or, once one is found, downloads and installs it.</summary>
    public Task ActAsync() => _available is { } release ? InstallAsync(release) : CheckAsync();

    private async Task CheckAsync()
    {
        IsBusy = true;
        ActionText = "Checking…";
        Status = "";
        try
        {
            var latest = await updater.GetLatestAsync();
            if (latest.Version > _current)
            {
                _available = latest;
                ReleasePage = latest.PageUrl;
                ActionText = $"Update to {Text(latest.Version)}";
                Status = $"UPS Tools {Text(latest.Version)} is available.";
                return;
            }
            ActionText = "Check for updates";
            Status = "You have the latest version.";
        }
        catch (Exception ex)
        {
            ActionText = "Check for updates";
            Status = $"Could not check for updates: {Reason(ex)}";
            log.Info($"Checking for updates failed: {Reason(ex)}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAsync(AppRelease release)
    {
        IsBusy = true;
        ActionText = "Downloading…";
        Status = $"Downloading UPS Tools {Text(release.Version)}.";
        log.Info($"Downloading UPS Tools {Text(release.Version)} from {release.SetupUrl}");
        try
        {
            var downloading = true; // a progress report can arrive late: Progress posts it
            string setup;
            try
            {
                setup = await updater.DownloadAsync(release, new Progress<double>(p =>
                {
                    if (Volatile.Read(ref downloading)) ActionText = $"Downloading… {p:P0}";
                }));
            }
            finally
            {
                Volatile.Write(ref downloading, false);
            }
            ActionText = "Installing…";
            Status = "The setup closes UPS Tools, updates it and opens it again.";
            log.Info($"Installing UPS Tools {Text(release.Version)}; the setup restarts the app");
            updater.Install(setup);
            // Busy for good: the setup is about to exit this app.
        }
        catch (Exception ex)
        {
            IsBusy = false;
            ActionText = $"Update to {Text(release.Version)}";
            Status = $"The update failed: {Reason(ex)}";
            log.Warn($"Updating to {Text(release.Version)} failed: {Reason(ex)}");
        }
    }

    private static string Text(Version version) => version.ToString(3);

    private static string Reason(Exception ex) => ex switch
    {
        TaskCanceledException => "GitHub did not answer in time",
        HttpRequestException { InnerException: { } inner } => inner.Message,
        IOException or HttpRequestException => ex.Message,
        _ => ex.GetBaseException().Message,
    };
}
