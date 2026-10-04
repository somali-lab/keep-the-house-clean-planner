namespace Huishoudplanner.Domain.Badges;

/// <summary>The rule as a caller sends it: <see cref="TaskIds"/> is ignored for an on-time-weeks rule. Values are checked by <see cref="BadgeValidation"/>.</summary>
public sealed record BadgeRuleInput(BadgeRuleType Type, IReadOnlyList<string> TaskIds, int Threshold);

/// <summary>An uploaded image: the declared content type and the bytes as base64 text.</summary>
public sealed record BadgeImageInput(string ContentType, string Data);

/// <summary>What the caller asks for when creating a badge. A missing description is empty, a missing active flag is true.</summary>
public sealed record CreateBadgeCommand(string Name, string? Description, BadgeRuleInput Rule, bool? Active, BadgeImageInput? Image);

/// <summary>A change of the picture: a new image, or <see langword="null"/> to remove it.</summary>
public sealed record BadgeImageChange(BadgeImageInput? Image);

/// <summary>A partial update: only the fields that are set change. <see cref="Image"/> is <see langword="null"/> when the picture is left alone.</summary>
public sealed record BadgePatch(string? Name = null, string? Description = null, BadgeRuleInput? Rule = null, bool? Active = null, BadgeImageChange? Image = null);

/// <summary>Creating a badge or adding examples beyond the limit (<c>409 badge_limit</c>).</summary>
public sealed record BadgeLimitReached(int Limit);

/// <summary>What adding the example badges did: the badges it created, and how many examples already existed.</summary>
public sealed record AddedExampleBadges(IReadOnlyList<Badge> Created, int Skipped);

/// <summary>How far one person is towards one active badge, evaluated on the data at the moment of the request.</summary>
public sealed record BadgeProgressItem(string BadgeId, int Current, int Threshold, DateTimeOffset? AwardedAt);

/// <summary>The progress of one person towards every active badge, oldest badge first.</summary>
public sealed record BadgeProgress(string PersonId, IReadOnlyList<BadgeProgressItem> Items);
