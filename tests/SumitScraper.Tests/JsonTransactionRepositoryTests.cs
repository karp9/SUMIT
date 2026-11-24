using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SumitScraper.Contracts;
using SumitScraper.Storage;

namespace SumitScraper.Tests;

public sealed class JsonTransactionRepositoryTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "SumitScraperTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetSavedAsync_ReturnsEmptyWhenFileDoesNotExist()
    {
        var repository = CreateRepository(Path.Combine(_tempDirectory, "missing", "transactions.json"));

        var items = await repository.GetSavedAsync();

        Assert.Empty(items);
    }

    [Fact]
    public async Task SaveAsyncAndGetSavedAsync_RoundTripsMaskedHebrewWithoutEscapingOrLeftoverTempFile()
    {
        var filePath = Path.Combine(_tempDirectory, "transactions.json");
        var repository = CreateRepository(filePath);
        IReadOnlyList<TransactionItem> expected =
        [
            new(new DateOnly(2026, 10, 24), -129.90m, "ILS", "ש***** ד** ס*** ***"),
            new(new DateOnly(2026, 10, 12), 50.00m, "USD", "A***** R*****")
        ];

        await repository.SaveAsync(expected);

        Assert.True(File.Exists(filePath));
        Assert.False(File.Exists(filePath + ".tmp"));

        var rawJson = await File.ReadAllTextAsync(filePath);
        Assert.Contains("ש***** ד** ס*** ***", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u05E9", rawJson, StringComparison.OrdinalIgnoreCase);

        var loaded = await repository.GetSavedAsync();
        Assert.Equal(expected, loaded);
    }

    [Fact]
    public async Task GetSavedAsync_ReturnsEmptyWithoutThrowingWhenJsonIsMalformed()
    {
        Directory.CreateDirectory(_tempDirectory);
        var filePath = Path.Combine(_tempDirectory, "transactions.json");
        await File.WriteAllTextAsync(filePath, "{ invalid json [");
        var repository = CreateRepository(filePath);

        var loaded = await repository.GetSavedAsync();

        Assert.Empty(loaded);
    }

    [Fact]
    public void Constructor_UsesLocalApplicationDataWhenConfiguredPathIsBlank()
    {
        var repository = CreateRepository("   ");
        var expectedSuffix = Path.Combine("SumitScraper", "transactions.json");

        Assert.EndsWith(expectedSuffix, repository.FilePath, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static JsonTransactionRepository CreateRepository(string filePath) =>
        new(
            Options.Create(new StorageOptions { FilePath = filePath }),
            NullLogger<JsonTransactionRepository>.Instance);
}
