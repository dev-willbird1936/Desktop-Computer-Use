// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ShadowUse.Jev;

internal sealed record JevRawResponse(string Body);

/// <summary>
/// Raw TypeSafe/Jev transport: one POST per decision, bounded retries on
/// throttling, and a hard mapping of every failure mode to
/// <see cref="JevUnavailableException"/> so callers have one exception type to
/// fall back on. Never a single point of failure: any thrown exception here
/// must leave the caller free to keep using DCU without Jev.
/// </summary>
internal sealed class JevClient(HttpClient httpClient, JevOptions options)
{
    private static readonly JsonSerializerOptions WireJson = new() { WriteIndented = false };

    public async Task<JevRawResponse> PostAsync(object requestBody, CancellationToken ct, int? maxRetriesOverride = null)
    {
        int maxRetries = maxRetriesOverride ?? options.MaxRetries;
        if (maxRetries is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maxRetriesOverride), "Jev retry override must be between 0 and 10.");
        var apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new JevUnavailableException($"Jev is enabled but {options.ApiKeyEnvironmentVariable} is not set; no Jev call was made.");

        var json = JsonSerializer.Serialize(requestBody, WireJson);
        using var timeoutCts = new CancellationTokenSource(options.TimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Exception? last = null;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                last = ex;
                if (attempt < maxRetries && !ct.IsCancellationRequested)
                {
                    await Task.Delay(RetryDelay(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                throw new JevUnavailableException("Jev model connection failed; no action executed.", ex);
            }

            var status = (int)response.StatusCode;
            if (status is 429 or 503 or 529 && attempt < maxRetries)
            {
                response.Dispose();
                await Task.Delay(RetryDelay(attempt), ct).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                string? providerDetail = null;
                try
                {
                    providerDetail = await ReadProviderErrorDetailAsync(response.Content, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                response.Dispose();
                ct.ThrowIfCancellationRequested();
                string detail = providerDetail is null ? "" : $" Provider detail: {providerDetail}.";
                throw new JevUnavailableException($"Jev model provider returned HTTP {status}; no action executed.{detail}");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            return new JevRawResponse(body);
        }

        throw new JevUnavailableException("Jev model unavailable.", last);
    }

    private static async Task<string?> ReadProviderErrorDetailAsync(HttpContent content, CancellationToken ct)
    {
        const int maxBytes = 4096;
        var buffer = new byte[maxBytes];
        int total = 0;
        try
        {
            await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            if (total == 0) return null;

            using var document = JsonDocument.Parse(buffer.AsMemory(0, total));
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return CleanProviderDetail(error.GetString());
                if (error.ValueKind == JsonValueKind.Object)
                {
                    foreach (string key in new[] { "message", "detail", "error_description" })
                        if (error.TryGetProperty(key, out var errorMessage) && errorMessage.ValueKind == JsonValueKind.String)
                            return CleanProviderDetail(errorMessage.GetString());
                }
            }
            foreach (string key in new[] { "message", "detail", "error_description" })
                if (root.TryGetProperty(key, out var message) && message.ValueKind == JsonValueKind.String)
                    return CleanProviderDetail(message.GetString());
            return null;
        }
        catch (JsonException) { return CleanProviderDetail(Encoding.UTF8.GetString(buffer, 0, total)); }
        catch (IOException) { return null; }
    }

    private static string? CleanProviderDetail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string clean = new string(value.Where(character => !char.IsControl(character)).Take(300).ToArray()).Trim();
        return clean.Length == 0 ? null : clean;
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
}
