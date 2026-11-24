using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Extensions.Options;
using SumitScraper.Contracts;

namespace SumitScraper.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string FilePath { get; set; } = string.Empty;
}

public sealed class JsonTransactionRepository : ITransactionRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly ILogger<JsonTransactionRepository> _logger;

    public JsonTransactionRepository(
        IOptions<StorageOptions> options,
        ILogger<JsonTransactionRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        var configuredPath = options.Value.FilePath;
        FilePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SumitScraper",
                "transactions.json")
            : Path.GetFullPath(configuredPath.Trim());
    }

    public string FilePath { get; }

    public async Task<IReadOnlyList<TransactionItem>> GetSavedAsync(CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(FilePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            return Array.Empty<TransactionItem>();
        }

        try
        {
            await using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            var items = await JsonSerializer.DeserializeAsync<List<TransactionItem>>(
                stream,
                SerializerOptions,
                cancellationToken);

            if (items is null || items.Count == 0)
            {
                return Array.Empty<TransactionItem>();
            }

            return items
                .Select(CoalesceNullFields)
                .ToArray();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                "Ignoring malformed transaction storage file at {FilePath} ({ExceptionType}).",
                FilePath,
                ex.GetType().Name);
            return Array.Empty<TransactionItem>();
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<TransactionItem> transactions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = FilePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, transactions, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private static TransactionItem CoalesceNullFields(TransactionItem item) =>
        item with
        {
            Currency = item.Currency ?? string.Empty,
            Description = item.Description ?? string.Empty
        };

    private void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(
                "Could not delete temporary storage file {TempPath} ({ExceptionType}).",
                tempPath,
                ex.GetType().Name);
        }
    }
}
