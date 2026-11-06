using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Services.Azure;

namespace AccessReviewPortal.Services.Scanning;

public sealed class ScanJobState
{
    private readonly List<ScanLogEntry> _log = [];

    public bool IsRunning { get; set; }
    public string Label { get; set; } = "";
    public ScanTrigger Trigger { get; set; }
    public string? StartedByUpn { get; set; }
    public int Percent { get; set; }
    public string Step { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int? SnapshotId { get; set; }
    public string? Error { get; set; }
    internal TaskCompletionSource<int?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<ScanLogEntry> Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToList();
            }
        }
    }

    public void AddLog(ScanLogEntry entry)
    {
        lock (_log)
        {
            _log.Add(entry);
            if (_log.Count > 300) _log.RemoveAt(0);
        }
    }
}

/// <summary>Runs one scan at a time in the background and publishes progress to the UI.</summary>
public sealed class ScanJobManager(IServiceScopeFactory scopeFactory, ILogger<ScanJobManager> logger)
{
    private readonly object _sync = new();

    public ScanJobState State { get; private set; } = new();

    public event Action? Changed;

    public bool TryStart(string label, ScanTrigger trigger = ScanTrigger.Manual, string? startedByUpn = null) =>
        Start(label, trigger, startedByUpn) is not null;

    /// <summary>Starts a scan and waits for it (scheduled runs). Returns null when another scan is already running.</summary>
    public async Task<int?> RunAndWaitAsync(string label, ScanTrigger trigger, string? startedByUpn, CancellationToken ct)
    {
        var state = Start(label, trigger, startedByUpn);
        if (state is null) return null;
        var snapshotId = await state.Completion.Task.WaitAsync(ct);
        if (snapshotId is null)
        {
            throw new InvalidOperationException(state.Error ?? "The scan failed.");
        }
        return snapshotId;
    }

    private ScanJobState? Start(string label, ScanTrigger trigger, string? startedByUpn)
    {
        ScanJobState state;
        lock (_sync)
        {
            if (State.IsRunning) return null;
            state = new ScanJobState
            {
                IsRunning = true, Label = label, Trigger = trigger, StartedByUpn = startedByUpn, StartedUtc = DateTime.UtcNow, Step = "Starting"
            };
            State = state;
        }
        Notify();
        _ = Task.Run(() => RunAsync(state));
        return state;
    }

    private async Task RunAsync(ScanJobState state)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var scanner = scope.ServiceProvider.GetRequiredService<AccessReviewScannerService>();
            state.SnapshotId = await scanner.RunAsync(state.Label, state.Trigger, state.StartedByUpn,
                p =>
                {
                    state.Percent = p.Percent;
                    state.Step = p.Step;
                    Notify();
                },
                entry =>
                {
                    state.AddLog(entry);
                    Notify();
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scan failed");
            state.Error = ex.ShortMessage();
        }
        finally
        {
            state.IsRunning = false;
            state.FinishedUtc = DateTime.UtcNow;
            state.Completion.TrySetResult(state.Error is null ? state.SnapshotId : null);
            Notify();
        }
    }

    private void Notify()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Progress listener failed");
        }
    }
}
