namespace MeshCoreMessenger.Desktop.Presentation;

internal enum MentionKind { Plain, Other, Own }
internal sealed record MentionSegment(string RawText, MentionKind Kind)
{
    public string DisplayText => RawText;
}

/// <summary>Presentation only: names in message text are not authenticated identities.</summary>
internal static class MentionParser
{
    public static IReadOnlyList<MentionSegment> Parse(string? text, string? ownName)
    {
        text ??= string.Empty;
        var result = new List<MentionSegment>();
        var plainStart = 0;
        for (var index = 0; index < text.Length - 1; index++)
        {
            if (text[index] != '@' || text[index + 1] != '[') continue;
            var end = index + 2;
            var valid = true;
            while (end < text.Length && text[end] != ']')
            {
                if (text[end] is '[' or '\r' or '\n') valid = false;
                end++;
            }
            if (end == text.Length) break;
            var name = text.AsSpan(index + 2, end - index - 2);
            if (valid && !name.IsWhiteSpace())
            {
                if (index > plainStart) result.Add(new(text[plainStart..index], MentionKind.Plain));
                var own = !string.IsNullOrWhiteSpace(ownName) && name.Equals(ownName.AsSpan(), StringComparison.Ordinal);
                result.Add(new(text[index..(end + 1)], own ? MentionKind.Own : MentionKind.Other));
                plainStart = end + 1;
            }
            index = end;
        }
        if (plainStart < text.Length) result.Add(new(text[plainStart..], MentionKind.Plain));
        return result;
    }
}
