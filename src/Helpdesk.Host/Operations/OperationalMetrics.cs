using System.Diagnostics.Metrics;
using Hangfire;
using Hangfire.Storage;
using Helpdesk.SharedKernel.Operations;

namespace Helpdesk.Host.Operations;

/// <summary>
/// Samples every <see cref="IOperationalSignal"/> (and the job signals below) every 30 seconds and exposes the latest value as an observable gauge on the
/// <c>Helpdesk.Operations</c> meter, which OpenTelemetry exports when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (SRE-005, decision D4: job health is a metric, not readiness).
/// A failed sample keeps the previous value and is logged by type only.
/// </summary>
internal sealed partial class OperationalMetrics : BackgroundService
{
    public const string MeterName = "Helpdesk.Operations";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly Meter _meter = new(MeterName);
    private readonly List<IOperationalSignal> _signals;
    private readonly Dictionary<string, double> _latest = [];
    private readonly ILogger<OperationalMetrics> _log;

    public OperationalMetrics(IEnumerable<IOperationalSignal> signals, ILogger<OperationalMetrics> log)
    {
        _signals = [.. signals];
        _log = log;
        foreach (var signal in _signals)
        {
            var name = signal.Name;
            _meter.CreateObservableGauge(name, () => Latest(name), signal.Unit, signal.Description);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Operational signal {Signal} could not be read ({ErrorType})")]
    private static partial void SignalUnreadable(ILogger logger, string signal, string errorType);

    private double Latest(string name)
    {
        lock (_latest)
        {
            return _latest.GetValueOrDefault(name);
        }
    }

    public async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var signal in _signals)
        {
            try
            {
                if (await signal.ReadAsync(cancellationToken) is { } value)
                {
                    lock (_latest)
                    {
                        _latest[signal.Name] = value;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SignalUnreadable(_log, signal.Name, ex.GetType().Name);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await SampleOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}

/// <summary>Hangfire's own view: failed jobs, and how long ago each recurring job last ran (a missed minute is lag, a stopped server is a growing lag).</summary>
internal sealed class FailedJobsSignal : IOperationalSignal
{
    public string Name => "helpdesk_jobs_failed";

    public string Description => "Background jobs currently in the Failed state.";

    public string Unit => "{job}";

    public Task<double?> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<double?>(JobStorage.Current.GetMonitoringApi().FailedCount());
}

internal sealed class RecurringJobLagSignal(string jobId, string metricSuffix, TimeProvider clock) : IOperationalSignal
{
    public string Name => $"helpdesk_job_{metricSuffix}_lag_seconds";

    public string Description => $"Seconds since the {jobId} recurring job last started.";

    public string Unit => "s";

    public Task<double?> ReadAsync(CancellationToken cancellationToken)
    {
        using var connection = JobStorage.Current.GetConnection();
        var job = connection.GetRecurringJobs().FirstOrDefault(j => j.Id == jobId);
        double? lag = job?.LastExecution is { } last ? Math.Max(0, (clock.GetUtcNow() - new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc))).TotalSeconds) : null;
        return Task.FromResult(lag);
    }
}
