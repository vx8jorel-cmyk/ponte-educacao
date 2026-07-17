namespace Ponte.Server;

public sealed record InstagramConnection(string UserId, string Username, string DisplayName, string ProtectedAccessToken,
    DateTimeOffset ConnectedAt, string? ProfilePictureUrl = null, DateTimeOffset? ProfileSyncedAt = null);
public sealed record TikTokConnection(string OpenId, string DisplayName, string AvatarUrl, string ProtectedAccessToken, string ProtectedRefreshToken, DateTimeOffset ExpiresAt, DateTimeOffset ConnectedAt);
public sealed record YouTubeConnection(string ChannelId, string Title, string ThumbnailUrl, string ProtectedAccessToken,
    string ProtectedRefreshToken, DateTimeOffset ExpiresAt, DateTimeOffset ConnectedAt);

public sealed class ScheduledPost
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Platform { get; init; } = "instagram";
    public string AccountId { get; init; } = "";
    public string Type { get; init; } = "IMAGE";
    public string Caption { get; set; } = "";
    public string Title { get; set; } = "";
    public string OriginalFileName { get; init; } = "";
    public string SourceMediaPath { get; init; } = "";
    public string MediaPath { get; set; } = "";
    public string AiStatus { get; set; } = "disabled";
    public double? AiConfidence { get; set; }
    public string? AiEvidence { get; set; }
    public string BrandingStatus { get; set; } = "pending";
    public string? BrandingSignature { get; set; }
    public DateTimeOffset PublishAt { get; init; }
    public string Status { get; set; } = "scheduled";
    public string? InstagramMediaId { get; set; }
    public string? Error { get; set; }
    public int PublishAttempts { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record EngagementSummary(long Reach, long Views, long Likes, long Comments, long Shares, long Saved, long TotalInteractions, int Publications, IReadOnlyList<MediaInsight> Media);
public sealed record MediaInsight(string Id, string Type, string Caption, string? Permalink, DateTimeOffset? Timestamp, long Reach, long Views, long Likes, long Comments, long Shares, long Saved, long TotalInteractions);

public sealed class MetaOptions
{
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string GraphVersion { get; set; } = "v23.0";
    public string PublicBaseUrl { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret) && Uri.TryCreate(RedirectUri, UriKind.Absolute, out _);
}

public sealed class TikTokOptions
{
    public string ClientKey { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientKey) && !string.IsNullOrWhiteSpace(ClientSecret) && Uri.TryCreate(RedirectUri, UriKind.Absolute, out _);
}

public sealed class YouTubeOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && Uri.TryCreate(RedirectUri, UriKind.Absolute, out _);
}
