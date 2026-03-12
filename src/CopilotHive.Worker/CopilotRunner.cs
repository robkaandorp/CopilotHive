using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotHive.Worker;

/// <summary>
/// Communicates with the Copilot CLI running in headless mode via JSON-RPC over HTTP.
/// The Copilot CLI is already running (started by entrypoint.sh); this class sends prompts to it.
/// </summary>
public sealed class CopilotRunner(int port = 8000) : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri($"http://localhost:{port}"),
        Timeout = TimeSpan.FromMinutes(10),
    };

    private int _requestId;

    /// <summary>
    /// Send a prompt to the Copilot CLI and return the response text.
    /// </summary>
    public async Task<string> SendPromptAsync(string prompt, string workDir, CancellationToken ct)
    {
        var requestId = Interlocked.Increment(ref _requestId);

        var payload = new JsonRpcRequest
        {
            Id = requestId,
            Method = "sendMessage",
            Params = new JsonRpcParams
            {
                Message = prompt,
                WorkingDirectory = workDir,
            },
        };

        Console.WriteLine($"[Copilot] Sending prompt ({prompt.Length} chars) to localhost:{port}");

        using var response = await _httpClient.PostAsJsonAsync("/", payload, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new CopilotRunnerException(
                $"Copilot returned HTTP {(int)response.StatusCode}: {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonRpcResponse>(ct);

        if (result?.Error is not null)
        {
            throw new CopilotRunnerException(
                $"Copilot JSON-RPC error ({result.Error.Code}): {result.Error.Message}");
        }

        var responseText = result?.Result?.Content ?? "";
        Console.WriteLine($"[Copilot] Received response ({responseText.Length} chars)");

        return responseText;
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed class CopilotRunnerException(string message) : Exception(message);

// JSON-RPC request/response models

internal sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public required JsonRpcParams Params { get; init; }
}

internal sealed class JsonRpcParams
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; init; }
}

internal sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string? JsonRpc { get; init; }

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("result")]
    public JsonRpcResult? Result { get; init; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; init; }
}

internal sealed class JsonRpcResult
{
    [JsonPropertyName("content")]
    public string? Content { get; init; }
}

internal sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
