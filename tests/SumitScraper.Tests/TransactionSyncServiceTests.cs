using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SumitScraper.Contracts;
using SumitScraper.Orchestration;
using SumitScraper.Processing;
using SumitScraper.Storage;

namespace SumitScraper.Tests;

public sealed class TransactionSyncServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "SumitScraperSyncTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PreviousMonth_RollsJanuaryBackToPriorDecember()
    {
        var januaryProvider = new FixedTimeProvider(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.FromHours(2)));

        var previous = BillingPeriod.PreviousMonth(januaryProvider);
        var current = BillingPeriod.CurrentMonth(januaryProvider);

        Assert.Equal(new BillingPeriod(2025, 12), previous);
        Assert.Equal("2025-12", previous.ToYearMonthString());
        Assert.Equal(previous, BillingPeriod.ParseYearMonth("2025-12"));
        Assert.Equal(new BillingPeriod(2026, 1), current);
    }

    [Fact]
    public async Task RunAsync_PreservesPreviouslySavedTransactionsWhenCollectorFails()
    {
        var repository = CreateRepository();
        IReadOnlyList<TransactionItem> existing =
        [
            new(new DateOnly(2026, 9, 10), -99.00m, "ILS", "ק*** ע***")
        ];
        await repository.SaveAsync(existing);

        var collector = new StubCollector((_, _) => throw new TimeoutException("Simulated timeout."));
        var service = CreateSyncService(collector, repository);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.RunAsync(new BillingPeriod(2026, 10)));

        var afterFailure = await repository.GetSavedAsync();
        Assert.Equal(existing, afterFailure);
    }

    [Fact]
    public async Task RunAsync_PreservesPreviouslySavedTransactionsWhenCancelled()
    {
        var repository = CreateRepository();
        IReadOnlyList<TransactionItem> existing =
        [
            new(new DateOnly(2026, 9, 10), -42.50m, "ILS", "מ*****")
        ];
        await repository.SaveAsync(existing);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var collector = new StubCollector((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<RawTransaction>>([]);
        });
        var service = CreateSyncService(collector, repository);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(new BillingPeriod(2026, 10), cancellationToken: cts.Token));

        var afterCancel = await repository.GetSavedAsync();
        Assert.Equal(existing, afterCancel);
    }

    [Fact]
    public async Task RunAsync_PreservesPreviouslySavedTransactionsWhenCollectorReturnsEmpty()
    {
        var repository = CreateRepository();
        IReadOnlyList<TransactionItem> existing =
        [
            new(new DateOnly(2026, 9, 10), -42.50m, "ILS", "מ*****")
        ];
        await repository.SaveAsync(existing);

        var collector = new StubCollector((_, _) => Task.FromResult<IReadOnlyList<RawTransaction>>([]));
        var service = CreateSyncService(collector, repository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunAsync(new BillingPeriod(2026, 10)));
        Assert.Equal(CollectionFailureMessages.EmptyStatement, ex.Message);

        var afterEmpty = await repository.GetSavedAsync();
        Assert.Equal(existing, afterEmpty);
    }

    [Fact]
    public async Task RunAsync_RejectsConcurrentExecutionAndRecoversAfterCompletion()
    {
        var repository = CreateRepository();
        var collectorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCollector = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var collector = new StubCollector(async (_, _) =>
        {
            collectorEntered.TrySetResult();
            await releaseCollector.Task;
            return
            [
                new RawTransaction("15/10/2026", 60.00m, "₪", IsCredit: false, "שופרסל שלי")
            ];
        });
        var service = CreateSyncService(collector, repository);

        var firstRunTask = service.RunAsync(new BillingPeriod(2026, 10));
        await collectorEntered.Task;

        var concurrentException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunAsync(new BillingPeriod(2026, 10)));
        Assert.Equal(CollectionFailureMessages.AlreadyRunning, concurrentException.Message);

        releaseCollector.TrySetResult();
        var firstResult = await firstRunTask;
        Assert.Single(firstResult);

        var secondResult = await service.RunAsync(new BillingPeriod(2026, 10));
        Assert.Single(secondResult);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private JsonTransactionRepository CreateRepository() =>
        new(
            Options.Create(new StorageOptions { FilePath = Path.Combine(_tempDirectory, "transactions.json") }),
            NullLogger<JsonTransactionRepository>.Instance);

    private static TransactionSyncService CreateSyncService(
        ITransactionCollector collector,
        ITransactionRepository repository) =>
        new(
            collector,
            new TransactionProcessor(),
            repository,
            new FixedTimeProvider(new DateTimeOffset(2026, 11, 23, 14, 0, 0, TimeSpan.FromHours(2))),
            NullLogger<TransactionSyncService>.Instance);

    private sealed class StubCollector(
        Func<BillingPeriod, CancellationToken, Task<IReadOnlyList<RawTransaction>>> handler)
        : ITransactionCollector
    {
        public Task<IReadOnlyList<RawTransaction>> CollectAsync(
            BillingPeriod period,
            IProgress<CollectionPhase>? progress = null,
            CancellationToken cancellationToken = default) =>
            handler(period, cancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset localNow) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone =>
            TimeZoneInfo.CreateCustomTimeZone("TestTz", localNow.Offset, "TestTz", "TestTz");

        public override DateTimeOffset GetUtcNow() => localNow.ToUniversalTime();
    }
}
