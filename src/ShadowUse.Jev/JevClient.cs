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

    public async Task<JevRawResponse> PostAsync(object requestBody, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new JevUnavailableException($"Jev is enabled but {options.ApiKeyEnvironmentVariable} is not set; no Jev call was made.");

        var json = JsonSerializer.Serialize(requestBody, WireJson);
        using var timeoutCts = new CancellationTokenSource(options.TimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        Exception? last = null;
        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
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
                last = ex;
                if (attempt < options.MaxRetries && !ct.IsCancellationRequested)
                {
                    await Task.Delay(RetryDelay(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                throw new JevUnavailableException("Jev model connection failed; no action executed.", ex);
            }

            var status = (int)response.StatusCode;
            if (status is 429 or 503 or 529 && attempt < options.MaxRetries)
            {
                response.Dispose();
                await Task.Delay(RetryDelay(attempt), ct).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new JevUnavailableException($"Jev model provider returned HTTP {status}; no action executed.");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            return new JevRawResponse(body);
        }

        throw new JevUnavailableException("Jev model unavailable.", last);
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
}
