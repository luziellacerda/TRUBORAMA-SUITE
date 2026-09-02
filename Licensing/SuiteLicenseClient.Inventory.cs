using System.IO;
using System.Net;
using System.Net.Http;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;

namespace TurboBoxManager.Licensing;

internal enum SuiteInventoryPublicationOutcome
{
    Published,
    AlreadyCurrent,
    Unsupported,
    Deferred,
    Rejected,
    CollectionUnavailable,
    Disabled
}

internal sealed partial class SuiteLicenseClient
{
    private static readonly TimeSpan InventoryRequestTimeout =
        TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InventoryUnsupportedTtl =
        TimeSpan.FromHours(24);
    private static readonly TimeSpan InventoryAcceptedRefreshInterval =
        TimeSpan.FromDays(30);
    private static readonly TimeSpan[] InventoryRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    internal async Task<SuiteInventoryPublicationOutcome>
        PublishMotherboardInventoryAsync(
            AuthorizedStoreContext context,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_inventorySource is null || _inventoryStateStore is null)
            return SuiteInventoryPublicationOutcome.Disabled;

        // This optional operation must never extend the synchronous path that
        // returns an already-authorized store context to the application.
        await Task.Yield();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.IsAuthorized)
                return SuiteInventoryPublicationOutcome.Deferred;

            var descriptor = UseMachineIdentity(_identity.Describe);
            if (!FixedHexEquals(descriptor.DeviceId, context.DeviceId))
                return SuiteInventoryPublicationOutcome.Rejected;

            var collected = await _inventorySource
                .CollectAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!collected.HasIdentityEvidence
                || collected.SchemaVersion != SuiteDeviceInventoryProtocol.SchemaVersion
                || !FixedHexEquals(
                    collected.MotherboardFingerprint,
                    SuiteMotherboardInventoryNormalizer.ComputeFingerprint(collected)))
                return SuiteInventoryPublicationOutcome.CollectionUnavailable;

            var now = _timeProvider.GetUtcNow();
            var inventory = new SuiteMotherboardInventoryV1(
                SuiteDeviceInventoryProtocol.SchemaVersion,
                context.LicenseId,
                context.DeviceId,
                collected.MotherboardFingerprint,
                collected.BaseboardManufacturer,
                collected.BaseboardProduct,
                collected.BaseboardVersion,
                collected.BaseboardSerial,
                collected.SystemManufacturer,
                collected.SystemModel,
                collected.SystemUuid,
                collected.BiosManufacturer,
                collected.BiosVersion,
                collected.OsName,
                collected.OsVersion,
                collected.Architecture,
                collected.ClientVersion,
                collected.Source,
                now.ToUnixTimeSeconds());
            SuiteDeviceInventoryProtocol.ValidateInventory(inventory);

            var semanticStateHash =
                SuiteDeviceInventoryProtocol.InventoryStateHash(inventory);
            var inventoryHash =
                SuiteDeviceInventoryProtocol.InventoryHash(inventory);
            var cacheKey = new SuiteInventoryPublicationCacheKey(
                _onlineAssertionKeyId,
                SuiteDeviceInventoryProtocol.SchemaVersion,
                SuiteDeviceInventoryProtocol.ProductId,
                context.LicenseId,
                context.DeviceId);
            var cached = await _inventoryStateStore
                .LoadAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);

            if (cached?.UnsupportedUntil is { } unsupportedUntil
                && unsupportedUntil > now)
                return SuiteInventoryPublicationOutcome.Unsupported;

            if (cached?.AcceptedAt is { } acceptedAt
                && acceptedAt >= now - InventoryAcceptedRefreshInterval
                && FixedHexEquals(cached.SemanticStateHash, semanticStateHash))
                return SuiteInventoryPublicationOutcome.AlreadyCurrent;

            for (var attempt = 0; attempt <= InventoryRetryDelays.Length; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!context.IsAuthorized)
                    return SuiteInventoryPublicationOutcome.Deferred;

                try
                {
                    var result = await PublishInventoryAttemptAsync(
                            context, inventory, inventoryHash, cancellationToken)
                        .ConfigureAwait(false);
                    _ = await _inventoryStateStore.TrySaveAsync(
                            cacheKey,
                            new SuiteInventoryPublicationState(
                                semanticStateHash,
                                inventoryHash,
                                UnsupportedUntil: null,
                                DateTimeOffset.FromUnixTimeSeconds(
                                    result.ServerTimeUnixSeconds)),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return SuiteInventoryPublicationOutcome.Published;
                }
                catch (SuiteInventoryUnsupportedException)
                {
                    _ = await _inventoryStateStore.TrySaveAsync(
                            cacheKey,
                            new SuiteInventoryPublicationState(
                                cached?.SemanticStateHash,
                                cached?.InventoryHash,
                                now + InventoryUnsupportedTtl,
                                cached?.AcceptedAt),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return SuiteInventoryPublicationOutcome.Unsupported;
                }
                catch (SuiteInventoryTransientException)
                    when (attempt < InventoryRetryDelays.Length)
                {
                    await Task.Delay(
                            InventoryRetryDelays[attempt],
                            _timeProvider,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SuiteInventoryTransientException)
                {
                    return SuiteInventoryPublicationOutcome.Deferred;
                }
                catch (Exception exception) when (exception is
                    SuiteApiException or SuiteLicensingUnavailableException
                    or SecurityException or CryptographicException
                    or InvalidDataException or ArgumentException)
                {
                    return SuiteInventoryPublicationOutcome.Rejected;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SuiteInventoryPublicationOutcome.Deferred;
        }
        catch (Exception exception) when (exception is
            ManagementException or COMException or IOException
            or UnauthorizedAccessException or PlatformNotSupportedException
            or NotSupportedException or SecurityException
            or CryptographicException or InvalidOperationException
            or ArgumentException or SuiteLicensingUnavailableException)
        {
            return SuiteInventoryPublicationOutcome.CollectionUnavailable;
        }

        return SuiteInventoryPublicationOutcome.Deferred;
    }

    private async Task<SuiteDeviceInventoryResultV1> PublishInventoryAttemptAsync(
        AuthorizedStoreContext context,
        SuiteMotherboardInventoryV1 inventory,
        string inventoryHash,
        CancellationToken cancellationToken)
    {
        var challengeRequest = new SuiteDeviceInventoryChallengeRequestV1(
            SuiteDeviceInventoryProtocol.SchemaVersion,
            SuiteDeviceInventoryProtocol.ProductId,
            context.LicenseId,
            context.DeviceId,
            context.SessionId,
            SuiteDeviceInventoryProtocol.Action,
            inventoryHash);
        var challenge = await PostInventoryAsync(
                SuiteDeviceInventoryProtocol.ChallengeRoute,
                SuiteDeviceInventoryProtocol.SerializeChallengeRequest(
                    challengeRequest),
                bytes => SuiteDeviceInventoryProtocol.ParseChallengeAssertion(
                    bytes,
                    _onlineAssertionSpki,
                    _onlineAssertionKeyId,
                    context.LicenseId,
                    context.DeviceId,
                    context.SessionId,
                    inventoryHash,
                    NowUnixSeconds()),
                cancellationToken)
            .ConfigureAwait(false);
        RegisterChallenge(challenge);

        var signature = UseMachineIdentity(() =>
            _identity.SignDeviceInventory(
                challenge,
                context.LicenseId,
                context.SessionId,
                inventoryHash));
        var proof = new SuiteDeviceInventoryProofV1(
            SuiteDeviceInventoryProtocol.SchemaVersion,
            SuiteDeviceInventoryProtocol.ProductId,
            context.LicenseId,
            context.DeviceId,
            context.SessionId,
            SuiteDeviceInventoryProtocol.Action,
            inventoryHash,
            challenge.ChallengeId,
            signature,
            inventory);
        return await PostInventoryAsync(
                SuiteDeviceInventoryProtocol.InventoryRoute,
                SuiteDeviceInventoryProtocol.SerializeProof(proof),
                bytes => SuiteDeviceInventoryProtocol.ParseResultAssertion(
                    bytes,
                    _onlineAssertionSpki,
                    _onlineAssertionKeyId,
                    context.LicenseId,
                    context.DeviceId,
                    context.SessionId,
                    inventoryHash,
                    challenge.ChallengeId,
                    NowUnixSeconds()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TResponse> PostInventoryAsync<TResponse>(
        string route,
        byte[] requestBytes,
        SuiteResponseParser<TResponse> parse,
        CancellationToken cancellationToken)
    {
        using var requestTimeout = new CancellationTokenSource(
            InventoryRequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, requestTimeout.Token);
        using var content = new ZeroingJsonContent(requestBytes);
        using var message = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = content
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new SuiteInventoryTransientException(exception);
        }
        catch (HttpRequestException exception)
        {
            throw new SuiteInventoryTransientException(exception);
        }

        using (response)
        {
            var status = response.StatusCode;
            if (status is HttpStatusCode.NotFound
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.NotImplemented)
                throw new SuiteInventoryUnsupportedException();
            if (status is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                || (int)status >= 500)
                throw new SuiteInventoryTransientException();

            if (!response.IsSuccessStatusCode)
            {
                if (status is HttpStatusCode.BadRequest
                    or HttpStatusCode.UnprocessableEntity)
                {
                    ValidateResponseHeaders(response);
                    var errorBytes = await ReadInventoryResponseAsync(
                            response.Content, linked.Token, cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        var error = SuiteOnlineLicenseProtocol.ParseErrorResponse(
                            errorBytes);
                        if (error.SchemaVersion ==
                                SuiteDeviceInventoryProtocol.SchemaVersion
                            && error.Code is "UNSUPPORTED_OPERATION"
                                or "UNSUPPORTED_SCHEMA_VERSION")
                            throw new SuiteInventoryUnsupportedException();
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(errorBytes);
                    }
                }

                throw new SuiteApiException(
                    (int)status,
                    "INVENTORY_DENIED",
                    "A autoridade recusou a operacao auxiliar de inventario.");
            }

            ValidateResponseHeaders(response);
            var responseBytes = await ReadInventoryResponseAsync(
                    response.Content, linked.Token, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                try { return parse(responseBytes); }
                catch (SecurityException exception)
                {
                    throw new SuiteApiException(
                        502,
                        "INVALID_INVENTORY_RESPONSE",
                        "A autoridade retornou uma resposta de inventario invalida.",
                        exception);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responseBytes);
            }
        }
    }

    private static async Task<byte[]> ReadInventoryResponseAsync(
        HttpContent content,
        CancellationToken requestCancellationToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            return await ReadBoundedAsync(
                    content,
                    SuiteDeviceInventoryProtocol.MaximumBodyBytes,
                    requestCancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!callerCancellationToken.IsCancellationRequested)
        {
            throw new SuiteInventoryTransientException(exception);
        }
        catch (Exception exception) when (
            !callerCancellationToken.IsCancellationRequested
            && exception is IOException or HttpRequestException)
        {
            throw new SuiteInventoryTransientException(exception);
        }
    }

    private static bool FixedHexEquals(string? left, string? right)
    {
        if (left is null || right is null
            || left.Length != 64 || right.Length != 64)
            return false;
        byte[]? leftBytes = null;
        byte[]? rightBytes = null;
        try
        {
            leftBytes = Convert.FromHexString(left);
            rightBytes = Convert.FromHexString(right);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (leftBytes is not null)
                CryptographicOperations.ZeroMemory(leftBytes);
            if (rightBytes is not null)
                CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private sealed class SuiteInventoryUnsupportedException : Exception
    {
    }

    private sealed class SuiteInventoryTransientException : Exception
    {
        public SuiteInventoryTransientException()
        {
        }

        public SuiteInventoryTransientException(Exception innerException)
            : base("Falha transitoria no inventario auxiliar.", innerException)
        {
        }
    }
}
