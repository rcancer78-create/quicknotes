namespace QuickNotes.App.Services;

public static class ImportLimits
{
    public const int MaxFileCount = 2000;
    public const long MaxIndividualFileBytes = 2 * 1024 * 1024;
    public const long MaxAggregateBytes = 32L * 1024 * 1024;
    public const int MaxHtmlNestingDepth = 32;
    public const int MaxDecodedTextChars = 2 * 1024 * 1024;
    public const int MaxEntityExpansionChars = 64 * 1024;
    public const long DefaultMaxAttachmentBytes = 25L * 1024 * 1024;
    public const int MaxPathDepth = 16;
    public const int MaxTagNameLength = 80;
}
