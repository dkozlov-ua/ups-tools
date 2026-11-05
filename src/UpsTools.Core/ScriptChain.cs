namespace UpsTools.Core;

/// <summary>One script of an event, as <see cref="ScriptChain"/> sees it.</summary>
/// <param name="Name">File name, for logs and notifications.</param>
/// <param name="WaitForExit">The next script starts only after this one finished successfully.</param>
public sealed record ScriptStep(string Name, bool WaitForExit);

/// <summary>
/// What running one script came to: for a script that is waited for, whether it finished successfully
/// (exit code 0); for any other, whether it started.
/// </summary>
public readonly record struct StepResult(bool Ok, string? Problem = null)
{
    public static StepResult Success => new(true);
    public static StepResult Failure(string problem) => new(false, problem);
}

/// <summary>A script that couldn't be started, or that was waited for and failed.</summary>
public sealed record FailedStep(int Index, string Name, string Problem);

/// <summary>
/// Runs an event's scripts in their listed order. A script marked <see cref="ScriptStep.WaitForExit"/> must
/// finish successfully before the next one starts; if it fails, the scripts after it are skipped. Any other
/// script is started and left running, and one that can't be started doesn't stop the others.
/// </summary>
/// <remarks>
/// Single-threaded: create, run and read it on one thread (the UI thread). Everything up to the first script
/// that is waited for happens synchronously inside <see cref="RunAsync"/>, so right after the call the chain
/// says what has been started and what it is waiting for.
/// </remarks>
public sealed class ScriptChain(IReadOnlyList<ScriptStep> steps)
{
    private int _next;

    public IReadOnlyList<ScriptStep> Steps { get; } = steps;

    /// <summary>Scripts that were started (and, if waited for, finished successfully, or were still running when the chain was stopped).</summary>
    public List<string> Started { get; } = [];

    public List<FailedStep> Failed { get; } = [];

    /// <summary>The script the chain is waiting for right now.</summary>
    public string? Waiting { get; private set; }

    /// <summary>Scripts not started yet; once the chain is done, the ones it skipped.</summary>
    public IReadOnlyList<string> Pending => Steps.Skip(_next).Select(s => s.Name).ToList();

    /// <summary>The script that was waited for and failed, so the ones after it were skipped.</summary>
    public FailedStep? StoppedAt { get; private set; }

    /// <summary>The chain was cancelled before it reached its end.</summary>
    public bool Cancelled { get; private set; }

    public bool Done { get; private set; }

    /// <summary>Scripts that didn't run because the chain was stopped or cancelled.</summary>
    public IReadOnlyList<string> Skipped => Done && (Cancelled || StoppedAt is not null) ? Pending : [];

    /// <param name="run">
    /// Runs step <c>index</c>. For a step that is waited for, it returns when the script has exited, and throws
    /// <see cref="OperationCanceledException"/> if the token is cancelled first (the script keeps running).
    /// </param>
    public async Task RunAsync(Func<int, ScriptStep, CancellationToken, Task<StepResult>> run, CancellationToken cancel)
    {
        if (_next != 0 || Done) throw new InvalidOperationException("A chain runs once.");
        try
        {
            while (_next < Steps.Count)
            {
                if (cancel.IsCancellationRequested)
                {
                    Cancelled = true;
                    return;
                }

                var index = _next++;
                var step = Steps[index];
                if (step.WaitForExit) Waiting = step.Name;

                StepResult result;
                try
                {
                    result = await run(index, step, cancel);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    // Stopped waiting; the script itself is still running.
                    Started.Add(step.Name);
                    Cancelled = true;
                    return;
                }
                catch (Exception ex)
                {
                    result = StepResult.Failure(ex.Message);
                }
                finally
                {
                    Waiting = null;
                }

                if (result.Ok)
                {
                    Started.Add(step.Name);
                    continue;
                }

                var failed = new FailedStep(index, step.Name, result.Problem ?? "failed");
                Failed.Add(failed);
                if (step.WaitForExit)
                {
                    StoppedAt = failed;
                    return;
                }
            }
        }
        finally
        {
            Done = true;
        }
    }
}

/// <summary>How notifications describe a <see cref="ScriptChain"/>.</summary>
public static class ScriptReport
{
    /// <summary>
    /// One line for an event's notification, describing the chain as it stands, e.g.
    /// "Started a.ps1; waiting for b.ps1, then c.ps1", or null when the event has no scripts.
    /// </summary>
    /// <param name="stopped">Another event's scripts that this event stopped, and the ones of them it skipped.</param>
    public static string? Footnote(ScriptChain? chain, (UpsEventKind Kind, IReadOnlyList<string> Skipped)? stopped = null)
    {
        var parts = new List<string>();
        if (stopped is { Skipped.Count: > 0 } s)
            parts.Add($"Stopped the {s.Kind.Title()} scripts before {List(s.Skipped)}");

        if (chain is not null)
        {
            var pending = chain.Pending;
            var waiting = chain.Waiting is { } w && pending.Count > 0 ? w : null;
            var started = chain.Started.ToList();
            if (chain.Waiting is { } running && waiting is null) started.Add(running); // last one: nothing waits for it

            if (started.Count > 0) parts.Add((parts.Count == 0 ? "Started " : "started ") + List(started));

            var couldNot = chain.Failed.Where(f => f != chain.StoppedAt).Select(f => f.Name).ToList();
            if (couldNot.Count > 0) parts.Add((parts.Count == 0 ? "Couldn't start " : "couldn't start ") + List(couldNot));

            if (chain.StoppedAt is { } stop)
                parts.Add(chain.Skipped.Count > 0 ? $"{stop.Name} failed, so {List(chain.Skipped)} didn't run" : $"{stop.Name} failed");
            else if (waiting is not null)
                parts.Add((parts.Count == 0 ? "Waiting for " : "waiting for ") + $"{waiting}, then {List(pending)}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    /// Title and text of a notification about what went wrong in a finished chain, or null when nothing did.
    /// </summary>
    /// <param name="label">What ran the scripts, e.g. "Grid lost" or "Manual run".</param>
    public static (string Title, string Body)? Problems(ScriptChain chain, string label)
    {
        if (chain.StoppedAt is { } stop)
        {
            var body = Capitalize(stop.Problem) +
                       (chain.Skipped.Count > 0 ? $", so {List(chain.Skipped)} didn't run." : ".");
            var others = chain.Failed.Where(f => f != stop).Select(f => f.Name).ToList();
            if (others.Count > 0) body += $" Couldn't start {List(others)} either.";
            return ($"{label}: {stop.Name} failed", body);
        }

        if (chain.Failed.Count == 0) return null;
        return ($"{label}: couldn't start {List(chain.Failed.Select(f => f.Name).ToList())}",
            string.Join(" ", chain.Failed.Select(f => $"{f.Name}: {f.Problem}.")));
    }

    public static string List(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{names[0]}, {names[1]} and {names.Count - 2} more",
    };

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
