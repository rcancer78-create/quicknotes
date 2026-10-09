namespace QuickNotes.App.Services.EncryptedArchive;

/// <summary>
/// Independent archive KDF bounds (ADR-002 §4 / ADR-004). Production create may omit N and
/// receive <see cref="EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives"/>.
/// Readers always use the explicit header N within min/max; they never substitute the default.
/// </summary>
public sealed class EncryptedArchiveKdfLimits
{
    public int MinIterations { get; }
    public int MaxIterations { get; }

    static EncryptedArchiveKdfLimits()
    {
        int shipping = EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives;
        if (shipping < EncryptedArchiveConstants.MinKdfIterationsUntrusted
            || shipping > EncryptedArchiveConstants.MaxKdfIterations)
        {
            throw new EncryptedArchiveFormatException("Некорректные границы KDF архива.");
        }
    }

    public EncryptedArchiveKdfLimits(int minIterations, int maxIterations)
    {
        if (minIterations < 1 || maxIterations < minIterations)
        {
            throw new EncryptedArchiveFormatException("Некорректные границы KDF архива.");
        }

        MinIterations = minIterations;
        MaxIterations = maxIterations;
    }

    public static EncryptedArchiveKdfLimits Production { get; } = new(
        EncryptedArchiveConstants.MinKdfIterationsUntrusted,
        EncryptedArchiveConstants.MaxKdfIterations);

    /// <summary>
    /// Test-only lower bound so focused tests can use a small N. Production callers must use <see cref="Production"/>.
    /// </summary>
    public static EncryptedArchiveKdfLimits ForTests(int minIterations = 1)
        => new(minIterations, EncryptedArchiveConstants.MaxKdfIterations);

    public int ResolveCreateIterations(int? requested)
    {
        int iterations = requested ?? EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives;
        ValidateIterations(iterations);
        return iterations;
    }

    public void ValidateIterations(int iterations)
    {
        if (iterations < MinIterations || iterations > MaxIterations)
        {
            throw new EncryptedArchiveFormatException("Параметры KDF архива недопустимы.");
        }
    }
}
