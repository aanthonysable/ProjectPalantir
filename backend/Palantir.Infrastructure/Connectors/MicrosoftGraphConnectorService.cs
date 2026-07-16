using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palantir.Application.Abstractions;
using Palantir.Application.Audit;
using Palantir.Application.Connectors;
using Palantir.Domain.Entities;
using Palantir.Domain.Enums;

namespace Palantir.Infrastructure.Connectors;

public sealed class MicrosoftGraphConnectorService : IMicrosoftGraphConnectorService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IPalantirDbContext _db;
    private readonly IAuditEventWriter _audit;
    private readonly IConnectorCredentialStore _credentials;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly MicrosoftGraphOptions _options;
    private readonly ILogger<MicrosoftGraphConnectorService> _logger;

    public MicrosoftGraphConnectorService(
        IPalantirDbContext db,
        IAuditEventWriter audit,
        IConnectorCredentialStore credentials,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<MicrosoftGraphOptions> options,
        ILogger<MicrosoftGraphConnectorService> logger)
    {
        _db = db;
        _audit = audit;
        _credentials = credentials;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public Task<AuthorizeMicrosoftResult> BeginAuthorizeAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var verifier = GenerateCodeVerifier();
        var challenge = CreateCodeChallenge(verifier);

        _cache.Set(
            CacheKey(state),
            new OAuthPendingState(userId, organizationId, verifier),
            TimeSpan.FromMinutes(15));

        var query = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = _options.RedirectUri,
            ["response_mode"] = "query",
            ["scope"] = string.Join(' ', _options.Scopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account"
        };

        var queryString = string.Join('&', query.Select(kv =>
            $"{WebUtility.UrlEncode(kv.Key)}={WebUtility.UrlEncode(kv.Value)}"));

        var url =
            $"https://login.microsoftonline.com/{_options.AuthorityTenant}/oauth2/v2.0/authorize?{queryString}";

        return Task.FromResult(new AuthorizeMicrosoftResult(url, state));
    }

    public async Task<ConnectedAccountDto> CompleteAuthorizeAsync(
        string code,
        string state,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        if (!_cache.TryGetValue(CacheKey(state), out OAuthPendingState? pending) || pending is null)
        {
            throw new InvalidOperationException("OAuth state is invalid or expired. Start Connect Outlook again.");
        }

        _cache.Remove(CacheKey(state));

        var token = await ExchangeCodeAsync(code, pending.CodeVerifier, cancellationToken);
        var profile = await GetGraphProfileAsync(token.AccessToken, cancellationToken);

        var account = _db.ConnectedAccounts
            .FirstOrDefault(a =>
                a.UserId == pending.UserId &&
                a.Provider == "MicrosoftGraph" &&
                a.ProviderAccountId == profile.Id);

        if (account is null)
        {
            account = new ConnectedAccount
            {
                UserId = pending.UserId,
                Provider = "MicrosoftGraph",
                ProviderTenantId = _options.TenantId,
                ProviderAccountId = profile.Id,
                DisplayName = profile.DisplayName,
                PrimaryAddress = profile.Mail ?? profile.UserPrincipalName,
                ConnectionStatus = ConnectionStatus.Connected,
                GrantedScopesJson = JsonSerializer.Serialize(_options.Scopes),
                LastSuccessfulSyncAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _db.Add(account);
        }
        else
        {
            account.DisplayName = profile.DisplayName;
            account.PrimaryAddress = profile.Mail ?? profile.UserPrincipalName;
            account.ConnectionStatus = ConnectionStatus.Connected;
            account.GrantedScopesJson = JsonSerializer.Serialize(_options.Scopes);
            account.LastSuccessfulSyncAt = DateTimeOffset.UtcNow;
            account.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);

        var payload = JsonSerializer.Serialize(new StoredTokenPayload(
            token.AccessToken,
            token.RefreshToken,
            token.ExpiresIn <= 0
                ? DateTimeOffset.UtcNow.AddHours(1)
                : DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
            token.Scope));

        var existingGrant = _db.OAuthGrants
            .Where(g => g.ConnectedAccountId == account.Id && g.RevokedAt == null)
            .ToList()
            .OrderByDescending(g => g.CreatedAt)
            .FirstOrDefault();

        if (existingGrant is not null)
        {
            existingGrant.RevokedAt = DateTimeOffset.UtcNow;
            existingGrant.UpdatedAt = DateTimeOffset.UtcNow;
        }

        _db.Add(new OAuthGrant
        {
            ConnectedAccountId = account.Id,
            CredentialReference = _credentials.Protect(payload),
            TokenVersion = (existingGrant?.TokenVersion ?? 0) + 1,
            ExpiresAt = token.ExpiresIn <= 0
                ? DateTimeOffset.UtcNow.AddHours(1)
                : DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);

        await _audit.WriteAsync(
            pending.OrganizationId,
            "connected_account.microsoft.connected",
            pending.UserId,
            nameof(ConnectedAccount),
            account.Id,
            JsonSerializer.Serialize(new { account.PrimaryAddress, account.DisplayName }),
            cancellationToken);

        return Map(account);
    }

    public Task<IReadOnlyList<ConnectedAccountDto>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var items = _db.ConnectedAccounts
            .Where(a => a.UserId == userId && a.Provider == "MicrosoftGraph")
            .ToList()
            .OrderByDescending(a => a.UpdatedAt)
            .Select(Map)
            .ToList();

        return Task.FromResult<IReadOnlyList<ConnectedAccountDto>>(items);
    }

    public Task<ConnectedAccountDto?> GetAsync(Guid connectedAccountId, CancellationToken cancellationToken = default)
    {
        var account = _db.ConnectedAccounts.FirstOrDefault(a => a.Id == connectedAccountId);
        return Task.FromResult(account is null ? null : Map(account));
    }

    public async Task DisconnectAsync(Guid connectedAccountId, Guid userId, CancellationToken cancellationToken = default)
    {
        var account = _db.ConnectedAccounts.FirstOrDefault(a => a.Id == connectedAccountId && a.UserId == userId)
            ?? throw new InvalidOperationException("Connected account was not found.");

        account.ConnectionStatus = ConnectionStatus.Revoked;
        account.UpdatedAt = DateTimeOffset.UtcNow;

        foreach (var grant in _db.OAuthGrants.Where(g => g.ConnectedAccountId == account.Id && g.RevokedAt == null).ToList())
        {
            grant.RevokedAt = DateTimeOffset.UtcNow;
            grant.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);

        var orgId = _db.Users.Where(u => u.Id == userId).Select(u => u.OrganizationId).FirstOrDefault();
        await _audit.WriteAsync(
            orgId,
            "connected_account.microsoft.disconnected",
            userId,
            nameof(ConnectedAccount),
            account.Id,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<OutlookMessageDto>> ListMailAsync(
        Guid connectedAccountId,
        Guid userId,
        int top = 20,
        CancellationToken cancellationToken = default)
    {
        var account = _db.ConnectedAccounts.FirstOrDefault(a => a.Id == connectedAccountId && a.UserId == userId)
            ?? throw new InvalidOperationException("Connected account was not found.");

        if (account.ConnectionStatus != ConnectionStatus.Connected)
        {
            throw new InvalidOperationException($"Mailbox connection status is '{account.ConnectionStatus}'.");
        }

        var accessToken = await GetValidAccessTokenAsync(account, cancellationToken);
        var client = _httpClientFactory.CreateClient("microsoft-graph");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/me/messages?$top={Math.Clamp(top, 1, 50)}&$select=id,subject,from,bodyPreview,body,receivedDateTime,isRead,conversationId&$orderby=receivedDateTime desc");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            MapGraphFailure(account, body);
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException($"Microsoft Graph mail read failed: {(int)response.StatusCode}");
        }

        var parsed = JsonSerializer.Deserialize<GraphMessageList>(body, JsonOptions)
            ?? new GraphMessageList();

        account.LastSuccessfulSyncAt = DateTimeOffset.UtcNow;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return (parsed.Value ?? [])
            .Select(m => new OutlookMessageDto(
                m.Id ?? string.Empty,
                m.Subject,
                m.From?.EmailAddress?.Address,
                m.BodyPreview,
                m.ReceivedDateTime,
                m.IsRead ?? false,
                m.ConversationId,
                NormalizeGraphBody(m.Body)))
            .ToList();
    }

    public async Task SendMailAsync(
        Guid connectedAccountId,
        Guid userId,
        string toAddress,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        var account = _db.ConnectedAccounts.FirstOrDefault(a => a.Id == connectedAccountId && a.UserId == userId)
            ?? throw new InvalidOperationException("Connected account was not found.");

        if (account.ConnectionStatus != ConnectionStatus.Connected)
        {
            throw new InvalidOperationException($"Mailbox connection status is '{account.ConnectionStatus}'.");
        }

        if (string.IsNullOrWhiteSpace(account.GrantedScopesJson) ||
            !account.GrantedScopesJson.Contains("Mail.Send", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Mail.Send is not granted yet. Disconnect and Connect Outlook again after adding the Mail.Send permission in Azure.");
        }

        if (string.IsNullOrWhiteSpace(toAddress))
        {
            throw new InvalidOperationException("A recipient address is required to send mail.");
        }

        var accessToken = await GetValidAccessTokenAsync(account, cancellationToken);
        var client = _httpClientFactory.CreateClient("microsoft-graph");
        var payload = new
        {
            message = new
            {
                subject,
                body = new { contentType = "Text", content = body },
                toRecipients = new[]
                {
                    new { emailAddress = new { address = toAddress } }
                }
            },
            saveToSentItems = true
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/sendMail")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            MapGraphFailure(account, responseBody);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("SendMail failed: {Body}", responseBody);
            throw new InvalidOperationException(
                $"Microsoft Graph send failed: {(int)response.StatusCode}. {DescribeTokenError(responseBody)}");
        }

        account.LastSuccessfulSyncAt = DateTimeOffset.UtcNow;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> GetValidAccessTokenAsync(ConnectedAccount account, CancellationToken cancellationToken)
    {
        var grant = _db.OAuthGrants
            .Where(g => g.ConnectedAccountId == account.Id && g.RevokedAt == null)
            .ToList()
            .OrderByDescending(g => g.CreatedAt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No OAuth grant is available. Reconnect Outlook.");

        var payload = JsonSerializer.Deserialize<StoredTokenPayload>(
            _credentials.Unprotect(grant.CredentialReference),
            JsonOptions)
            ?? throw new InvalidOperationException("Stored OAuth grant is invalid.");

        if (payload.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return payload.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(payload.RefreshToken))
        {
            account.ConnectionStatus = ConnectionStatus.ReauthorizationRequired;
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Access token expired. Reconnect Outlook.");
        }

        var refreshed = await RefreshTokenAsync(payload.RefreshToken, cancellationToken);
        var updated = new StoredTokenPayload(
            refreshed.AccessToken,
            refreshed.RefreshToken ?? payload.RefreshToken,
            refreshed.ExpiresIn <= 0
                ? DateTimeOffset.UtcNow.AddHours(1)
                : DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn),
            refreshed.Scope ?? payload.Scope);

        grant.CredentialReference = _credentials.Protect(JsonSerializer.Serialize(updated));
        grant.ExpiresAt = updated.ExpiresAt;
        grant.UpdatedAt = DateTimeOffset.UtcNow;
        grant.TokenVersion += 1;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return updated.AccessToken;
    }

    private async Task<TokenResponse> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("microsoft-graph");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = string.Join(' ', _options.Scopes)
        });

        using var response = await client.PostAsync(
            $"https://login.microsoftonline.com/{_options.AuthorityTenant}/oauth2/v2.0/token",
            content,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Token exchange failed: {Body}", body);
            throw new InvalidOperationException(DescribeTokenError(body));
        }

        return JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Token response was empty.");
    }

    private async Task<TokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("microsoft-graph");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = string.Join(' ', _options.Scopes)
        });

        using var response = await client.PostAsync(
            $"https://login.microsoftonline.com/{_options.AuthorityTenant}/oauth2/v2.0/token",
            content,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Refresh token failed: {Body}", body);
            throw new InvalidOperationException(DescribeTokenError(body));
        }

        return JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Refresh response was empty.");
    }

    private async Task<GraphProfile> GetGraphProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("microsoft-graph");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to read Microsoft profile: {(int)response.StatusCode}");
        }

        var profile = JsonSerializer.Deserialize<GraphProfile>(body, JsonOptions)
            ?? throw new InvalidOperationException("Microsoft profile response was empty.");

        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            throw new InvalidOperationException("Microsoft profile did not include an id.");
        }

        return profile;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId) ||
            string.IsNullOrWhiteSpace(_options.ClientSecret) ||
            string.IsNullOrWhiteSpace(_options.RedirectUri))
        {
            throw new InvalidOperationException(
                "Microsoft Graph connector is not configured. Set ClientId, ClientSecret, and RedirectUri.");
        }

        // Azure portal shows Secret ID (a GUID) and Value (a longer random string).
        // Using the ID produces AADSTS7000215 and the connection never saves.
        if (_options.ClientSecret.Length == 36 &&
            _options.ClientSecret.Count(c => c == '-') == 4 &&
            Guid.TryParse(_options.ClientSecret, out _))
        {
            throw new InvalidOperationException(
                "Connectors:MicrosoftGraph:ClientSecret looks like a Secret ID (GUID). " +
                "In Azure → Certificates & secrets, copy the Value column (not Secret ID), then run: " +
                "dotnet user-secrets set \"Connectors:MicrosoftGraph:ClientSecret\" \"<VALUE>\" " +
                "and restart the API.");
        }
    }

    private static void MapGraphFailure(ConnectedAccount account, string body)
    {
        if (body.Contains("AADSTS65001", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("admin consent", StringComparison.OrdinalIgnoreCase))
        {
            account.ConnectionStatus = ConnectionStatus.AdminConsentRequired;
        }
        else if (body.Contains("AADSTS50105", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("blocked", StringComparison.OrdinalIgnoreCase))
        {
            account.ConnectionStatus = ConnectionStatus.PolicyBlocked;
        }
        else if (body.Contains("InvalidAuthenticationToken", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("LifetimeError", StringComparison.OrdinalIgnoreCase))
        {
            account.ConnectionStatus = ConnectionStatus.ReauthorizationRequired;
        }
        else
        {
            account.ConnectionStatus = ConnectionStatus.Error;
        }

        account.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string DescribeTokenError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            var description = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                return error;
            }
        }
        catch
        {
            // fall through
        }

        return "Microsoft token request failed.";
    }

    private static string CacheKey(string state) => $"ms-oauth:{state}";

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ConnectedAccountDto Map(ConnectedAccount a) =>
        new(a.Id, a.UserId, a.Provider, a.DisplayName, a.PrimaryAddress, a.ConnectionStatus,
            a.GrantedScopesJson, a.LastSuccessfulSyncAt, a.UpdatedAt);

    private sealed record OAuthPendingState(Guid UserId, Guid OrganizationId, string CodeVerifier);

    private sealed record StoredTokenPayload(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset ExpiresAt,
        string? Scope);

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }

    private sealed class GraphProfile
    {
        public string Id { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string? Mail { get; set; }
        public string? UserPrincipalName { get; set; }
    }

    private sealed class GraphMessageList
    {
        public List<GraphMessage>? Value { get; set; }
    }

    private sealed class GraphMessage
    {
        public string? Id { get; set; }
        public string? Subject { get; set; }
        public GraphFrom? From { get; set; }
        public string? BodyPreview { get; set; }
        public GraphItemBody? Body { get; set; }
        public DateTimeOffset? ReceivedDateTime { get; set; }
        public bool? IsRead { get; set; }
        public string? ConversationId { get; set; }
    }

    private sealed class GraphItemBody
    {
        public string? ContentType { get; set; }
        public string? Content { get; set; }
    }

    private sealed class GraphFrom
    {
        public GraphEmailAddress? EmailAddress { get; set; }
    }

    private sealed class GraphEmailAddress
    {
        public string? Address { get; set; }
    }

    private static string? NormalizeGraphBody(GraphItemBody? body)
    {
        if (body?.Content is null || string.IsNullOrWhiteSpace(body.Content))
        {
            return null;
        }

        if (string.Equals(body.ContentType, "text", StringComparison.OrdinalIgnoreCase))
        {
            return body.Content.Trim();
        }

        return HtmlToPlainText(body.Content);
    }

    private static string HtmlToPlainText(string html)
    {
        var text = System.Net.WebUtility.HtmlDecode(html);
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"<(script|style)[^>]*>[\s\S]*?</\1>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"</p\s*>", "\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"</div\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+\n", "\n");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}
