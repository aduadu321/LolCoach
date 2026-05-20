using System.Net.Http;
using System.Net.Security;
using System.Text.Json;
using LolCoach.Models;

namespace LolCoach.Services;

public class LiveClientService : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LiveClientService()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(3),
            BaseAddress = new Uri("https://127.0.0.1:2999/")
        };
    }

    public async Task<LiveGameData?> GetAllGameDataAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("liveclientdata/allgamedata", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<LiveGameData>(stream, JsonOpts, ct);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _http.Dispose();
}
