using System;
using System.Text;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Crypto;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Unit)]
public sealed class UntrustedInboundKdfWorkBudgetTests
{
    [Fact]
    public void DefaultCycleBudget_FitsDocumentedNormalScenario_AndSingleEnvelopeMax()
    {
        const int productionN = SyncCryptoService.DefaultIterations;
        const int documentedNormalDerivations = 1 + 100;
        Assert.Equal(100_000, productionN);
        Assert.True(documentedNormalDerivations * (long)productionN
            <= UntrustedInboundKdfWorkBudget.DefaultCycleBudgetIterations);
        Assert.True(KdfDescriptorConstants.MaxPbkdf2Iterations
            <= UntrustedInboundKdfWorkBudget.DefaultCycleBudgetIterations);

        var budget = new UntrustedInboundKdfWorkBudget(
            UntrustedInboundKdfWorkBudget.DefaultCycleBudgetIterations);
        budget.Reserve(KdfDescriptorConstants.MaxPbkdf2Iterations);
        Assert.Equal(KdfDescriptorConstants.MaxPbkdf2Iterations, budget.Consumed);
        budget.Reserve(documentedNormalDerivations * (long)productionN);
        Assert.Equal(
            KdfDescriptorConstants.MaxPbkdf2Iterations + (documentedNormalDerivations * (long)productionN),
            budget.Consumed);
    }

    [Fact]
    public void Reserve_ExactBoundary_ThenPlusOneFailsWithoutMutation()
    {
        var budget = new UntrustedInboundKdfWorkBudget(10_000);
        budget.Reserve(10_000);
        Assert.Equal(10_000, budget.Consumed);
        var ex = Assert.Throws<SyncKdfWorkBudgetExceededException>(() => budget.Reserve(1));
        Assert.Equal(10_000, budget.Consumed);
        Assert.Equal(UntrustedInboundKdfWorkBudget.UserFacingMessage, ex.Message);
        AssertDoesNotLeakSecrets(ex.Message);
    }

    [Fact]
    public void Overflow_IsFailClosedAndDoesNotCountAsOfflineOrAuth()
    {
        var budget = new UntrustedInboundKdfWorkBudget(long.MaxValue);
        budget.Reserve(long.MaxValue);
        Assert.Equal(long.MaxValue, budget.Consumed);
        Assert.Throws<SyncKdfWorkBudgetExceededException>(() => budget.Reserve(1));
        Assert.Equal(long.MaxValue, budget.Consumed);
        var result = SyncCycleResult.KdfWorkBudgetExceeded();
        Assert.False(result.Success);
        Assert.True(result.IsKdfWorkBudgetExceeded);
        Assert.False(result.IsOffline);
        Assert.False(result.IsAuthError);
        Assert.False(result.IsCancelled);
        Assert.False(result.IsTimeout);
    }

    [Fact]
    public void DecryptPayload_BoundaryPlusOne_FailsBeforePbkdf2()
    {
        var crypto = new SyncCryptoService(iterations: 1_000);
        byte[] plaintext = Encoding.UTF8.GetBytes("kdf-budget");
        var enc = crypto.EncryptPayload(plaintext, "budget-password", Array.Empty<byte>());
        KdfDiagnostics.ResetPbkdf2Invocations();

        var exact = new UntrustedInboundKdfWorkBudget(enc.KdfIterations);
        byte[] roundTrip = crypto.DecryptPayload(
            enc.Ciphertext, enc.Salt, enc.Nonce, enc.Tag, "budget-password", Array.Empty<byte>(), enc.KdfIterations, exact);
        Assert.Equal(plaintext, roundTrip);
        Assert.Equal(1, KdfDiagnostics.Pbkdf2Invocations);

        KdfDiagnostics.ResetPbkdf2Invocations();
        var plusOne = new UntrustedInboundKdfWorkBudget(enc.KdfIterations - 1);
        Assert.Throws<SyncKdfWorkBudgetExceededException>(() =>
            crypto.DecryptPayload(
                enc.Ciphertext, enc.Salt, enc.Nonce, enc.Tag, "budget-password", Array.Empty<byte>(), enc.KdfIterations, plusOne));
        Assert.Equal(0, KdfDiagnostics.Pbkdf2Invocations);
    }

    [Fact]
    public void EncryptPayload_DoesNotConsumeInboundBudget()
    {
        var crypto = new SyncCryptoService(iterations: 1_000);
        var budget = new UntrustedInboundKdfWorkBudget(1);
        var enc = crypto.EncryptPayload(Encoding.UTF8.GetBytes("local-send"), "budget-password", Array.Empty<byte>());
        Assert.NotEmpty(enc.Ciphertext);
        Assert.Equal(0, budget.Consumed);
    }

    private static void AssertDoesNotLeakSecrets(string message)
    {
        Assert.DoesNotContain("password", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("salt", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ciphertext", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/packages/", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/blobs/", message, StringComparison.OrdinalIgnoreCase);
    }
}
