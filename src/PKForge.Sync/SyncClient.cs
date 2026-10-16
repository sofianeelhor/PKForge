using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PKForge.Sync.Protocol;

namespace PKForge.Sync;

/// <summary>An error answer from the sync server (<see cref="Code"/> is one of <see cref="ErrorCodes"/>).</summary>
public sealed class SyncApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// The sync server's HTTP API. The <see cref="HttpClient"/>'s base address is the server address
/// (ending in "/"); every call names the protocol version and, once signed in, the device key.
/// </summary>
public sealed class SyncClient(HttpClient http)
{
    public string? DeviceKey { get; set; }

    public Task<ServerInfo> GetServerInfoAsync(CancellationToken ct = default) => GetAsync<ServerInfo>("v1/server-info", ct);
    public Task<DeviceKeyResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default) => PostAsync<DeviceKeyResponse>("v1/accounts", request, ct);
    public Task<DeviceKeyResponse> LoginAsync(LoginRequest request, CancellationToken ct = default) => PostAsync<DeviceKeyResponse>("v1/auth/login", request, ct);
    public Task<DeviceKeyResponse> RedeemPairingAsync(RedeemPairingRequest request, CancellationToken ct = default) => PostAsync<DeviceKeyResponse>("v1/pairing/redeem", request, ct);
    public Task<PairingCodeResponse> CreatePairingCodeAsync(CancellationToken ct = default) => PostAsync<PairingCodeResponse>("v1/pairing", null, ct);
    public Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct = default) => GetAsync<List<DeviceInfo>>("v1/devices", ct);
    public Task RemoveDeviceAsync(Guid id, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"v1/devices/{id}", null, ct);
    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "v1/auth/password", request, ct);
    public Task RequestPasswordResetAsync(string email, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "v1/auth/password-reset", new PasswordResetRequest(email), ct);
    public Task ResendVerificationAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "v1/auth/resend-verification", null, ct);
    public Task DeleteAccountAsync(string password, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, "v1/account", new DeleteAccountRequest(password), ct);

    public async Task<IReadOnlyList<string>> MissingBlobsAsync(IReadOnlyList<string> hashes, CancellationToken ct = default) =>
        (await PostAsync<MissingBlobsResponse>("v1/blobs/missing", new MissingBlobsRequest(hashes), ct).ConfigureAwait(false)).Missing;

    public Task UploadBlobsAsync(IReadOnlyList<BlobData> blobs, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "v1/blobs/upload", new UploadBlobsRequest(blobs), ct);

    public async Task<IReadOnlyList<BlobData>> DownloadBlobsAsync(IReadOnlyList<string> hashes, CancellationToken ct = default) =>
        (await PostAsync<DownloadBlobsResponse>("v1/blobs/download", new DownloadBlobsRequest(hashes), ct).ConfigureAwait(false)).Blobs;

    public Task<PushResponse> PushAsync(PushRequest request, CancellationToken ct = default) => PostAsync<PushResponse>("v1/sync/push", request, ct);

    public Task<ChangesResponse> GetChangesAsync(long since, string? epoch, int limit, CancellationToken ct = default) =>
        GetAsync<ChangesResponse>(
            $"v1/sync/changes?since={since.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}"
            + (epoch is null ? "" : "&epoch=" + Uri.EscapeDataString(epoch)), ct);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task<T> PostAsync<T>(string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Post, path, body, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(SyncProtocol.VersionHeader, SyncProtocol.Version.ToString(CultureInfo.InvariantCulture));
        if (DeviceKey is { } key) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: ProtocolJson.Options);

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        using (response) throw await ErrorAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(ProtocolJson.Options, ct).ConfigureAwait(false)
        ?? throw new SyncApiException((int)response.StatusCode, "empty_response", "The server sent an empty answer.");

    private static async Task<SyncApiException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        try
        {
            if (await response.Content.ReadFromJsonAsync<ErrorResponse>(ProtocolJson.Options, ct).ConfigureAwait(false) is { Code: not null } error)
                return new SyncApiException(status, error.Code, error.Message);
        }
        catch (JsonException)
        {
            // Not one of our error bodies (a proxy's page, say).
        }
        return new SyncApiException(status, $"http_{status}", $"The server answered {status} {response.ReasonPhrase}.");
    }
}
