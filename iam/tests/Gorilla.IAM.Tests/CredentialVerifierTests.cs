using Gorilla.IAM.Auth;
using Gorilla.IAM.Data.Entities;

namespace Gorilla.IAM.Tests;

public class CredentialVerifierTests
{
    private static Credential BcryptCredential(string password) => new()
    {
        SubjectId = Guid.NewGuid(),
        Algorithm = CredentialAlgorithm.Bcrypt,
        Hash = BcryptPasswordHasher.Hash(password),
    };

    private static Credential Pbkdf2Credential(string password) => new()
    {
        SubjectId = Guid.NewGuid(),
        Algorithm = CredentialAlgorithm.Pbkdf2Sha256,
        Hash = Pbkdf2PasswordHasher.Hash(password),
    };

    [Fact]
    public void Rejects_a_null_credential()
    {
        Assert.Equal(CredentialVerifyResult.Rejected, CredentialVerifier.Verify(null, "anything"));
    }

    [Fact]
    public void Accepts_a_correct_bcrypt_password_without_touching_the_credential()
    {
        var credential = BcryptCredential("s3cret!");
        var originalHash = credential.Hash;

        var result = CredentialVerifier.Verify(credential, "s3cret!");

        Assert.Equal(CredentialVerifyResult.Accepted, result);
        Assert.Equal(CredentialAlgorithm.Bcrypt, credential.Algorithm);
        Assert.Equal(originalHash, credential.Hash);
    }

    [Fact]
    public void Rejects_a_wrong_bcrypt_password()
    {
        var credential = BcryptCredential("s3cret!");
        Assert.Equal(CredentialVerifyResult.Rejected, CredentialVerifier.Verify(credential, "wrong"));
    }

    /// <summary>The self-healing convergence spec section 3.4 describes: a
    /// correct PBKDF2 verify rewrites the credential to bcrypt in memory.</summary>
    [Fact]
    public void Accepts_a_correct_pbkdf2_password_and_rehashes_to_bcrypt()
    {
        var credential = Pbkdf2Credential("s3cret!");

        var result = CredentialVerifier.Verify(credential, "s3cret!");

        Assert.Equal(CredentialVerifyResult.AcceptedAndRehashed, result);
        Assert.Equal(CredentialAlgorithm.Bcrypt, credential.Algorithm);
        Assert.StartsWith("$2", credential.Hash);
        Assert.True(BcryptPasswordHasher.Verify("s3cret!", credential.Hash));
    }

    [Fact]
    public void Rejects_a_wrong_pbkdf2_password_and_leaves_the_credential_untouched()
    {
        var credential = Pbkdf2Credential("s3cret!");
        var originalHash = credential.Hash;

        var result = CredentialVerifier.Verify(credential, "wrong");

        Assert.Equal(CredentialVerifyResult.Rejected, result);
        Assert.Equal(CredentialAlgorithm.Pbkdf2Sha256, credential.Algorithm);
        Assert.Equal(originalHash, credential.Hash);
    }
}
