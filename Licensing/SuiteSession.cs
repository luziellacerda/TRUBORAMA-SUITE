using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using TurboBoxManager.Catalog;

[assembly: InternalsVisibleTo("CatalogVerifier")]

namespace TurboBoxManager.Licensing;

public sealed class SuiteLicensingUnavailableException : Exception
{
    internal SuiteLicensingUnavailableException(string failureCode,
        Exception? innerException = null)
        : base("O licenciamento TURBORAMA_SUITE nao esta disponivel.",
            innerException)
        => FailureCode = failureCode;

    public string FailureCode { get; }
}

public sealed class SuiteAuthorizationException : SecurityException
{
    internal SuiteAuthorizationException(string reasonCode)
        : base("A loja nao possui uma sessao TURBORAMA_SUITE autorizada.")
        => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}

public sealed class SuiteAuthorizationRevokedEventArgs : EventArgs
{
    internal SuiteAuthorizationRevokedEventArgs(string reasonCode)
        => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}

/// <summary>
/// Capability handed to store code only after an authoritative session opens.
/// It has no public constructor and continuously reflects expiry or revocation.
/// </summary>
public sealed class AuthorizedStoreContext
{
    private readonly SuiteAuthorizationState _state;

    internal AuthorizedStoreContext(SuiteAuthorizationState state)
        => _state = state ?? throw new ArgumentNullException(nameof(state));

    internal SuiteAuthorizationState StateForRuntime => _state;

    public string ProductId => _state.ProductId;
    public string LicenseId => _state.LicenseId;
    public string DeviceId => _state.DeviceId;
    public string SessionId => _state.SessionId;
    public DateTimeOffset AuthorizedUntilServerTime => _state.AuthorizedUntilServerTime;
    public bool IsAuthorized => _state.IsAuthorized;
    public string? RevocationCode => _state.RevocationCode;

    /// <summary>
    /// Sticky token cancelled exactly once when this capability is revoked or expires.
    /// Consumers should link long-running privileged work to this token.
    /// </summary>
    public CancellationToken AuthorizationCancellationToken
        => _state.AuthorizationCancellationToken;

    public void ThrowIfUnauthorized()
    {
        if (!_state.IsAuthorized)
            throw new SuiteAuthorizationException(
                _state.RevocationCode ?? "SESSION_EXPIRED");
    }
}

/// <summary>
/// Atomic authorization-consumer registration. Holding this object guarantees that
/// either registration observed a valid current context or revocation wins first.
/// </summary>
public sealed class SuiteAuthorizationSubscription : IDisposable
{
    private readonly object _gate = new();
    private readonly EventHandler<SuiteAuthorizationRevokedEventArgs> _runtimeHandler;
    private SuiteLicensingRuntime? _owner;
    private EventHandler<SuiteAuthorizationRevokedEventArgs>? _consumerHandler;

    internal SuiteAuthorizationSubscription(SuiteLicensingRuntime owner,
        EventHandler<SuiteAuthorizationRevokedEventArgs> handler,
        CancellationToken authorizationCancellationToken)
    {
        _owner = owner;
        _consumerHandler = handler;
        _runtimeHandler = Dispatch;
        AuthorizationCancellationToken = authorizationCancellationToken;
    }

    public CancellationToken AuthorizationCancellationToken { get; }
    internal EventHandler<SuiteAuthorizationRevokedEventArgs> RuntimeHandler
        => _runtimeHandler;

    public void Dispose()
    {
        SuiteLicensingRuntime? owner;
        lock (_gate)
        {
            owner = _owner;
            _owner = null;
            _consumerHandler = null;
        }
        if (owner is not null)
            owner.DetachAuthorizationConsumer(_runtimeHandler);
    }

    private void Dispatch(object? sender, SuiteAuthorizationRevokedEventArgs args)
    {
        EventHandler<SuiteAuthorizationRevokedEventArgs>? consumerHandler;
        lock (_gate) consumerHandler = _consumerHandler;
        // Consumer code must never run while the subscription lock is held:
        // a revocation handler is allowed to close its window and dispose this
        // subscription synchronously without deadlocking the dispatch thread.
        consumerHandler?.Invoke(sender, args);
    }
}

public sealed class SuiteLicensingRuntime : IAsyncDisposable
{
    // System.Threading.Timer (used by Task.Delay with a TimeProvider) has a
    // platform due-time ceiling of roughly 49.7 days. Authority documents can
    // be valid for years, so long deadlines must be observed in bounded slices.
    private static readonly TimeSpan MaximumAuthorityTimerSlice =
        TimeSpan.FromDays(30);

    private readonly SuiteLicenseClient? _client;
    private readonly SuiteContentClient? _contentClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationTokenSource _authorityCancellation = new();
    private readonly object _authorizationGate = new();
    private readonly string? _unavailableCode;
    private readonly SuiteMonotonicDeadline? _authorityDeadline;
    private readonly Task _authorityExpirationTask;

    private CancellationTokenSource? _sessionCancellation;
    private Task? _sessionTask;
    private Task? _inventoryTask;
    private AuthorizedStoreContext? _currentContext;
    private EventHandler<SuiteAuthorizationRevokedEventArgs>? _authorizationRevoked;
    private int _authorityExpired;
    private int _disposed;

    internal SuiteLicensingRuntime(string unavailableCode, TimeProvider timeProvider)
    {
        _unavailableCode = unavailableCode;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _authorityExpirationTask = Task.CompletedTask;
    }

    internal SuiteLicensingRuntime(SuiteLicenseClient client,
        SuiteAuthorityConfiguration authority, TimeProvider timeProvider)
        : this(client, authority, contentAuthority: null, timeProvider,
            initialize: true)
    {
    }

    internal SuiteLicensingRuntime(SuiteLicenseClient client,
        SuiteAuthorityConfiguration authority,
        SuiteContentAuthorityConfiguration contentAuthority,
        TimeProvider timeProvider)
        : this(client, authority,
            contentAuthority
            ?? throw new ArgumentNullException(nameof(contentAuthority)),
            timeProvider,
            initialize: true)
    {
    }

    private SuiteLicensingRuntime(SuiteLicenseClient client,
        SuiteAuthorityConfiguration authority,
        SuiteContentAuthorityConfiguration? contentAuthority,
        TimeProvider timeProvider,
        bool initialize)
    {
        _ = initialize;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(authority);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (contentAuthority is not null)
        {
            if (SuiteOnlineLicenseProtocol.FixedHexEquals(
                    contentAuthority.ContentAssertionKeyId,
                    authority.OnlineAssertionKeyId)
                || SuiteOnlineLicenseProtocol.FixedHexEquals(
                    contentAuthority.ContentAssertionKeyId,
                    authority.KeyId))
                throw new SecurityException(
                    "A chave de conteudo nao esta isolada das chaves de licenciamento.");
            _contentClient = new SuiteContentClient(client, contentAuthority);
        }
        var effectiveExpiry = contentAuthority is not null
            && contentAuthority.ExpiresAt < authority.ExpiresAt
                ? contentAuthority.ExpiresAt
                : authority.ExpiresAt;
        _authorityDeadline = new SuiteMonotonicDeadline(
            _timeProvider, effectiveExpiry - _timeProvider.GetUtcNow());
        if (_authorityDeadline.IsElapsed)
        {
            _authorityExpired = 1;
            CancelNoThrow(_authorityCancellation);
            _authorityExpirationTask = Task.CompletedTask;
        }
        else
        {
            _authorityExpirationTask = WatchAuthorityExpirationAsync(
                _lifetimeCancellation.Token);
        }
    }

    public bool IsAvailable
    {
        get
        {
            if (_client is null || Volatile.Read(ref _disposed) != 0) return false;
            ObserveAuthorityExpiryIfElapsed();
            return Volatile.Read(ref _authorityExpired) == 0;
        }
    }

    public string? FailureCode
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return "RUNTIME_DISPOSED";
            if (_client is null)
                return _unavailableCode ?? "AUTHORITY_CONFIGURATION_INVALID";
            ObserveAuthorityExpiryIfElapsed();
            return Volatile.Read(ref _authorityExpired) != 0
                ? "AUTHORITY_CONFIGURATION_EXPIRED"
                : null;
        }
    }

    public AuthorizedStoreContext? CurrentContext => Volatile.Read(ref _currentContext);

    internal async Task<SuiteAuthorizedCatalog> ReadAuthorizedCatalogAsync(
        AuthorizedStoreContext context,
        IReadOnlyList<CatalogItem> publicItems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(publicItems);
        EnsureCurrentContentContext(context);
        if (_contentClient is null)
            throw new SuiteLicensingUnavailableException(
                "CONTENT_AUTHORITY_CONFIGURATION_MISSING");
        var expectedItems = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in publicItems)
        {
            if (item is null || !expectedItems.TryAdd(item.Id, item.Extract))
                throw new SecurityException(
                    "O catalogo publico possui identidade duplicada.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token,
            _authorityCancellation.Token,
            context.AuthorizationCancellationToken);
        var result = await _contentClient!.ReadAuthorizedCatalogAsync(
            context, expectedItems, linked.Token).ConfigureAwait(false);
        EnsureCurrentContentContext(context);
        return result;
    }

    internal CatalogDownloadService CreateCatalogDownloadService(
        AuthorizedStoreContext context,
        SuiteAuthorizedCatalog catalog,
        CatalogDownloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        EnsureCurrentContentContext(context);
        if (_contentClient is null)
            return new CatalogDownloadService(options);
        var provider = _contentClient!.CreateRequestProvider(
            context, catalog, () => EnsureCurrentContentContext(context));
        return new CatalogDownloadService(
            _contentClient.DownloadHttpClient,
            provider,
            new CatalogDownloadOptions
            {
                MaximumFileSizeBytes = options.MaximumFileSizeBytes,
                MaximumRedirects = 1,
                InactivityTimeout = options.InactivityTimeout,
                RetryDelays = options.RetryDelays,
                AllowedHosts = new HashSet<string>(
                    [_contentClient.AuthorityHost],
                    StringComparer.OrdinalIgnoreCase)
            });
    }

    public event EventHandler<SuiteAuthorizationRevokedEventArgs>? AuthorizationRevoked
    {
        add
        {
            lock (_authorizationGate) _authorizationRevoked += value;
        }
        remove
        {
            lock (_authorizationGate) _authorizationRevoked -= value;
        }
    }

    /// <summary>
    /// Registers a consumer and validates that <paramref name="context"/> is still
    /// the current authorized capability as one atomic operation with revocation.
    /// </summary>
    public SuiteAuthorizationSubscription AttachAuthorizationConsumer(
        AuthorizedStoreContext context,
        EventHandler<SuiteAuthorizationRevokedEventArgs> revokedHandler)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(revokedHandler);
        EnsureAvailable();

        var authorityExpired = false;
        var sessionExpired = false;
        lock (_authorizationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            authorityExpired = Volatile.Read(ref _authorityExpired) != 0
                || (_authorityDeadline?.IsElapsed ?? false);
            if (!authorityExpired)
            {
                if (!ReferenceEquals(_currentContext, context))
                    throw new SuiteAuthorizationException("CONTEXT_NOT_CURRENT");
                sessionExpired = !context.StateForRuntime.IsAuthorized;
                if (!sessionExpired)
                {
                    var subscription = new SuiteAuthorizationSubscription(
                        this, revokedHandler, context.AuthorizationCancellationToken);
                    _authorizationRevoked += subscription.RuntimeHandler;
                    return subscription;
                }
            }
        }

        if (authorityExpired)
        {
            ExpireAuthority();
            throw new SuiteLicensingUnavailableException(
                "AUTHORITY_CONFIGURATION_EXPIRED");
        }

        Revoke(context.StateForRuntime, "SESSION_EXPIRED");
        throw new SuiteAuthorizationException(
            context.RevocationCode ?? "SESSION_EXPIRED");
    }

    public async Task<AuthorizedStoreContext> ActivateAndOpenAsync(string licenseId,
        string activationCode, CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCancellation.Token,
            _authorityCancellation.Token);
        var entered = false;
        try
        {
            await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            EnsureAvailable();
            await RetireCurrentSessionAsync("SESSION_REPLACED").ConfigureAwait(false);
            try
            {
                await _client!.ActivateAsync(licenseId, activationCode, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (SuiteActivationIndeterminateException indeterminate)
            {
                try
                {
                    return await OpenCoreAsync(licenseId, linked.Token)
                        .ConfigureAwait(false);
                }
                catch (SuiteLicensingUnavailableException) { throw; }
                catch (Exception verificationFailure) when (
                    verificationFailure is not OperationCanceledException)
                {
                    ExceptionDispatchInfo.Capture(indeterminate).Throw();
                    throw;
                }
            }

            return await OpenCoreAsync(licenseId, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (
            ShouldReportAuthorityExpiration(cancellationToken))
        {
            throw new SuiteLicensingUnavailableException(
                "AUTHORITY_CONFIGURATION_EXPIRED", ex);
        }
        finally
        {
            if (entered) _operationGate.Release();
        }
    }

    public async Task<AuthorizedStoreContext> OpenAsync(string licenseId,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCancellation.Token,
            _authorityCancellation.Token);
        var entered = false;
        try
        {
            await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            EnsureAvailable();
            await RetireCurrentSessionAsync("SESSION_REPLACED").ConfigureAwait(false);
            return await OpenCoreAsync(licenseId, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (
            ShouldReportAuthorityExpiration(cancellationToken))
        {
            throw new SuiteLicensingUnavailableException(
                "AUTHORITY_CONFIGURATION_EXPIRED", ex);
        }
        finally
        {
            if (entered) _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelNoThrow(_lifetimeCancellation);
        CancelNoThrow(_authorityCancellation);
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RetireCurrentSessionAsync("RUNTIME_DISPOSED").ConfigureAwait(false);
            _contentClient?.Dispose();
            _client?.Dispose();
        }
        finally
        {
            _operationGate.Release();
        }

        try { await _authorityExpirationTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        _authorityCancellation.Dispose();
        _lifetimeCancellation.Dispose();
        _operationGate.Dispose();
    }

    internal void DetachAuthorizationConsumer(
        EventHandler<SuiteAuthorizationRevokedEventArgs> handler)
    {
        lock (_authorizationGate) _authorizationRevoked -= handler;
    }

    private async Task<AuthorizedStoreContext> OpenCoreAsync(string licenseId,
        CancellationToken cancellationToken)
    {
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
        // Start the monotonic validity window before network I/O. This is
        // conservative: transport delay can shorten, but never extend, authority.
        var validityStartedTimestamp = _timeProvider.GetTimestamp();
        var response = await _client!.OpenSessionAsync(licenseId, sessionId,
            heartbeat: false, cancellationToken).ConfigureAwait(false);
        EnsureAvailable();

        var state = new SuiteAuthorizationState(
            _timeProvider, response, validityStartedTimestamp);
        var context = new AuthorizedStoreContext(state);
        var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token, _authorityCancellation.Token);
        var authorityExpired = false;
        lock (_authorizationGate)
        {
            authorityExpired = Volatile.Read(ref _authorityExpired) != 0
                || (_authorityDeadline?.IsElapsed ?? false);
            if (!authorityExpired)
            {
                Volatile.Write(ref _currentContext, context);
                _sessionCancellation = sessionCancellation;
            }
        }

        if (authorityExpired)
        {
            sessionCancellation.Dispose();
            ExpireAuthority();
            throw new SuiteLicensingUnavailableException(
                "AUTHORITY_CONFIGURATION_EXPIRED");
        }

        _sessionTask = MaintainSessionAsync(state, response,
            sessionCancellation.Token);
        _inventoryTask = PublishMotherboardInventoryBestEffortAsync(
            context, sessionCancellation.Token);
        return context;
    }

    private async Task PublishMotherboardInventoryBestEffortAsync(
        AuthorizedStoreContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await _client!.PublishMotherboardInventoryAsync(
                    context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Inventory is auxiliary evidence only. The signed session and its
            // heartbeat remain the sole authority for the active capability.
        }
    }

    private async Task MaintainSessionAsync(SuiteAuthorizationState state,
        SuiteSessionResponse initialResponse, CancellationToken cancellationToken)
    {
        var heartbeat = HeartbeatLoopAsync(state, initialResponse, cancellationToken);
        var expiration = SessionExpirationLoopAsync(state, cancellationToken);
        await Task.WhenAll(heartbeat, expiration).ConfigureAwait(false);
    }

    private async Task SessionExpirationLoopAsync(SuiteAuthorizationState state,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await state.WaitForExpirationAsync(cancellationToken).ConfigureAwait(false))
                Revoke(state, "SESSION_EXPIRED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            Revoke(state, "SESSION_CLOCK_FAILED");
        }
    }

    private async Task HeartbeatLoopAsync(SuiteAuthorizationState state,
        SuiteSessionResponse initialResponse, CancellationToken cancellationToken)
    {
        var response = initialResponse;
        var transientFailureCount = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = transientFailureCount == 0
                    ? HeartbeatDelay(response)
                    : TransientRetryDelay(transientFailureCount);
                var requiredRemaining = transientFailureCount == 0
                    ? delay
                    : delay + TimeSpan.FromSeconds(1);
                if (!state.CanWait(requiredRemaining))
                {
                    Revoke(state, "SESSION_EXPIRED");
                    return;
                }

                await Task.Delay(delay, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                if (!state.IsAuthorized)
                {
                    Revoke(state, "SESSION_EXPIRED");
                    return;
                }

                try
                {
                    var validityStartedTimestamp = _timeProvider.GetTimestamp();
                    response = await _client!.OpenSessionAsync(state.LicenseId,
                        state.SessionId, heartbeat: true, cancellationToken)
                        .ConfigureAwait(false);
                    if (!state.IsAuthorized)
                    {
                        Revoke(state, "SESSION_EXPIRED");
                        return;
                    }
                    state.Renew(response, validityStartedTimestamp);
                    transientFailureCount = 0;
                }
                catch (SuiteLicensingUnavailableException)
                {
                    Revoke(state, "IDENTITY_UNAVAILABLE");
                    return;
                }
                catch (SuiteApiException ex) when (ex.StatusCode >= 500
                    && !string.Equals(ex.Code, "INVALID_RESPONSE", StringComparison.Ordinal))
                {
                    transientFailureCount++;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                    && (ex is HttpRequestException or TaskCanceledException))
                {
                    transientFailureCount++;
                }
                catch (SuiteApiException)
                {
                    Revoke(state, "AUTHORITY_DENIED");
                    return;
                }
                catch (SecurityException)
                {
                    Revoke(state, "INVALID_AUTHORITY_RESPONSE");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            Revoke(state, "HEARTBEAT_FAILED");
        }
    }

    private async Task WatchAuthorityExpirationAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var remaining = _authorityDeadline?.Remaining ?? TimeSpan.Zero;
                if (remaining <= TimeSpan.Zero)
                {
                    ExpireAuthority();
                    return;
                }

                await Task.Delay(
                    remaining < MaximumAuthorityTimerSlice
                        ? remaining
                        : MaximumAuthorityTimerSlice,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            ExpireAuthority();
        }
    }

    private static TimeSpan TransientRetryDelay(int failureCount)
    {
        var exponent = Math.Min(failureCount - 1, 5);
        var seconds = Math.Min(30, 2 * (1 << exponent));
        var jitter = RandomNumberGenerator.GetInt32(-1000, 1001) / 10_000d;
        return TimeSpan.FromSeconds(Math.Max(1, seconds * (1 + jitter)));
    }

    private static TimeSpan HeartbeatDelay(SuiteSessionResponse response)
    {
        var ttl = response.AuthorizedUntilUnixSeconds - response.ServerTimeUnixSeconds;
        var margin = Math.Min(60d, Math.Max(15d, ttl / 10d));
        var latest = Math.Max(1d, ttl - margin);
        var requested = Math.Min(response.HeartbeatAfterSeconds, latest);
        var jitter = RandomNumberGenerator.GetInt32(-1000, 1001) / 10_000d;
        return TimeSpan.FromSeconds(Math.Clamp(requested * (1 + jitter), 1d, latest));
    }

    private async Task RetireCurrentSessionAsync(string reasonCode)
    {
        var cancellation = Interlocked.Exchange(ref _sessionCancellation, null);
        var task = Interlocked.Exchange(ref _sessionTask, null);
        var inventoryTask = Interlocked.Exchange(ref _inventoryTask, null);
        if (cancellation is not null)
        {
            CancelNoThrow(cancellation);
            if (task is not null)
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (inventoryTask is not null)
            {
                try { await inventoryTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            cancellation.Dispose();
        }

        AuthorizedStoreContext? context;
        lock (_authorizationGate)
        {
            context = _currentContext;
            Volatile.Write(ref _currentContext, null);
        }
        if (context is not null) Revoke(context.StateForRuntime, reasonCode);
    }

    private void Revoke(SuiteAuthorizationState state, string reasonCode)
    {
        EventHandler<SuiteAuthorizationRevokedEventArgs>? handlers;
        CancellationTokenSource? sessionCancellation;
        lock (_authorizationGate)
        {
            if (!state.TryMarkRevoked(reasonCode)) return;
            handlers = _authorizationRevoked;
            sessionCancellation = ReferenceEquals(
                _currentContext?.StateForRuntime, state)
                ? _sessionCancellation
                : null;
        }

        state.PublishRevocation();
        CancelNoThrow(sessionCancellation);
        if (handlers is null) return;
        var args = new SuiteAuthorizationRevokedEventArgs(reasonCode);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (EventHandler<SuiteAuthorizationRevokedEventArgs> handler
                in handlers.GetInvocationList())
            {
                try { handler(this, args); }
                catch { }
            }
        });
    }

    private void ExpireAuthority()
    {
        if (Interlocked.Exchange(ref _authorityExpired, 1) != 0) return;
        CancelNoThrow(_authorityCancellation);
        var context = Volatile.Read(ref _currentContext);
        if (context is not null)
            Revoke(context.StateForRuntime, "AUTHORITY_CONFIGURATION_EXPIRED");
    }

    private void ObserveAuthorityExpiryIfElapsed()
    {
        if (Volatile.Read(ref _authorityExpired) == 0
            && (_authorityDeadline?.IsElapsed ?? false))
            ExpireAuthority();
    }

    private bool ShouldReportAuthorityExpiration(CancellationToken callerToken)
        => !callerToken.IsCancellationRequested
            && !_lifetimeCancellation.IsCancellationRequested
            && _authorityCancellation.IsCancellationRequested
            && Volatile.Read(ref _authorityExpired) != 0;

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_client is null)
            throw new SuiteLicensingUnavailableException(
                _unavailableCode ?? "AUTHORITY_CONFIGURATION_INVALID");
        ObserveAuthorityExpiryIfElapsed();
        if (Volatile.Read(ref _authorityExpired) != 0)
            throw new SuiteLicensingUnavailableException(
                "AUTHORITY_CONFIGURATION_EXPIRED");
    }

    private void EnsureCurrentContentContext(AuthorizedStoreContext context)
    {
        EnsureAvailable();
        var authorityExpired = false;
        var sessionExpired = false;
        lock (_authorizationGate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0, this);
            authorityExpired = Volatile.Read(ref _authorityExpired) != 0
                || (_authorityDeadline?.IsElapsed ?? false);
            if (!authorityExpired)
            {
                if (!ReferenceEquals(_currentContext, context))
                    throw new SuiteAuthorizationException(
                        "CONTEXT_NOT_CURRENT");
                sessionExpired = !context.StateForRuntime.IsAuthorized;
                if (!sessionExpired) return;
            }
        }

        if (authorityExpired)
        {
            ExpireAuthority();
            throw new SuiteLicensingUnavailableException(
                "AUTHORITY_CONFIGURATION_EXPIRED");
        }

        Revoke(context.StateForRuntime, "SESSION_EXPIRED");
        throw new SuiteAuthorizationException(
            context.RevocationCode ?? "SESSION_EXPIRED");
    }

    private static void CancelNoThrow(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(throwOnFirstException: false); }
        catch (AggregateException) { }
        catch (ObjectDisposedException) { }
    }
}

public static class SuiteLicensingFactory
{
    public static SuiteLicensingRuntime CreateDefault(TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var assembly = Assembly.GetEntryAssembly() ?? typeof(SuiteLicensingFactory).Assembly;
        var loaded = SuiteEmbeddedAuthorityLoader.Load(assembly, time);
        if (loaded.Configuration is null)
            return new SuiteLicensingRuntime(loaded.FailureCode, time);
        var contentLoaded = SuiteEmbeddedContentAuthorityLoader.Load(
            assembly, time);
        if (contentLoaded.Configuration is null)
            return new SuiteLicensingRuntime(contentLoaded.FailureCode, time);

        try
        {
            var identity = new SuiteCngMachineIdentity(
                loaded.Configuration.IdentityPolicy);
            ISuiteMotherboardInventorySource? inventorySource = null;
            ISuiteInventoryPublicationStateStore? inventoryStateStore = null;
            try
            {
                inventorySource = new SuiteWindowsMotherboardInventorySource();
                inventoryStateStore = new SuiteInventoryPublicationStateStore();
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException
                or SecurityException or UnauthorizedAccessException
                or NotSupportedException or InvalidOperationException
                or IOException or ArgumentException)
            {
                // Auxiliary hardware inventory must never disable the existing
                // activation/session capability when local collection or its
                // protected cache cannot be initialized.
                inventorySource = null;
                inventoryStateStore = null;
            }

            var client = inventorySource is not null
                && inventoryStateStore is not null
                ? new SuiteLicenseClient(
                    loaded.Configuration,
                    identity,
                    inventorySource,
                    inventoryStateStore,
                    time)
                : new SuiteLicenseClient(
                    loaded.Configuration,
                    identity,
                    time);
            return new SuiteLicensingRuntime(
                client,
                loaded.Configuration,
                contentLoaded.Configuration,
                time);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException
            or CryptographicException or SecurityException
            or UnauthorizedAccessException or NotSupportedException
            or ArgumentException)
        {
            return new SuiteLicensingRuntime("IDENTITY_UNAVAILABLE", time);
        }
    }
}

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source intentionally has the same GC lifetime as the sticky token handed to authorization consumers; disposing it at revocation would make late token registration race with disposal.")]
internal sealed class SuiteAuthorizationState
{
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _revocationCancellation = new();
    private TaskCompletionSource _deadlineChanged = NewDeadlineSignal();
    private long _deadlineStartedTimestamp;
    private TimeSpan _deadlineLifetime;
    private int _revoked;
    private string? _revocationCode;
    private DateTimeOffset _authorizedUntilServerTime;
    private long _lastServerTimeUnixSeconds;
    private bool _initialized;

    public SuiteAuthorizationState(TimeProvider timeProvider, SuiteSessionResponse response)
        : this(timeProvider, response,
            (timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)))
                .GetTimestamp())
    {
    }

    public SuiteAuthorizationState(TimeProvider timeProvider,
        SuiteSessionResponse response, long validityStartedTimestamp)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(response);
        ProductId = response.ProductId;
        LicenseId = response.LicenseId;
        DeviceId = response.DeviceId;
        SessionId = response.SessionId;
        Renew(response, validityStartedTimestamp);
    }

    public string ProductId { get; }
    public string LicenseId { get; }
    public string DeviceId { get; }
    public string SessionId { get; }
    public CancellationToken AuthorizationCancellationToken
        => _revocationCancellation.Token;

    public DateTimeOffset AuthorizedUntilServerTime
    {
        get { lock (_gate) return _authorizedUntilServerTime; }
    }

    public string? RevocationCode
    {
        get { lock (_gate) return _revocationCode; }
    }

    public bool IsAuthorized
    {
        get
        {
            lock (_gate)
            {
                return _revoked == 0 && RemainingUnsafe() > TimeSpan.Zero;
            }
        }
    }

    public void Renew(SuiteSessionResponse response)
        => Renew(response, _timeProvider.GetTimestamp());

    public void Renew(SuiteSessionResponse response, long validityStartedTimestamp)
    {
        SuiteOnlineLicenseProtocol.ValidateSessionResponse(response,
            LicenseId, DeviceId, SessionId);
        var lifetimeSeconds = response.AuthorizedUntilUnixSeconds
            - response.ServerTimeUnixSeconds;
        var lifetime = TimeSpan.FromSeconds(lifetimeSeconds);
        var authorizedUntil = DateTimeOffset.FromUnixTimeSeconds(
            response.AuthorizedUntilUnixSeconds);
        var elapsedBeforeReceipt = _timeProvider.GetElapsedTime(
            validityStartedTimestamp, _timeProvider.GetTimestamp());
        if (elapsedBeforeReceipt >= lifetime)
            throw new SecurityException(
                "A sessao recebida ja atingiu seu prazo monotonicamente.");
        TaskCompletionSource? changed = null;
        lock (_gate)
        {
            if (_revoked != 0 || (_initialized && RemainingUnsafe() <= TimeSpan.Zero))
                throw new SecurityException("Uma sessao revogada nao pode ser renovada.");
            if (_initialized
                && response.ServerTimeUnixSeconds <= _lastServerTimeUnixSeconds)
                throw new SecurityException(
                    "Uma resposta de heartbeat repetida ou fora de ordem foi rejeitada.");
            if (_initialized)
            {
                changed = _deadlineChanged;
                _deadlineChanged = NewDeadlineSignal();
            }
            _deadlineStartedTimestamp = validityStartedTimestamp;
            _deadlineLifetime = lifetime;
            _authorizedUntilServerTime = authorizedUntil;
            _lastServerTimeUnixSeconds = response.ServerTimeUnixSeconds;
            _initialized = true;
        }
        changed?.TrySetResult();
    }

    public bool CanWait(TimeSpan delay)
    {
        lock (_gate)
        {
            return _revoked == 0 && delay >= TimeSpan.Zero
                && RemainingUnsafe() > delay;
        }
    }

    public async Task<bool> WaitForExpirationAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan remaining;
            Task changed;
            lock (_gate)
            {
                if (_revoked != 0) return false;
                remaining = RemainingUnsafe();
                if (remaining <= TimeSpan.Zero) return true;
                changed = _deadlineChanged.Task;
            }

            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var delay = Task.Delay(remaining, _timeProvider, delayCancellation.Token);
            var completed = await Task.WhenAny(delay, changed).ConfigureAwait(false);
            if (ReferenceEquals(completed, delay))
            {
                await delay.ConfigureAwait(false);
            }
            else
            {
                CancelNoThrow(delayCancellation);
                try { await delay.ConfigureAwait(false); }
                catch (OperationCanceledException) when (
                    delayCancellation.IsCancellationRequested)
                { }
            }
        }
    }

    public bool TryMarkRevoked(string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("Um codigo de revogacao e obrigatorio.",
                nameof(reasonCode));
        lock (_gate)
        {
            if (_revoked != 0) return false;
            _revoked = 1;
            _revocationCode = reasonCode;
            return true;
        }
    }

    public void PublishRevocation()
    {
        TaskCompletionSource changed;
        lock (_gate) changed = _deadlineChanged;
        changed.TrySetResult();
        // Cancellation callbacks belong to consumers and may synchronously dispose
        // the runtime. Dispatching them prevents a callback from waiting on the very
        // expiry task that is currently publishing this revocation.
        ThreadPool.QueueUserWorkItem(_ => CancelNoThrow(_revocationCancellation));
    }

    private TimeSpan RemainingUnsafe()
    {
        var elapsed = _timeProvider.GetElapsedTime(
            _deadlineStartedTimestamp, _timeProvider.GetTimestamp());
        if (elapsed <= TimeSpan.Zero) return _deadlineLifetime;
        if (elapsed >= _deadlineLifetime) return TimeSpan.Zero;
        return _deadlineLifetime - elapsed;
    }

    private static TaskCompletionSource NewDeadlineSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void CancelNoThrow(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(throwOnFirstException: false); }
        catch (AggregateException) { }
        catch (ObjectDisposedException) { }
    }
}

internal sealed class SuiteMonotonicDeadline
{
    private readonly TimeProvider _timeProvider;
    private readonly long _startedTimestamp;
    private readonly TimeSpan _duration;

    public SuiteMonotonicDeadline(TimeProvider timeProvider, TimeSpan duration)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _startedTimestamp = _timeProvider.GetTimestamp();
        _duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    public TimeSpan Remaining
    {
        get
        {
            var elapsed = _timeProvider.GetElapsedTime(
                _startedTimestamp, _timeProvider.GetTimestamp());
            if (elapsed <= TimeSpan.Zero) return _duration;
            if (elapsed >= _duration) return TimeSpan.Zero;
            return _duration - elapsed;
        }
    }

    public bool IsElapsed => Remaining <= TimeSpan.Zero;
}
