using System.Globalization;
using System.Text;
using System.Text.Json;
using SumitScraper.Contracts;
using SumitScraper.Processing;

namespace SumitScraper.Collection;

internal readonly record struct CapturedJsonPayload(int Phase, string EndpointKey, string Body);

internal readonly record struct CalStatementRow(
    string DateText,
    string Description,
    string AmountText,
    string? CurrencyText);

internal static class CalStatementParser
{
    private static readonly string[] WrapperChildArrayNames =
    [
        "transactions",
        "debitDates",
        "debitDays",
        "bankAccounts"
    ];

    private static readonly string[] JsonDatePropertyNames =
    [
        "trnPurchaseDate",
        "purchaseDate",
        "trnDate",
        "transDate",
        "transactionDate",
        "dealDate",
        "eventDate",
        "valueDate",
        "date",
        "debitDate"
    ];

    private static readonly string[] JsonDescriptionPropertyNames =
    [
        "merchantName",
        "merchantHebName",
        "merchantEngName",
        "merchantDescription",
        "merchantDesc",
        "businessName",
        "supplierName",
        "trnDescription",
        "transDescription",
        "transactionDescription",
        "dealDescription",
        "description",
        "desc",
        "payeeName"
    ];

    private static readonly (string AmountProp, string[] CurrencyProps)[] JsonPairedAmountProperties =
    [
        ("debCrdAmt", ["debCrdCurrencySymbol", "chargedCurrency", "currencySymbol", "trnCurrencySymbol"]),
        ("amtBeforeConvAndIndex", ["debCrdCurrencySymbol", "trnCurrencySymbol", "currencySymbol"]),
        ("billedAmount", ["billedCurrencySymbol", "billingCurrency", "currencySymbol", "currency"]),
        ("billingAmount", ["billingCurrency", "billedCurrencySymbol", "currencySymbol", "currency"]),
        ("chargedAmount", ["chargedCurrency", "debCrdCurrencySymbol", "currencySymbol", "currency"]),
        ("chargeAmount", ["chargedCurrency", "debCrdCurrencySymbol", "currencySymbol", "currency"]),
        ("nisAmount", ["debCrdCurrencySymbol", "currencySymbol", "currency"]),
        ("ilsAmount", ["debCrdCurrencySymbol", "currencySymbol", "currency"]),
        ("trnAmt", ["trnCurrencySymbol", "trnCurrencyIso", "currencySymbol", "currency"]),
        ("transAmount", ["trnCurrencySymbol", "currencySymbol", "currency"]),
        ("transactionAmount", ["trnCurrencySymbol", "currencySymbol", "currency"]),
        ("dealAmount", ["trnCurrencySymbol", "currencySymbol", "currency"]),
        ("originalAmount", ["trnCurrencySymbol", "currencySymbol", "currency"]),
        ("amount", ["currencySymbol", "currency", "currencyCode"]),
        ("sum", ["currencySymbol", "currency"])
    ];

    internal static IReadOnlyList<RawTransaction> SelectAllJsonTransactions(
        IReadOnlyList<CapturedJsonPayload> snapshots,
        BillingPeriod period,
        out int matchedPeriodCount)
    {
        matchedPeriodCount = 0;
        if (snapshots.Count == 0)
        {
            return Array.Empty<RawTransaction>();
        }

        var endpointSnapshots = new List<IReadOnlyList<RawTransaction>>();

        foreach (var phase in new[] { 1, 0 })
        {
            var phasePayloads = snapshots.Where(item => item.Phase == phase).ToArray();
            if (phasePayloads.Length == 0)
            {
                continue;
            }

            var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = phasePayloads.Length - 1; i >= 0; i--)
            {
                var payload = phasePayloads[i];
                var allInPayload = ExtractTransactionsFromJson(payload.Body, filterPeriod: null);
                if (allInPayload.Count == 0 || !seenEndpoints.Add(payload.EndpointKey))
                {
                    continue;
                }

                endpointSnapshots.Add(allInPayload);
            }

            if (endpointSnapshots.Count > 0)
            {
                break;
            }
        }

        if (endpointSnapshots.Count == 0)
        {
            return Array.Empty<RawTransaction>();
        }

        var combined = MergeDistinctSnapshots(endpointSnapshots);
        matchedPeriodCount = combined.Count(item =>
            TryParseCollectorDate(item.DateText, referencePeriod: null, out var parsed) && period.Contains(parsed));

        return combined;
    }

    internal static IReadOnlyList<RawTransaction> MergeDistinctSnapshots(
        IReadOnlyList<IReadOnlyList<RawTransaction>> snapshotLists)
    {
        if (snapshotLists.Count == 0)
        {
            return Array.Empty<RawTransaction>();
        }

        if (snapshotLists.Count == 1)
        {
            return snapshotLists[0];
        }

        var maxCountPerKey = new Dictionary<RawTransaction, int>();
        var orderedKeys = new List<RawTransaction>();

        foreach (var list in snapshotLists)
        {
            var countsInList = new Dictionary<RawTransaction, int>();
            foreach (var item in list)
            {
                countsInList.TryGetValue(item, out var existing);
                countsInList[item] = existing + 1;
            }

            foreach (var (item, count) in countsInList)
            {
                if (!maxCountPerKey.TryGetValue(item, out var previousMax))
                {
                    orderedKeys.Add(item);
                    maxCountPerKey[item] = count;
                }
                else if (count > previousMax)
                {
                    maxCountPerKey[item] = count;
                }
            }
        }

        var merged = new List<RawTransaction>();
        foreach (var key in orderedKeys)
        {
            var copies = maxCountPerKey[key];
            for (var i = 0; i < copies; i++)
            {
                merged.Add(key);
            }
        }

        return merged;
    }

    internal static IReadOnlyList<RawTransaction> ExtractTransactionsFromJson(
        string? jsonPayload,
        BillingPeriod? filterPeriod)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return Array.Empty<RawTransaction>();
        }

        try
        {
            using var document = JsonDocument.Parse(jsonPayload);
            var collected = new List<RawTransaction>();
            WalkJsonForTransactions(document.RootElement, filterPeriod, collected);
            return collected;
        }
        catch (JsonException)
        {
            return Array.Empty<RawTransaction>();
        }
    }

    internal static IReadOnlyList<RawTransaction> ParseStatementRows(
        IReadOnlyList<IReadOnlyList<string>> cellRows,
        BillingPeriod period) =>
        ParseStatementRows(cellRows, filterPeriod: period, referencePeriod: period);

    internal static IReadOnlyList<RawTransaction> ParseStatementRows(
        IReadOnlyList<IReadOnlyList<string>> cellRows,
        BillingPeriod? filterPeriod,
        BillingPeriod? referencePeriod)
    {
        ArgumentNullException.ThrowIfNull(cellRows);

        if (cellRows.Count == 0)
        {
            return Array.Empty<RawTransaction>();
        }

        var results = new List<RawTransaction>(cellRows.Count);
        foreach (var cells in cellRows)
        {
            if (!TryExtractStatementRow(cells, referencePeriod, out var row))
            {
                continue;
            }

            if (!TryParseCollectorDate(row.DateText, referencePeriod, out var parsedDate))
            {
                continue;
            }

            if (filterPeriod is { } target && !target.Contains(parsedDate))
            {
                continue;
            }

            if (!TryParseAmountAndCurrency(row.AmountText, row.CurrencyText, out var amount, out var currency, out var isCredit))
            {
                continue;
            }

            results.Add(new RawTransaction(
                parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                amount,
                currency,
                isCredit,
                row.Description));
        }

        return results;
    }

    internal static bool TryParseAmountAndCurrency(
        string? rawAmountText,
        string? fallbackCurrency,
        out decimal amount,
        out string currency,
        out bool isCredit)
    {
        amount = 0m;
        currency = fallbackCurrency?.Trim() ?? string.Empty;
        isCredit = false;

        if (string.IsNullOrWhiteSpace(rawAmountText))
        {
            return false;
        }

        var cleaned = StripFormattingCharacters(rawAmountText);
        if (cleaned.Length == 0 ||
            !TryExtractFirstNumberToken(cleaned, out var numberToken, out var tokenStart, out var tokenEnd) ||
            !decimal.TryParse(numberToken, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
        {
            return false;
        }

        isCredit =
            HasAdjacentNegativeSign(cleaned, tokenStart, tokenEnd) ||
            cleaned.Contains("זיכוי", StringComparison.Ordinal) ||
            cleaned.Contains("זכות", StringComparison.Ordinal) ||
            cleaned.Contains("credit", StringComparison.OrdinalIgnoreCase);

        var detectedCurrency = ExtractCurrencyToken(cleaned);
        if (detectedCurrency.Length > 0)
        {
            currency = detectedCurrency;
        }

        return true;
    }

    internal static bool TryParseCollectorDate(
        string candidate,
        BillingPeriod? referencePeriod,
        out DateOnly date)
    {
        if (TransactionProcessor.TryParseDate(candidate, out date))
        {
            return true;
        }

        var trimmed = StripFormattingCharacters(candidate);
        if (DateOnly.TryParseExact(
                trimmed,
                ["dd/MM", "d/M", "dd.MM", "d.M"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dayMonth))
        {
            var baseYear = referencePeriod?.Year ?? DateTimeOffset.Now.Year;
            var baseMonth = referencePeriod?.Month ?? DateTimeOffset.Now.Month;
            var inferredYear = dayMonth.Month > baseMonth + 1 ? baseYear - 1 : baseYear;

            if (dayMonth.Day <= DateTime.DaysInMonth(inferredYear, dayMonth.Month))
            {
                date = new DateOnly(inferredYear, dayMonth.Month, dayMonth.Day);
                return true;
            }
        }

        date = default;
        return false;
    }

    private static void WalkJsonForTransactions(
        JsonElement element,
        BillingPeriod? filterPeriod,
        List<RawTransaction> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryMapJsonTransactionObject(element, filterPeriod, out var transaction))
                {
                    output.Add(transaction);
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        WalkJsonForTransactions(property.Value, filterPeriod, output);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        WalkJsonForTransactions(item, filterPeriod, output);
                    }
                }

                break;
        }
    }

    private static bool TryMapJsonTransactionObject(
        JsonElement obj,
        BillingPeriod? filterPeriod,
        out RawTransaction transaction)
    {
        transaction = default!;

        foreach (var childArrayName in WrapperChildArrayNames)
        {
            if (TryGetPropertyIgnoreCase(obj, childArrayName, out var child) &&
                child.ValueKind == JsonValueKind.Array)
            {
                return false;
            }
        }

        var description = FindJsonStringProperty(obj, JsonDescriptionPropertyNames);
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        var dateRaw = FindJsonStringProperty(obj, JsonDatePropertyNames);
        if (string.IsNullOrWhiteSpace(dateRaw) ||
            !TryParseCollectorDate(dateRaw, filterPeriod, out var parsedDate))
        {
            return false;
        }

        if (filterPeriod is { } target && !target.Contains(parsedDate))
        {
            return false;
        }

        if (!TryExtractJsonPairedAmount(obj, out var signedAmount, out var currency, out var explicitCredit))
        {
            return false;
        }

        var isCredit = explicitCredit || signedAmount < 0m || IsJsonCreditTransaction(obj);
        var normalizedDateText = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        transaction = new RawTransaction(
            normalizedDateText,
            Math.Abs(signedAmount),
            currency,
            isCredit,
            description);

        return true;
    }

    private static bool TryExtractJsonPairedAmount(
        JsonElement obj,
        out decimal signedAmount,
        out string currency,
        out bool explicitCredit)
    {
        signedAmount = 0m;
        currency = string.Empty;
        explicitCredit = false;

        var hasZeroCandidate = false;
        var zeroCurrency = string.Empty;

        foreach (var (amountProp, currencyProps) in JsonPairedAmountProperties)
        {
            if (!TryGetPropertyIgnoreCase(obj, amountProp, out var amountElement) ||
                !TryReadJsonDecimal(amountElement, out var parsedDecimal, out var inlineCurrency, out var inlineCredit))
            {
                continue;
            }

            var pairedCurrency = FindJsonStringProperty(obj, currencyProps) ?? inlineCurrency;
            if (parsedDecimal == 0m)
            {
                hasZeroCandidate = true;
                zeroCurrency = pairedCurrency;
                continue;
            }

            signedAmount = parsedDecimal;
            currency = pairedCurrency;
            explicitCredit = inlineCredit;
            return true;
        }

        if (hasZeroCandidate)
        {
            signedAmount = 0m;
            currency = zeroCurrency;
            return true;
        }

        return false;
    }

    private static bool TryReadJsonDecimal(
        JsonElement element,
        out decimal value,
        out string inlineCurrency,
        out bool inlineCredit)
    {
        value = 0m;
        inlineCurrency = string.Empty;
        inlineCredit = false;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String &&
            TryParseAmountAndCurrency(element.GetString(), null, out var parsedAbs, out inlineCurrency, out inlineCredit))
        {
            value = inlineCredit ? -parsedAbs : parsedAbs;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            inlineCurrency = FindJsonStringProperty(element, ["currencySymbol", "symbol", "currency", "currencyCode", "code"]) ?? string.Empty;
            foreach (var nestedKey in new[] { "value", "amount", "sum" })
            {
                if (TryGetPropertyIgnoreCase(element, nestedKey, out var nestedValue) &&
                    TryReadJsonDecimal(nestedValue, out value, out var nestedCurrency, out inlineCredit))
                {
                    if (inlineCurrency.Length == 0)
                    {
                        inlineCurrency = nestedCurrency;
                    }

                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsJsonCreditTransaction(JsonElement obj)
    {
        foreach (var flagName in new[] { "refundInd", "isCredit", "isRefund", "creditInd" })
        {
            if (!TryGetPropertyIgnoreCase(obj, flagName, out var flag))
            {
                continue;
            }

            if (flag.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (flag.ValueKind == JsonValueKind.String &&
                flag.GetString() is { } text &&
                (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(text, "Y", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(text, "1", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        if (FindJsonStringProperty(obj, ["trnTypeCode"]) is { } typeCode &&
            string.Equals(typeCode.Trim(), "6", StringComparison.Ordinal))
        {
            return true;
        }

        if (FindJsonStringProperty(obj, ["trnType", "transactionType", "debitCreditInd"]) is { } trnType &&
            (trnType.Contains("זיכוי", StringComparison.Ordinal) ||
             trnType.Contains("זכות", StringComparison.Ordinal) ||
             trnType.Contains("credit", StringComparison.OrdinalIgnoreCase) ||
             trnType.Contains("refund", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static string? FindJsonStringProperty(JsonElement obj, IReadOnlyList<string> candidateNames)
    {
        foreach (var name in candidateNames)
        {
            if (TryGetPropertyIgnoreCase(obj, name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is { } str &&
                !string.IsNullOrWhiteSpace(str))
            {
                return NormalizeCellText(str);
            }
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryExtractFirstNumberToken(
        string text,
        out string numberToken,
        out int startIndex,
        out int endIndex)
    {
        numberToken = string.Empty;
        startIndex = -1;
        endIndex = -1;

        var builder = new StringBuilder(16);
        var hasDecimalPoint = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsAsciiDigit(ch))
            {
                if (startIndex < 0)
                {
                    startIndex = i;
                }

                builder.Append(ch);
                endIndex = i;
            }
            else if (ch == ',' && startIndex >= 0 && !hasDecimalPoint)
            {
                endIndex = i;
            }
            else if (ch == '.' && startIndex >= 0 && !hasDecimalPoint)
            {
                builder.Append('.');
                hasDecimalPoint = true;
                endIndex = i;
            }
            else if (startIndex >= 0)
            {
                break;
            }
        }

        if (builder.Length == 0)
        {
            return false;
        }

        numberToken = builder.ToString();
        return true;
    }

    private static bool HasAdjacentNegativeSign(string text, int startIndex, int endIndex)
    {
        for (var i = startIndex - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch) || ch is '₪' or '$' or '€' or '£')
            {
                continue;
            }

            if (ch is '-' or '−' or '(')
            {
                return true;
            }

            break;
        }

        for (var i = endIndex + 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch) || ch is '₪' or '$' or '€' or '£')
            {
                continue;
            }

            return ch is '-' or '−' or ')';
        }

        return false;
    }

    private static bool TryExtractStatementRow(
        IReadOnlyList<string> cells,
        BillingPeriod? referencePeriod,
        out CalStatementRow statementRow)
    {
        statementRow = default;
        if (cells is null || cells.Count < 3)
        {
            return false;
        }

        var nonEmpty = cells
            .Select(static cell => NormalizeCellText(cell))
            .Where(static cell => cell.Length > 0)
            .ToArray();

        if (nonEmpty.Length < 3)
        {
            return false;
        }

        var dateIndex = -1;
        for (var i = 0; i < Math.Min(3, nonEmpty.Length); i++)
        {
            if (TryParseCollectorDate(nonEmpty[i], referencePeriod, out _))
            {
                dateIndex = i;
                break;
            }
        }

        if (dateIndex < 0)
        {
            return false;
        }

        var remaining = nonEmpty.Skip(dateIndex + 1).ToArray();
        if (remaining.Length < 2)
        {
            return false;
        }

        var amountIndex = -1;
        for (var i = remaining.Length - 1; i >= 1; i--)
        {
            if (IsStatementAmountCell(remaining, i))
            {
                amountIndex = i;
                break;
            }
        }

        if (amountIndex < 1)
        {
            return false;
        }

        var adjacentCurrency = FindAdjacentCurrencyToken(remaining, amountIndex);

        statementRow = new CalStatementRow(
            nonEmpty[dateIndex],
            remaining[0],
            remaining[amountIndex],
            adjacentCurrency.Length > 0 ? adjacentCurrency : null);

        return true;
    }

    private static bool IsStatementAmountCell(string[] remaining, int index)
    {
        var candidate = remaining[index];
        if (!TryParseAmountAndCurrency(candidate, null, out _, out var inlineCurrency, out _))
        {
            return false;
        }

        return inlineCurrency.Length > 0 ||
               FindAdjacentCurrencyToken(remaining, index).Length > 0 ||
               candidate.Contains('.', StringComparison.Ordinal) ||
               !candidate.Any(char.IsLetter);
    }

    private static string FindAdjacentCurrencyToken(string[] remaining, int amountIndex)
    {
        var after = amountIndex + 1 < remaining.Length
            ? ExtractCurrencyToken(remaining[amountIndex + 1])
            : string.Empty;

        if (after.Length > 0)
        {
            return after;
        }

        return amountIndex - 1 >= 1 && remaining[amountIndex - 1].Length <= 5
            ? ExtractCurrencyToken(remaining[amountIndex - 1])
            : string.Empty;
    }

    private static string NormalizeCellText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var stripped = StripFormattingCharacters(text);
        var parts = stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    private static string StripFormattingCharacters(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch == '\u00A0')
            {
                builder.Append(' ');
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.Format)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Trim();
    }

    private static string ExtractCurrencyToken(string cleaned)
    {
        if (cleaned.Contains('₪', StringComparison.Ordinal) ||
            cleaned.Contains("ש\"ח", StringComparison.Ordinal) ||
            cleaned.Contains("ש״ח", StringComparison.Ordinal) ||
            cleaned.Contains("ILS", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("NIS", StringComparison.OrdinalIgnoreCase))
        {
            return "₪";
        }

        if (cleaned.Contains('$', StringComparison.Ordinal) ||
            cleaned.Contains("USD", StringComparison.OrdinalIgnoreCase))
        {
            return "$";
        }

        if (cleaned.Contains('€', StringComparison.Ordinal) ||
            cleaned.Contains("EUR", StringComparison.OrdinalIgnoreCase))
        {
            return "€";
        }

        if (cleaned.Contains('£', StringComparison.Ordinal) ||
            cleaned.Contains("GBP", StringComparison.OrdinalIgnoreCase))
        {
            return "£";
        }

        return string.Empty;
    }
}
