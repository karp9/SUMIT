using System.Globalization;

namespace SumitScraper.Contracts;

public readonly record struct BillingPeriod(int Year, int Month)
{
    public static BillingPeriod PreviousMonth(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var localNow = timeProvider.GetLocalNow();
        return localNow.Month == 1
            ? new BillingPeriod(localNow.Year - 1, 12)
            : new BillingPeriod(localNow.Year, localNow.Month - 1);
    }

    public static BillingPeriod CurrentMonth(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var localNow = timeProvider.GetLocalNow();
        return new BillingPeriod(localNow.Year, localNow.Month);
    }

    public string ToYearMonthString() =>
        string.Format(CultureInfo.InvariantCulture, "{0:D4}-{1:D2}", Year, Month);

    public static BillingPeriod ParseYearMonth(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !DateOnly.TryParseExact(
                value.Trim(),
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw new FormatException("Invalid billing period format.");
        }

        return new BillingPeriod(parsed.Year, parsed.Month);
    }

    public bool Contains(DateOnly date) => date.Year == Year && date.Month == Month;
}

public sealed record RawTransaction(
    string DateText,
    decimal Amount,
    string Currency,
    bool IsCredit,
    string Description);

public sealed record TransactionItem(
    DateOnly Date,
    decimal Amount,
    string Currency,
    string Description);

public enum CollectionPhase
{
    LaunchingBrowser,
    WaitingForUserLogin,
    LoadingStatement,
    ProcessingAndSaving,
    Completed
}

public static class CollectionFailureMessages
{
    public const string AlreadyRunning = "A collection run is already in progress.";
    public const string BrowserRequired = "Google Chrome or Microsoft Edge is required to run collection.";
    public const string EmptyStatement = "No transactions were returned from Visa Cal.";
}

public interface ITransactionCollector
{
    Task<IReadOnlyList<RawTransaction>> CollectAsync(
        BillingPeriod period,
        IProgress<CollectionPhase>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ITransactionProcessor
{
    IReadOnlyList<TransactionItem> Process(IReadOnlyList<RawTransaction> rawTransactions);
}

public interface ITransactionRepository
{
    string FilePath { get; }

    Task<IReadOnlyList<TransactionItem>> GetSavedAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<TransactionItem> transactions, CancellationToken cancellationToken = default);
}
