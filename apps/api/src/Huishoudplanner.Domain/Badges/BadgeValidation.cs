using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Huishoudplanner.Domain.Limits;

namespace Huishoudplanner.Domain.Badges;

/// <summary>
/// The value rules of a badge (requirements 3 <c>badges</c>, 4.13; the zod schemas of <c>packages/shared/src/schemas/badges.ts</c> and
/// <c>decodeBadgeImage</c> of the Node server). Every problem is keyed by the dotted field path, like the Node <c>validation_error</c> details; the
/// shape of the request body (types, nulls) is the HTTP adapter's concern.
/// </summary>
public static partial class BadgeValidation
{
    public const string InvalidBase64 = "invalid_base64";
    public const string ImageTooLarge = "image_too_large";
    public const string UnsupportedImageType = "unsupported_image_type";
    public const string ImageTypeMismatch = "image_type_mismatch";
    public const string UnknownTask = "unknown_task";

    /// <summary>Longest base64 text that can hold the largest image: 4 characters per 3 bytes, with padding.</summary>
    public static int MaxBase64Length(BadgeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return (int)Math.Ceiling(limits.MaxImageBytes / 3.0) * 4;
    }

    /// <summary>The trimmed name, or <see langword="null"/> with an error on <c>name</c>.</summary>
    public static string? CheckName(string? name, BadgeLimits limits, IDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(errors);
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length < limits.MinNameLength || trimmed.Length > limits.MaxNameLength)
        {
            errors["name"] = [$"must be {limits.MinNameLength} to {limits.MaxNameLength} characters"];
            return null;
        }

        return trimmed;
    }

    /// <summary>The trimmed description (empty when absent), or <see langword="null"/> with an error on <c>description</c>.</summary>
    public static string? CheckDescription(string? description, BadgeLimits limits, IDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(errors);
        var trimmed = description?.Trim() ?? string.Empty;
        if (trimmed.Length > limits.MaxDescriptionLength)
        {
            errors["description"] = [$"must be at most {limits.MaxDescriptionLength} characters"];
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// The rule with its tasks stored once, lower case and in a stable order (an on-time-weeks rule has none), or <see langword="null"/> with
    /// errors on <c>rule.threshold</c> and <c>rule.taskIds</c>. Whether the tasks exist is the use case's question.
    /// </summary>
    public static BadgeRule? CheckRule(BadgeRuleInput? rule, BadgeLimits limits, IDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(errors);
        if (rule is null)
        {
            errors["rule"] = ["is required"];
            return null;
        }

        var max = rule.Type == BadgeRuleType.OnTimeWeeks ? limits.MaxOnTimeWeeksThreshold : limits.MaxThreshold;
        var valid = true;
        if (rule.Threshold < 1 || rule.Threshold > max)
        {
            errors["rule.threshold"] = [$"must be a whole number from 1 to {max}"];
            valid = false;
        }

        if (rule.Type == BadgeRuleType.OnTimeWeeks)
        {
            return valid ? BadgeRule.OnTimeWeeks(rule.Threshold) : null;
        }

        var ids = rule.TaskIds ?? [];
        if (ids.Count > limits.MaxRuleTasks)
        {
            errors["rule.taskIds"] = [$"must hold at most {limits.MaxRuleTasks} tasks"];
            valid = false;
        }
        else if (ids.Any(id => !BadgeIds.IsValid(id)))
        {
            errors["rule.taskIds"] = ["invalid_object_id"];
            valid = false;
        }

        return valid ? new BadgeRule(rule.Type, SortedIds(ids), rule.Threshold) : null;
    }

    /// <summary>The ids once, lower case, in ordinal order, so the tasks of a rule compare as a set.</summary>
    public static IReadOnlyList<string> SortedIds(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return [.. ids.Select(id => id.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Checks the bytes of an image and returns what is stored (ADR-0014): valid base64, at most the size limit, a type that is really PNG, JPEG
    /// or WebP (an SVG is refused, because it can carry script) and the same as the declared type. The hash identifies the bytes in the audit log.
    /// </summary>
    public static BadgeImageData? CheckImage(BadgeImageInput? image, BadgeLimits limits, IDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(errors);
        if (image is null)
        {
            return null;
        }

        if (!BadgeNames.TryParseImageType(image.ContentType, out var declared))
        {
            errors["image.contentType"] = ["must be image/png, image/jpeg or image/webp"];
            return null;
        }

        var data = image.Data ?? string.Empty;
        if (data.Length == 0)
        {
            errors["image.data"] = [InvalidBase64];
            return null;
        }

        if (data.Length > MaxBase64Length(limits))
        {
            errors["image.data"] = [ImageTooLarge];
            return null;
        }

        if (!Base64().IsMatch(data))
        {
            errors["image.data"] = [InvalidBase64];
            return null;
        }

        var bytes = Convert.FromBase64String(data);
        if (bytes.Length == 0)
        {
            errors["image.data"] = [InvalidBase64];
            return null;
        }

        if (bytes.Length > limits.MaxImageBytes)
        {
            errors["image.data"] = [ImageTooLarge];
            return null;
        }

        if (BadgeImages.Sniff(bytes) is not { } real)
        {
            errors["image.data"] = [UnsupportedImageType];
            return null;
        }

        if (real != declared)
        {
            errors["image.contentType"] = [ImageTypeMismatch];
            return null;
        }

        return new BadgeImageData(bytes, real, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [GeneratedRegex("^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Base64();
}
