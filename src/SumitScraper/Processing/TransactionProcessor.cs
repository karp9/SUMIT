using System.Globalization;
using SumitScraper.Contracts;

namespace SumitScraper.Processing;

public sealed class TransactionProcessor : ITransactionProcessor
{
    private static readonly string[] SupportedDateFormats =
    [
        "yyyy-MM-dd",
        "dd/MM/yyyy",
        "dd/MM/yy",
        "dd.MM.yyyy",
        "dd.MM.yy",
        "d/M/yyyy",
        "d/M/yy",
        "d.M.yyyy",
        "d.M.yy"
    ];

    public IReadOnlyList<TransactionItem> Process(IReadOnlyList<RawTransaction> rawTransactions)
    {
        ArgumentNullException.ThrowIfNull(rawTransactions);

        if (rawTransactions.Count == 0)
        {
            return Array.Empty<TransactionItem>();
        }

        var items = new TransactionItem[rawTransactions.Count];
        for (var index = 0; index < rawTransactions.Count; index++)
        {
            var raw = rawTransactions[index]
                ?? throw new ArgumentNullException(nameof(rawTransactions), $"Null transaction at index {index}.");

            var date = ParseDate(raw.DateText, index);
            var signedAmount = raw.IsCredit ? Math.Abs(raw.Amount) : -Math.Abs(raw.Amount);
            var currency = NormalizeCurrency(raw.Currency);
            var maskedDescription = MaskDescription(raw.Description);

            items[index] = new TransactionItem(date, signedAmount, currency, maskedDescription);
        }

        return items
            .OrderByDescending(static item => item.Date)
            .ToArray();
    }

    public static string MaskDescription(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var trimmed = input.Trim();
        return string.Create(trimmed.Length, trimmed, static (destination, source) =>
        {
            var inWord = false;
            for (var index = 0; index < source.Length; index++)
            {
                var current = source[index];
                if (char.IsDigit(current))
                {
                    destination[index] = '*';
                    inWord = false;
                }
                else if (char.IsLetter(current))
                {
                    destination[index] = inWord ? '*' : current;
                    inWord = true;
                }
                else
                {
                    destination[index] = current;
                    inWord = false;
                }
            }
        });
    }

    internal static DateOnly ParseDate(string? dateText, int index) =>
        TryParseDate(dateText, out var parsed)
            ? parsed
            : throw new FormatException($"Unrecognized transaction date at index {index}.");

    internal static bool TryParseDate(string? dateText, out DateOnly date)
    {
        if (!string.IsNullOrWhiteSpace(dateText))
        {
            var trimmed = dateText.Trim();
            var datePrefix = trimmed.Split([' ', 'T'], 2, StringSplitOptions.RemoveEmptyEntries)[0];

            if (DateOnly.TryParseExact(
                    datePrefix,
                    SupportedDateFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out date))
            {
                return true;
            }

            if (trimmed.Contains('-', StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(
                    trimmed,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var timestamp))
            {
                date = DateOnly.FromDateTime(timestamp.Date);
                return true;
            }
        }

        date = default;
        return false;
    }

    public static string NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return "ILS";
        }

        var trimmed = currency.Trim();
        return trimmed switch
        {
            "₪" or "ש\"ח" or "ש״ח" or "שח" => "ILS",
            "$" => "USD",
            "€" => "EUR",
            "£" => "GBP",
            _ => trimmed.ToUpperInvariant()
        };
    }
}
