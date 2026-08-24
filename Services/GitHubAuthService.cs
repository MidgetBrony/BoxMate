using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BoxMate.Services;

public sealed record GitHubDevicePrompt(string UserCode, string VerificationUri);

public sealed class GitHubAuthService
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoxMate", "github-token.dat");

    public string ClientId => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "GitHubClientId")?.Value?.Trim() ?? string.Empty;

    public string? LoadToken()
    {
        if (!File.Exists(TokenPath)) return null;
        try
        {
            if (!OperatingSystem.IsWindows()) return File.ReadAllText(TokenPath).Trim();
            var encrypted = File.ReadAllBytes(TokenPath);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }

    public async Task<string> SignInAsync(Action<GitHubDevicePrompt> showPrompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ClientId))
            throw new InvalidOperationException("This BoxMate build has no GitHub OAuth client ID. Re-publish it with -p:GitHubClientId=YOUR_CLIENT_ID.");

        using var deviceRequest = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        {
            Content = new FormUrlEncodedContent([new("client_id", ClientId)])
        };
        using var deviceResponse = await Client.SendAsync(deviceRequest, cancellationToken);
        deviceResponse.EnsureSuccessStatusCode();
        var device = JsonSerializer.Deserialize<DeviceCodeResponse>(await deviceResponse.Content.ReadAsStringAsync(cancellationToken))
            ?? throw new InvalidOperationException("GitHub returned an empty device-login response.");
        showPrompt(new GitHubDevicePrompt(device.UserCode, device.VerificationUri));
        Process.Start(new ProcessStartInfo(device.VerificationUri) { UseShellExecute = true });

        var interval = Math.Max(5, device.Interval);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent([
                    new("client_id", ClientId), new("device_code", device.DeviceCode),
                    new("grant_type", "urn:ietf:params:oauth:grant-type:device_code")])
            };
            using var tokenResponse = await Client.SendAsync(tokenRequest, cancellationToken);
            tokenResponse.EnsureSuccessStatusCode();
            var result = JsonSerializer.Deserialize<TokenResponse>(await tokenResponse.Content.ReadAsStringAsync(cancellationToken))
                ?? throw new InvalidOperationException("GitHub returned an empty sign-in response.");
            if (!string.IsNullOrWhiteSpace(result.AccessToken))
            {
                SaveToken(result.AccessToken);
                return result.AccessToken;
            }
            if (result.Error == "authorization_pending") continue;
            if (result.Error == "slow_down") { interval += 5; continue; }
            if (result.Error == "access_denied") throw new InvalidOperationException("GitHub sign-in was cancelled.");
            if (result.Error == "expired_token") break;
            throw new InvalidOperationException(result.ErrorDescription ?? $"GitHub sign-in failed: {result.Error}.");
        }
        throw new InvalidOperationException("The GitHub sign-in code expired. Try again.");
    }

    public void SignOut()
    {
        if (File.Exists(TokenPath)) File.Delete(TokenPath);
    }

    private static void SaveToken(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        if (OperatingSystem.IsWindows())
        {
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenPath, encrypted);
            return;
        }
        File.WriteAllText(TokenPath, token);
        File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BoxMate", "1.0"));
        return client;
    }

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = string.Empty;
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = string.Empty;
        [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }
}
