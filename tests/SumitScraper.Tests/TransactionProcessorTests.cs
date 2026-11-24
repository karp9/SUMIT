using SumitScraper.Collection;
using SumitScraper.Contracts;
using SumitScraper.Processing;

namespace SumitScraper.Tests;

public sealed class TransactionProcessorTests
{
    private readonly TransactionProcessor _processor = new();

    [Theory]
    [InlineData("שופרסל דיל סניף 142", "ש***** ד** ס*** ***")]
    [InlineData("NETFLIX.COM 849201", "N******.C** ******")]
    [InlineData("העברה בביט 050-0000000", "ה**** ב*** ***-*******")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void MaskDescription_MasksTrailingWordLettersAndAllDigits(string? input, string expected)
    {
        var actual = TransactionProcessor.MaskDescription(input);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Process_NormalizesIsraeliDatesSignsCurrenciesAndSortsNewestFirst()
    {
        IReadOnlyList<RawTransaction> raw =
        [
            new("05/03/2026", 129.90m, "₪", IsCredit: false, "שופרסל דיל סניף 142"),
            new("2026-03-18T14:22:10+02:00", 45.00m, "$", IsCredit: true, "AMAZON REFUND 9921"),
            new("11.03.2026", 19.99m, "€", IsCredit: false, "SPOTIFY AB"),
            new("02/03/26", 80.00m, "", IsCredit: false, "העברה בביט 050-0000000")
        ];

        var result = _processor.Process(raw);

        Assert.Equal(4, result.Count);

        Assert.Equal(new DateOnly(2026, 3, 18), result[0].Date);
        Assert.Equal(45.00m, result[0].Amount);
        Assert.Equal("USD", result[0].Currency);
        Assert.Equal("A***** R***** ****", result[0].Description);

        Assert.Equal(new DateOnly(2026, 3, 11), result[1].Date);
        Assert.Equal(-19.99m, result[1].Amount);
        Assert.Equal("EUR", result[1].Currency);
        Assert.Equal("S****** A*", result[1].Description);

        Assert.Equal(new DateOnly(2026, 3, 5), result[2].Date);
        Assert.Equal(-129.90m, result[2].Amount);
        Assert.Equal("ILS", result[2].Currency);
        Assert.Equal("ש***** ד** ס*** ***", result[2].Description);

        Assert.Equal(new DateOnly(2026, 3, 2), result[3].Date);
        Assert.Equal(-80.00m, result[3].Amount);
        Assert.Equal("ILS", result[3].Currency);
        Assert.Equal("ה**** ב*** ***-*******", result[3].Description);
    }

    [Theory]
    [InlineData("ש\"ח", "ILS")]
    [InlineData("ש״ח", "ILS")]
    [InlineData("₪", "ILS")]
    [InlineData("", "ILS")]
    [InlineData("$", "USD")]
    [InlineData("€", "EUR")]
    [InlineData("gbp", "GBP")]
    public void NormalizeCurrency_MapsKnownSymbolsAndUppercasesCodes(string input, string expected)
    {
        Assert.Equal(expected, TransactionProcessor.NormalizeCurrency(input));
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("12/25/2026")]
    [InlineData("")]
    public void Process_ThrowsFormatExceptionWhenDateIsInvalid(string invalidDate)
    {
        IReadOnlyList<RawTransaction> raw =
        [
            new(invalidDate, 10m, "ILS", IsCredit: false, "TEST")
        ];

        Assert.Throws<FormatException>(() => _processor.Process(raw));
    }

    [Theory]
    [InlineData("₪ 129.90", 129.90, "₪", false)]
    [InlineData("1,250.50 ש\"ח", 1250.50, "₪", false)]
    [InlineData("\u200E-45.00 $", 45.00, "$", true)]
    [InlineData("45.00- ₪", 45.00, "₪", true)]
    [InlineData("ש\"ח 45.00-", 45.00, "₪", true)]
    [InlineData("−19.99 €", 19.99, "€", true)]
    [InlineData("129.90 ₪ תשלום 1 מתוך 3", 129.90, "₪", false)]
    public void TryParseAmountAndCurrency_ExtractsInlineCurrencyAndCreditSign(
        string rawText,
        double expectedAmount,
        string expectedCurrency,
        bool expectedCredit)
    {
        var parsed = CalStatementParser.TryParseAmountAndCurrency(
            rawText,
            fallbackCurrency: null,
            out var amount,
            out var currency,
            out var isCredit);

        Assert.True(parsed);
        Assert.Equal((decimal)expectedAmount, amount);
        Assert.Equal(expectedCurrency, currency);
        Assert.Equal(expectedCredit, isCredit);
    }

    [Fact]
    public void ParseStatementRows_FiltersToTargetBillingPeriodAndOmitsCardType()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["Date", "Description", "Card Type", "Amount"],
            ["18/10/2026", "שופרסל דיל סניף 142", "Visa Gold 0000", "129.90 ₪"],
            ["04/10/2026", "AMAZON REFUND", "Cal Fix 0000", "-45.00 $"],
            ["29/09/2026", "PRIOR MONTH ITEM", "Visa Gold 0000", "88.00 ₪"]
        ];

        var extracted = CalStatementParser.ParseStatementRows(rows, new BillingPeriod(2026, 10));

        Assert.Equal(2, extracted.Count);

        Assert.Equal("2026-10-18", extracted[0].DateText);
        Assert.Equal(129.90m, extracted[0].Amount);
        Assert.Equal("₪", extracted[0].Currency);
        Assert.False(extracted[0].IsCredit);
        Assert.Equal("שופרסל דיל סניף 142", extracted[0].Description);

        Assert.Equal("2026-10-04", extracted[1].DateText);
        Assert.Equal(45.00m, extracted[1].Amount);
        Assert.Equal("$", extracted[1].Currency);
        Assert.True(extracted[1].IsCredit);
        Assert.Equal("AMAZON REFUND", extracted[1].Description);
    }

    [Fact]
    public void ParseStatementRows_SupportsSplitCurrencySpansAndDayMonthDates()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["18/10", "שופרסל שלי", "ויזה כאל", "129.90", "₪"]
        ];

        var extracted = CalStatementParser.ParseStatementRows(rows, new BillingPeriod(2026, 10));

        Assert.Single(extracted);
        Assert.Equal("2026-10-18", extracted[0].DateText);
        Assert.Equal(129.90m, extracted[0].Amount);
        Assert.Equal("₪", extracted[0].Currency);
        Assert.False(extracted[0].IsCredit);
    }

    [Fact]
    public void ExtractTransactionsFromJson_IgnoresSummaryWrapperAndExtractsNestedTransactions()
    {
        const string sampleCalJson =
            """
            {
              "result": {
                "bankAccounts": [
                  {
                    "debitDates": [
                      {
                        "date": "2026-10-02T00:00:00",
                        "description": "סיכום חיוב חודשי",
                        "amount": 4500.00,
                        "transactions": [
                          {
                            "trnPurchaseDate": "2026-10-21T00:00:00",
                            "merchantName": "שופרסל דיל סניף 142",
                            "debCrdAmt": 165.40,
                            "debCrdCurrencySymbol": "₪",
                            "trnAmt": 45.00,
                            "trnCurrencySymbol": "$",
                            "refundInd": false
                          },
                          {
                            "trnPurchaseDate": "2026-10-09T00:00:00",
                            "merchantName": "NETFLIX.COM",
                            "debCrdAmt": 0,
                            "debCrdCurrencySymbol": "₪",
                            "trnAmt": 15.99,
                            "trnCurrencySymbol": "$",
                            "trnTypeCode": "6"
                          }
                        ]
                      }
                    ],
                    "immidiateDebits": {
                      "debitDays": [
                        {
                          "transactions": [
                            {
                              "trnPurchaseDate": "2026-09-28T00:00:00",
                              "merchantName": "SEPTEMBER ITEM",
                              "debCrdAmt": 50.00,
                              "debCrdCurrencySymbol": "₪"
                            }
                          ]
                        }
                      ]
                    }
                  }
                ]
              }
            }
            """;

        var extracted = CalStatementParser.ExtractTransactionsFromJson(
            sampleCalJson,
            new BillingPeriod(2026, 10));

        Assert.Equal(2, extracted.Count);

        Assert.Equal("2026-10-21", extracted[0].DateText);
        Assert.Equal(165.40m, extracted[0].Amount);
        Assert.Equal("₪", extracted[0].Currency);
        Assert.False(extracted[0].IsCredit);
        Assert.Equal("שופרסל דיל סניף 142", extracted[0].Description);

        Assert.Equal("2026-10-09", extracted[1].DateText);
        Assert.Equal(15.99m, extracted[1].Amount);
        Assert.Equal("$", extracted[1].Currency);
        Assert.True(extracted[1].IsCredit);
        Assert.Equal("NETFLIX.COM", extracted[1].Description);
    }

    [Fact]
    public void ExtractTransactionsFromJson_SupportsSlashTimestampsAndMoneyObjects()
    {
        const string sampleJson =
            """
            {
              "data": {
                "items": [
                  {
                    "purchaseDate": "18/10/2026 14:30:00",
                    "businessName": "רמי לוי שיווק 12",
                    "chargedAmount": {
                      "value": 240.80,
                      "currencySymbol": "₪"
                    }
                  }
                ]
              }
            }
            """;

        var extracted = CalStatementParser.ExtractTransactionsFromJson(
            sampleJson,
            new BillingPeriod(2026, 10));

        var processed = _processor.Process(extracted);

        Assert.Single(processed);
        Assert.Equal(new DateOnly(2026, 10, 18), processed[0].Date);
        Assert.Equal(-240.80m, processed[0].Amount);
        Assert.Equal("ILS", processed[0].Currency);
        Assert.Equal("ר** ל** ש**** **", processed[0].Description);
    }

    [Fact]
    public void SelectAllJsonTransactions_PreservesFull90DayWindowAcrossMonths()
    {
        const string sample90DayJson =
            """
            {
              "result": {
                "transactions": [
                  {
                    "trnPurchaseDate": "2026-11-12T00:00:00",
                    "merchantName": "נובמבר עסקה",
                    "debCrdAmt": 80.00,
                    "debCrdCurrencySymbol": "₪"
                  },
                  {
                    "trnPurchaseDate": "2026-10-19T00:00:00",
                    "merchantName": "אוקטובר עסקה",
                    "debCrdAmt": 120.00,
                    "debCrdCurrencySymbol": "₪"
                  }
                ]
              }
            }
            """;

        IReadOnlyList<CapturedJsonPayload> snapshots =
        [
            new(1, "/Transactions/api/lastTransactions", sample90DayJson)
        ];

        var all90Days = CalStatementParser.SelectAllJsonTransactions(
            snapshots,
            new BillingPeriod(2026, 10),
            out var octoberMatched);

        Assert.Equal(2, all90Days.Count);
        Assert.Equal(1, octoberMatched);
        Assert.Contains(all90Days, item => item.DateText == "2026-11-12");
        Assert.Contains(all90Days, item => item.DateText == "2026-10-19");
    }

    [Fact]
    public void ParseStatementRows_SkipsTrailingInstallmentCellAndExtractsActualAmount()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["18/10/2026", "שופרסל דיל", "ויזה 4580", "129.90 ₪", "תשלום 1 מתוך 3"]
        ];

        var extracted = CalStatementParser.ParseStatementRows(rows, new BillingPeriod(2026, 10));

        Assert.Single(extracted);
        Assert.Equal(129.90m, extracted[0].Amount);
        Assert.Equal("₪", extracted[0].Currency);
    }

    [Fact]
    public void SelectAllJsonTransactions_DeduplicatesOverlappingEndpointsInSamePhase()
    {
        const string summaryEndpointJson =
            """
            {
              "transactions": [
                {
                  "trnPurchaseDate": "2026-10-19T00:00:00",
                  "merchantName": "אוקטובר עסקה",
                  "debCrdAmt": 120.00,
                  "debCrdCurrencySymbol": "₪"
                }
              ]
            }
            """;

        const string fullEndpointJson =
            """
            {
              "transactions": [
                {
                  "trnPurchaseDate": "2026-10-19T00:00:00",
                  "merchantName": "אוקטובר עסקה",
                  "debCrdAmt": 120.00,
                  "debCrdCurrencySymbol": "₪"
                },
                {
                  "trnPurchaseDate": "2026-10-12T00:00:00",
                  "merchantName": "סופר פארם",
                  "debCrdAmt": 64.50,
                  "debCrdCurrencySymbol": "₪"
                }
              ]
            }
            """;

        IReadOnlyList<CapturedJsonPayload> snapshots =
        [
            new(1, "/Transactions/api/summary", summaryEndpointJson),
            new(1, "/Transactions/api/all", fullEndpointJson)
        ];

        var merged = CalStatementParser.SelectAllJsonTransactions(
            snapshots,
            new BillingPeriod(2026, 10),
            out var octoberMatched);

        Assert.Equal(2, merged.Count);
        Assert.Equal(2, octoberMatched);
    }
}
