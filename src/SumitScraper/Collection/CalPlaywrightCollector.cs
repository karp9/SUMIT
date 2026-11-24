using System.Collections.Concurrent;
using Microsoft.Playwright;
using SumitScraper.Contracts;

namespace SumitScraper.Collection;

public sealed class CalPlaywrightCollector : ITransactionCollector
{
    private const string PortalUrl = "https://digital-web.cal-online.co.il/";
    private const string PortalHost = "digital-web.cal-online.co.il";
    private const string CalDomainSuffix = "cal-online.co.il";
    private const string LandingHost = "www.cal-online.co.il";
    private const string AuthHost = "connect.cal-online.co.il";
    private const int InteractiveLoginTimeoutMs = 180_000;
    private const int InitialNavigationTimeoutMs = 30_000;
    private const int PostLoginStepTimeoutMs = 90_000;
    private const int ViewportWidth = 1180;
    private const int ViewportHeight = 740;

    private static readonly string[] BrowserChannels = ["chrome", "msedge"];

    private static readonly string[] PopupWindowArgs =
    [
        "--window-size=1200,820",
        "--window-position=100,60"
    ];

    private static readonly string[] LandingLoginSelectors =
    [
        "#ccLoginDesktopBtn",
        "a[href*='digital-web.cal-online.co.il']",
        "button:has-text('כניסה לחשבון')",
        "a:has-text('כניסה לחשבון')",
        "a:has-text('Login')"
    ];

    private static readonly string[] ModalContainerSelectors =
    [
        "mat-dialog-container",
        ".mat-mdc-dialog-container",
        "[role='dialog']",
        "[role='alertdialog']",
        ".modal.show",
        ".modal-dialog"
    ];

    private static readonly string[] ModalDismissButtonSelectors =
    [
        "button[aria-label*='סגור']",
        "button[aria-label*='close' i]",
        "[role='button'][aria-label*='סגור']",
        "[role='button'][aria-label*='close' i]",
        "[mat-dialog-close]",
        "[data-dismiss='modal']",
        "button[class*='close' i]",
        ".close-btn",
        ".dialog-close",
        "button:has-text('סגור')",
        "button:has-text('סגירה')",
        "button:has-text('דלג')",
        "button:has-text('לא עכשיו')",
        "button:has-text('לא תודה')",
        "button:has-text('אולי מאוחר יותר')",
        "button:has-text('הבנתי')",
        "a:has-text('סגור')",
        "a:has-text('דלג')",
        "a:has-text('לא עכשיו')",
        "button:has-text('Close')",
        "button:has-text('Dismiss')",
        "button:has-text('Skip')",
        "button:has-text('Got it')"
    ];

    private static readonly string[] SeeAllTransactionsSelectors =
    [
        "a:has-text('See all transactions')",
        "button:has-text('See all transactions')",
        "[role='button']:has-text('See all transactions')",
        "[role='link']:has-text('See all transactions')",
        "a:has-text('לכל העסקאות')",
        "button:has-text('לכל העסקאות')",
        "[role='button']:has-text('לכל העסקאות')",
        "[role='link']:has-text('לכל העסקאות')",
        "a:has-text('לצפייה בכל העסקאות')",
        "button:has-text('לצפייה בכל העסקאות')",
        "a:has-text('צפייה בכל העסקאות')",
        "button:has-text('צפייה בכל העסקאות')",
        "a:has-text('כל העסקאות')",
        "button:has-text('כל העסקאות')",
        "span:has-text('לכל העסקאות')",
        "span:has-text('See all transactions')"
    ];

    private readonly ILogger<CalPlaywrightCollector> _logger;

    public CalPlaywrightCollector(ILogger<CalPlaywrightCollector> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<IReadOnlyList<RawTransaction>> CollectAsync(
        BillingPeriod period,
        IProgress<CollectionPhase>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await LaunchBrowserAsync(playwright);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
            AcceptDownloads = false
        });

        var capturedResponses = new ConcurrentQueue<CapturedJsonPayload>();
        var inFlightResponses = new ConcurrentBag<Task>();
        var capturePhase = 0;

        void HandleResponse(object? sender, IResponse response)
        {
            inFlightResponses.Add(TryBufferJsonResponseAsync(
                response,
                Volatile.Read(ref capturePhase),
                capturedResponses));
        }

        context.Response += HandleResponse;

        var page = await context.NewPageAsync();
        var windowClosed = 0;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void HandleWindowClosed(object? sender, object args)
        {
            Interlocked.Exchange(ref windowClosed, 1);
            TryCancel(linkedCts);
        }

        browser.Disconnected += HandleWindowClosed;
        page.Close += HandleWindowClosed;

        await using var browserCloseRegistration = linkedCts.Token.Register(
            () => _ = SafeCloseBrowserAsync(browser));

        try
        {
            await NavigateToPortalEntryAsync(page, linkedCts.Token);

            progress?.Report(CollectionPhase.WaitingForUserLogin);
            _logger.LogInformation("Waiting for user authentication on Visa Cal portal.");

            var dashboardPage = await WaitForAuthenticatedDashboardAsync(context, page, linkedCts.Token);

            progress?.Report(CollectionPhase.LoadingStatement);
            _logger.LogInformation(
                "Authenticated on Visa Cal. Opening 90-day transactions view for period {BillingPeriod}.",
                period.ToYearMonthString());

            var transactions = await CollectCardStatementAsync(
                dashboardPage,
                period,
                capturedResponses,
                inFlightResponses,
                phase => Interlocked.Exchange(ref capturePhase, phase),
                linkedCts.Token);

            _logger.LogInformation(
                "Extracted {RawCount} transactions across 90-day window (requested {BillingPeriod}).",
                transactions.Count,
                period.ToYearMonthString());

            return transactions;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref windowClosed) == 1)
        {
            throw new OperationCanceledException("Collection cancelled.", ex, cancellationToken);
        }
        finally
        {
            context.Response -= HandleResponse;
            browser.Disconnected -= HandleWindowClosed;
            page.Close -= HandleWindowClosed;
        }
    }

    private async Task TryBufferJsonResponseAsync(
        IResponse response,
        int phase,
        ConcurrentQueue<CapturedJsonPayload> buffer)
    {
        try
        {
            if (response.Status != 200 ||
                string.Equals(response.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase) ||
                !response.Url.Contains(CalDomainSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (response.Headers.TryGetValue("content-type", out var contentType) &&
                !contentType.Contains("json", StringComparison.OrdinalIgnoreCase) &&
                !contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var text = await response.TextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var trimmed = text.AsSpan().TrimStart();
            if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
            {
                return;
            }

            buffer.Enqueue(new CapturedJsonPayload(phase, SanitizeEndpointPath(response.Url), text));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "Skipped buffering response from {EndpointPath} ({ExceptionType}).",
                SanitizeEndpointPath(response.Url),
                ex.GetType().Name);
        }
    }

    private static string SanitizeEndpointPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "unknown";
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(static segment => segment.Any(char.IsDigit) ? "{id}" : segment);

        return "/" + string.Join('/', segments);
    }

    private async Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright)
    {
        foreach (var channel in BrowserChannels)
        {
            try
            {
                return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = false,
                    Channel = channel,
                    Args = PopupWindowArgs
                });
            }
            catch (PlaywrightException ex)
            {
                _logger.LogDebug(
                    "Browser channel {Channel} unavailable ({ExceptionType}); trying next channel.",
                    channel,
                    ex.GetType().Name);
            }
        }

        throw new InvalidOperationException(CollectionFailureMessages.BrowserRequired);
    }

    private static async Task NavigateToPortalEntryAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await page.GotoAsync(PortalUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = InitialNavigationTimeoutMs
        });

        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var currentUri) ||
            !currentUri.Host.Contains(LandingHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await Task.Delay(1_000, cancellationToken);

        foreach (var selector in LandingLoginSelectors)
        {
            if (await TryClickCalmlyAsync(page.Locator(selector).First, timeoutMs: 5_000))
            {
                break;
            }
        }
    }

    private static async Task<IPage> WaitForAuthenticatedDashboardAsync(
        IBrowserContext context,
        IPage initialPage,
        CancellationToken cancellationToken)
    {
        using var loginTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(InteractiveLoginTimeoutMs));
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, loginTimeoutCts.Token);

        try
        {
            while (!waitCts.Token.IsCancellationRequested)
            {
                var pages = context.Pages.Count > 0 ? context.Pages : [initialPage];
                foreach (var candidatePage in pages)
                {
                    if (await IsAuthenticatedDashboardAsync(candidatePage))
                    {
                        return candidatePage;
                    }
                }

                await Task.Delay(600, waitCts.Token);
            }
        }
        catch (OperationCanceledException) when (loginTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for user login on Visa Cal.");
        }

        waitCts.Token.ThrowIfCancellationRequested();
        return initialPage;
    }

    private static async Task<bool> IsAuthenticatedDashboardAsync(IPage page)
    {
        if (page.IsClosed || !Uri.TryCreate(page.Url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Host.Contains(PortalHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Contains(AuthHost, StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (await IsVisibleSafeAsync(page.Locator($"iframe[src*='{AuthHost}']").First))
        {
            return false;
        }

        if (uri.AbsolutePath.Contains("dashboard", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("transactions", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var selector in SeeAllTransactionsSelectors)
        {
            if (await CountSafeAsync(page.Locator(selector)) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<RawTransaction>> CollectCardStatementAsync(
        IPage page,
        BillingPeriod period,
        ConcurrentQueue<CapturedJsonPayload> capturedResponses,
        ConcurrentBag<Task> inFlightResponses,
        Action<int> setCapturePhase,
        CancellationToken cancellationToken)
    {
        using var stepTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(PostLoginStepTimeoutMs));
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stepTimeoutCts.Token);

        try
        {
            await Task.Delay(3_500, stepCts.Token);

            var modalsClosed = await DismissPostLoginModalsAsync(page, stepCts.Token);
            if (modalsClosed > 0)
            {
                _logger.LogInformation("Dismissed {ModalCount} post-login popup modal(s).", modalsClosed);
                await Task.Delay(1_500, stepCts.Token);
            }

            setCapturePhase(1);
            var openedSeeAll = await OpenSeeAllTransactionsPatientlyAsync(page, stepCts.Token);
            _logger.LogInformation("See-all-transactions navigation triggered: {Opened}.", openedSeeAll);

            await WaitCalmlyForStatementDataAsync(page, capturedResponses, minPhase: 1, stepCts.Token);
            await Task.WhenAll(inFlightResponses).WaitAsync(stepCts.Token);

            var snapshots = capturedResponses.ToArray();
            var jsonAll = CalStatementParser.SelectAllJsonTransactions(snapshots, period, out var jsonMatchedCount);

            IReadOnlyList<RawTransaction> domAll = Array.Empty<RawTransaction>();
            var domMatchedCount = 0;
            if (jsonAll.Count == 0)
            {
                var rawDomRows = await ExtractDomRowsAsync(page, stepCts.Token);
                domAll = CalStatementParser.ParseStatementRows(rawDomRows, filterPeriod: null, referencePeriod: null);
                domMatchedCount = domAll.Count(item =>
                    CalStatementParser.TryParseCollectorDate(item.DateText, referencePeriod: null, out var d) && period.Contains(d));
            }

            var endpointsSeen = string.Join(", ", snapshots.Select(static s => s.EndpointKey).Distinct(StringComparer.OrdinalIgnoreCase));
            _logger.LogInformation(
                "Portal extraction summary for {BillingPeriod}: JsonResponses={ResponseCount} [{Endpoints}], JsonTotal90Days={JsonTotal}, JsonPeriodMatched={JsonMatched}, DomTotal90Days={DomTotal}, DomPeriodMatched={DomMatched}.",
                period.ToYearMonthString(),
                snapshots.Length,
                endpointsSeen,
                jsonAll.Count,
                jsonMatchedCount,
                domAll.Count,
                domMatchedCount);

            await Task.Delay(1_000, stepCts.Token);

            if (jsonAll.Count > 0)
            {
                return jsonAll;
            }

            if (domAll.Count > 0)
            {
                return domAll;
            }

            throw new InvalidOperationException("No transaction records were readable from Visa Cal.");
        }
        catch (OperationCanceledException) when (stepTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out loading Visa Cal transactions view.");
        }
    }

    private async Task<int> DismissPostLoginModalsAsync(IPage page, CancellationToken cancellationToken)
    {
        var dismissedCount = 0;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var activeModal = await FindVisibleModalContainerAsync(page);
            if (activeModal is null)
            {
                break;
            }

            var clickedClose = false;
            foreach (var closeSelector in ModalDismissButtonSelectors)
            {
                if (await TryClickCalmlyAsync(activeModal.Locator(closeSelector).First, timeoutMs: 2_500))
                {
                    clickedClose = true;
                    dismissedCount++;
                    break;
                }
            }

            if (!clickedClose)
            {
                foreach (var globalCloseSelector in new[]
                {
                    ".cdk-overlay-container button[aria-label*='סגור']",
                    ".cdk-overlay-container button[aria-label*='close' i]",
                    ".cdk-overlay-container [mat-dialog-close]",
                    ".cdk-overlay-container button[class*='close' i]"
                })
                {
                    if (await TryClickCalmlyAsync(page.Locator(globalCloseSelector).First, timeoutMs: 2_500))
                    {
                        clickedClose = true;
                        dismissedCount++;
                        break;
                    }
                }
            }

            if (!clickedClose)
            {
                try
                {
                    await page.Keyboard.PressAsync("Escape");
                }
                catch (PlaywrightException ex)
                {
                    _logger.LogDebug("Modal Escape press skipped ({ExceptionType}).", ex.GetType().Name);
                }
            }

            await Task.Delay(1_500, cancellationToken);

            if (!clickedClose || await FindVisibleModalContainerAsync(page) is null)
            {
                break;
            }
        }

        return dismissedCount;
    }

    private static async Task<ILocator?> FindVisibleModalContainerAsync(IPage page)
    {
        foreach (var modalSelector in ModalContainerSelectors)
        {
            var modals = page.Locator(modalSelector);
            var count = await CountSafeAsync(modals);
            for (var i = 0; i < count; i++)
            {
                var modal = modals.Nth(i);
                if (await IsVisibleSafeAsync(modal))
                {
                    return modal;
                }
            }
        }

        return null;
    }

    private static async Task<bool> HasBlockingOverlayBackdropAsync(IPage page) =>
        await FindVisibleModalContainerAsync(page) is not null ||
        await IsVisibleSafeAsync(page.Locator(".cdk-overlay-backdrop-showing, .modal-backdrop.show").First);

    private async Task<bool> OpenSeeAllTransactionsPatientlyAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        var loggedOverlayWait = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await DismissPostLoginModalsAsync(page, cancellationToken) > 0)
            {
                await Task.Delay(1_200, cancellationToken);
            }

            if (await HasBlockingOverlayBackdropAsync(page))
            {
                if (!loggedOverlayWait)
                {
                    _logger.LogInformation("Popup modal is still open on Visa Cal; waiting calmly for it to close.");
                    loggedOverlayWait = true;
                }

                await Task.Delay(1_500, cancellationToken);
                continue;
            }

            if (Uri.TryCreate(page.Url, UriKind.Absolute, out var currentUri) &&
                currentUri.AbsolutePath.Contains("transaction", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var selector in SeeAllTransactionsSelectors)
            {
                var locator = page.Locator(selector).First;
                if (await CountSafeAsync(locator) == 0)
                {
                    continue;
                }

                try
                {
                    await locator.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions
                    {
                        Timeout = 3_000
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Scrolling transaction link into view skipped ({ExceptionType}).", ex.GetType().Name);
                }

                await Task.Delay(600, cancellationToken);

                if (await TryClickCalmlyAsync(locator, timeoutMs: 3_500))
                {
                    return true;
                }
            }

            try
            {
                await page.EvaluateAsync("() => window.scrollBy({ top: 280, behavior: 'smooth' })");
            }
            catch (PlaywrightException ex)
            {
                _logger.LogDebug("Dashboard scroll nudge skipped ({ExceptionType}).", ex.GetType().Name);
            }

            await Task.Delay(1_500, cancellationToken);
        }

        return false;
    }

    private async Task WaitCalmlyForStatementDataAsync(
        IPage page,
        ConcurrentQueue<CapturedJsonPayload> capturedResponses,
        int minPhase,
        CancellationToken cancellationToken)
    {
        await Task.Delay(2_500, cancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasMatchingPhaseJson = capturedResponses.Any(item =>
                item.Phase >= minPhase &&
                CalStatementParser.ExtractTransactionsFromJson(item.Body, filterPeriod: null).Count > 0);

            if (hasMatchingPhaseJson)
            {
                await Task.Delay(1_200, cancellationToken);
                break;
            }

            await Task.Delay(700, cancellationToken);
        }

        try
        {
            await page.EvaluateAsync("() => window.scrollBy({ top: 420, behavior: 'smooth' })");
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug("Statement scroll nudge skipped ({ExceptionType}).", ex.GetType().Name);
        }

        await Task.Delay(1_200, cancellationToken);
    }

    private static async Task<string[][]> ExtractDomRowsAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await page.EvaluateAsync<string[][]>(
            """
            () => {
                const results = [];
                const seenRows = new Set();

                const tableRows = Array.from(document.querySelectorAll("table tbody tr, [role='row'], mat-row, .mat-mdc-row"));
                for (const row of tableRows) {
                    const cells = Array.from(row.querySelectorAll("td, [role='cell'], [role='gridcell'], mat-cell, .mat-mdc-cell"))
                        .map(cell => (cell.innerText || cell.textContent || "").trim())
                        .filter(Boolean);
                    if (cells.length >= 3) {
                        seenRows.add(row);
                        results.push(cells);
                    }
                }

                const datePattern = /^(\d{1,2}[\/.]\d{1,2}([\/.]\d{2,4})?|\d{4}-\d{2}-\d{2})$/;
                const amountHintPattern = /(₪|\$|€|£|ש"ח|ש״ח|\d+\.\d{2})/;

                const allElements = Array.from(document.querySelectorAll("body *"));
                const dateLeaves = allElements.filter(el => {
                    if (el.children.length > 0) return false;
                    const txt = (el.innerText || el.textContent || "").trim();
                    return txt.length >= 4 && txt.length <= 12 && datePattern.test(txt);
                });

                const countDatesInside = (container) => {
                    let count = 0;
                    for (const leaf of dateLeaves) {
                        if (container.contains(leaf)) {
                            count++;
                            if (count > 2) return count;
                        }
                    }
                    return count;
                };

                for (const dateLeaf of dateLeaves) {
                    let candidate = dateLeaf.parentElement;
                    let chosenRow = null;

                    for (let depth = 0; depth < 6 && candidate && candidate !== document.body; depth++) {
                        if (countDatesInside(candidate) > 2) {
                            break;
                        }

                        if (amountHintPattern.test((candidate.innerText || "").trim())) {
                            chosenRow = candidate;
                            break;
                        }

                        candidate = candidate.parentElement;
                    }

                    if (!chosenRow || seenRows.has(chosenRow)) {
                        continue;
                    }

                    const leafTexts = Array.from(chosenRow.querySelectorAll("*"))
                        .filter(el => el.children.length === 0)
                        .map(el => (el.innerText || el.textContent || "").trim())
                        .filter(Boolean);

                    if (leafTexts.length >= 3) {
                        seenRows.add(chosenRow);
                        results.push(leafTexts);
                    }
                }

                return results;
            }
            """);
    }

    private static async Task<bool> TryClickCalmlyAsync(ILocator locator, int timeoutMs)
    {
        try
        {
            if (!await locator.IsVisibleAsync())
            {
                return false;
            }

            await locator.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> IsVisibleSafeAsync(ILocator locator)
    {
        try
        {
            return await locator.IsVisibleAsync();
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<int> CountSafeAsync(ILocator locator)
    {
        try
        {
            return await locator.CountAsync();
        }
        catch (PlaywrightException)
        {
            return 0;
        }
    }

    private void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug("Cancellation source already disposed ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private async Task SafeCloseBrowserAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Browser already closed during cleanup ({ExceptionType}).", ex.GetType().Name);
        }
    }
}
