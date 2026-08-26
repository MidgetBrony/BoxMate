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
public sealed record GitHubCredential(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt);

public sealed class GitHubAuthService
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoxMate", "github-token.dat");

    public string ClientId => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "GitHubClientId")?.Value?.Trim() ?? string.Empty;

    public string? LoadToken()
        => LoadCredential()?.AccessToken;

    public GitHubCredential? LoadCredential()
    {
        if (!File.Exists(TokenPath)) return null;
        try
        {
            string stored;
            if (!OperatingSystem.IsWindows()) stored = File.ReadAllText(TokenPath).Trim();
            else
            {
                var encrypted = File.ReadAllBytes(TokenPath);
                stored = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
            }

            if (string.IsNullOrWhiteSpace(stored)) return null;
            if (stored.StartsWith('{'))
                return JsonSerializer.Deserialize<GitHubCredential>(stored);

            // Compatibility with BoxMate 1.3.2 and earlier, which stored only
            // a long-lived access token.
            return new GitHubCredential(stored, null, null);
        }
        catch { return null; }
    }

    public async Task<string?> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var credential = LoadCredential();
        if (credential is null) return null;
        if (credential.ExpiresAt is null || credential.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
            return credential.AccessToken;
        return await RefreshAccessTokenAsync(cancellationToken);
    }

    public async Task<string?> RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var credential = LoadCredential();
        if (credential is null || string.IsNullOrWhiteSpace(credential.RefreshToken)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent([
                new("client_id", ClientId),
                new("grant_type", "refresh_token"),
                new("refresh_token", credential.RefreshToken)])
        };
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<TokenResponse>(await response.Content.ReadAsStringAsync(cancellationToken))
            ?? throw new InvalidOperationException("GitHub returned an empty token-refresh response.");
        if (string.IsNullOrWhiteSpace(result.AccessToken))
        {
            if (result.Error is "bad_refresh_token" or "incorrect_client_credentials")
            {
                SignOut();
                return null;
            }
            throw new InvalidOperationException(result.ErrorDescription ?? $"GitHub token refresh failed: {result.Error}.");
        }

        var refreshed = CreateCredential(result);
        SaveCredential(refreshed);
        return refreshed.AccessToken;
    }

    public async Task<string> SignInAsync(Action<GitHubDevicePrompt> showPrompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ClientId))
            throw new InvalidOperationException("This BoxMate build has no GitHub OAuth client ID. Re-publish it with -p:GitHubClientId=YOUR_CLIENT_ID.");

        using var deviceRequest = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        {
            Content = new FormUrlEncodedContent([
                new("client_id", ClientId),
                new("scope", "offline_access")])
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
                SaveCredential(CreateCredential(result));
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

    private static GitHubCredential CreateCredential(TokenResponse response) => new(
        response.AccessToken!,
        response.RefreshToken,
        response.ExpiresIn is > 0 ? DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn.Value) : null);

    private static void SaveCredential(GitHubCredential credential)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        var serialized = JsonSerializer.Serialize(credential);
        if (OperatingSystem.IsWindows())
        {
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(serialized), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenPath, encrypted);
            return;
        }
        File.WriteAllText(TokenPath, serialized);
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
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
        [JsonPropertyName("refresh_token_expires_in")] public int? RefreshTokenExpiresIn { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }
}
