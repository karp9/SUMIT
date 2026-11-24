using SumitScraper.Contracts;

namespace SumitScraper.Orchestration;

public sealed class TransactionSyncService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ITransactionCollector _collector;
    private readonly ITransactionProcessor _processor;
    private readonly ITransactionRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TransactionSyncService> _logger;

    public TransactionSyncService(
        ITransactionCollector collector,
        ITransactionProcessor processor,
        ITransactionRepository repository,
        TimeProvider timeProvider,
        ILogger<TransactionSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _collector = collector;
        _processor = processor;
        _repository = repository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public bool IsRunning => _gate.CurrentCount == 0;

    public IReadOnlyList<(string Label, BillingPeriod Period)> GetAvailablePeriods() =>
    [
        ("Previous Month", BillingPeriod.PreviousMonth(_timeProvider)),
        ("Current Month", BillingPeriod.CurrentMonth(_timeProvider))
    ];

    public async Task<IReadOnlyList<TransactionItem>> RunAsync(
        BillingPeriod period,
        IProgress<CollectionPhase>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, CancellationToken.None))
        {
            throw new InvalidOperationException(CollectionFailureMessages.AlreadyRunning);
        }

        var startedAt = _timeProvider.GetTimestamp();
        try
        {
            progress?.Report(CollectionPhase.LaunchingBrowser);
            _logger.LogInformation(
                "Starting collection for period {BillingPeriod}.",
                period.ToYearMonthString());

            var rawTransactions = await _collector.CollectAsync(period, progress, cancellationToken);

            progress?.Report(CollectionPhase.ProcessingAndSaving);
            var processedTransactions = _processor.Process(rawTransactions);
            if (processedTransactions.Count == 0)
            {
                throw new InvalidOperationException(CollectionFailureMessages.EmptyStatement);
            }

            await _repository.SaveAsync(processedTransactions, cancellationToken);

            progress?.Report(CollectionPhase.Completed);
            var elapsedMs = _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Completed collection for period {BillingPeriod} with {ItemCount} items in {ElapsedMs:F0} ms.",
                period.ToYearMonthString(),
                processedTransactions.Count,
                elapsedMs);

            return processedTransactions;
        }
        catch (OperationCanceledException)
        {
            var elapsedMs = _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            _logger.LogInformation(
                "Collection cancelled for period {BillingPeriod} after {ElapsedMs:F0} ms.",
                period.ToYearMonthString(),
                elapsedMs);
            throw;
        }
        catch (Exception ex)
        {
            var elapsedMs = _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
            _logger.LogWarning(
                "Collection failed for period {BillingPeriod} after {ElapsedMs:F0} ms ({ExceptionType}).",
                period.ToYearMonthString(),
                elapsedMs,
                ex.GetType().Name);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
