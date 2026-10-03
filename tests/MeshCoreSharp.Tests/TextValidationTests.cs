using System.Text;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Commands;

internal static class TextValidationTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Shared UTF-8 validation: ASCII, Cyrillic and emoji byte boundaries", Boundaries),
        ("Shared UTF-8 validation: invalid text, unknown budget and whitespace", InvalidText),
        ("Channel budget uses full actual name including whitespace", ChannelBudget),
        ("Shared validator preserves legacy encoder bytes and exception types", EncoderCompatibility),
    ];
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Text validation regression"); }

    private static Task Boundaries()
    {
        foreach (var bytes in new[] { 159, 160, 161 })
        foreach (var text in new[] { new string('a', bytes), new string('я', bytes / 2) + new string('a', bytes % 2),
            string.Concat(Enumerable.Repeat("👋", bytes / 4)) + new string('a', bytes % 4) })
        {
            var validation = TextMessageValidator.Validate(text);
            Check(validation.Utf8ByteCount == bytes && validation.MaxUtf8Bytes == 160);
            Check(validation.IsValid == (bytes <= 160));
        }
        return Task.CompletedTask;
    }
    private static Task InvalidText()
    {
        Check(TextMessageValidator.Validate(null).Error == TextMessageValidationError.Empty);
        Check(TextMessageValidator.Validate("").Utf8ByteCount == 0);
        Check(TextMessageValidator.Validate("a\0b").Error == TextMessageValidationError.ContainsNul);
        foreach (var text in new[] { "\uD800", "\uDC00", "a\uD800b", "\uDC00\uD800" })
        {
            var result = TextMessageValidator.Validate(text);
            Check(result.Error == TextMessageValidationError.InvalidUtf16 && result.Utf8ByteCount is null);
        }
        Check(TextMessageValidator.Validate(" \n\t ").IsValid);
        var unknown = TextMessageValidator.Validate("я👋", null);
        Check(unknown.Utf8ByteCount == 6 && unknown.MaxUtf8Bytes is null && !unknown.IsValid);
        Check(TextMessageValidator.Validate("я\0", null).Error == TextMessageValidationError.ContainsNul);
        return Task.CompletedTask;
    }
    private static Task ChannelBudget()
    {
        foreach (var name in new[] { "", " Node ", "🐈", "Узел🐈", "\uD800", new string('x', 160) })
        {
            var limit = 160 - Encoding.UTF8.GetByteCount(name) - 2;
            Check(TextMessageValidator.GetChannelTextLimit(name) == limit);
            if (limit > 0)
            {
                Check(TextMessageValidator.Validate(new string('x', limit), limit).IsValid);
                Check(!TextMessageValidator.Validate(new string('x', limit + 1), limit).IsValid);
            }
            else Check(!TextMessageValidator.Validate("x", limit).IsValid);
        }
        return Task.CompletedTask;
    }
    private static Task EncoderCompatibility()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        foreach (var name in new[] { " Node ", "🐈", "\uD800", new string('x', 160) })
        foreach (var text in new string?[] { null, "", "a\0b", "\0\uD800", "\uD800", "  Я🐈\n", new string('x', 160), new string('я', 81) })
        {
            Compare(() => LegacyBody(text!, 160), () => CompanionCommands.SendText(key, text!, 0x12345678)[13..]);
            var limit = 160 - Encoding.UTF8.GetByteCount(name) - 2;
            Compare(() => LegacyBody(text!, limit), () => CompanionCommands.SendChannelText(7, name, text!, 0x12345678)[7..]);
        }
        return Task.CompletedTask;
    }
    private static byte[] LegacyBody(string text, int limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (text.Contains('\0')) throw new ArgumentException("Text must not contain NUL.", nameof(text));
        var bytes = new UTF8Encoding(false, true).GetBytes(text);
        if (bytes.Length > limit) throw new ArgumentOutOfRangeException(nameof(text), $"Text exceeds the {limit}-byte UTF-8 limit.");
        return bytes;
    }
    private static void Compare(Func<byte[]> legacy, Func<byte[]> actual)
    {
        byte[]? expected = null;
        Exception? failure = null;
        try { expected = legacy(); } catch (Exception error) { failure = error; }
        try
        {
            var bytes = actual();
            Check(failure is null && expected!.SequenceEqual(bytes));
        }
        catch (ArgumentException error)
        {
            Check(failure?.GetType() == error.GetType() && ((ArgumentException)failure).ParamName == error.ParamName);
        }
    }
}
