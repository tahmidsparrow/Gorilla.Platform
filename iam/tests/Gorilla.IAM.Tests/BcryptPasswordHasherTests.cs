using Gorilla.IAM.Auth;

namespace Gorilla.IAM.Tests;

public class BcryptPasswordHasherTests
{
    [Fact]
    public void Hash_then_Verify_round_trips()
    {
        var hash = BcryptPasswordHasher.Hash("s3cret!");
        Assert.True(BcryptPasswordHasher.Verify("s3cret!", hash));
    }

    [Fact]
    public void Verify_rejects_the_wrong_password()
    {
        var hash = BcryptPasswordHasher.Hash("s3cret!");
        Assert.False(BcryptPasswordHasher.Verify("wrong", hash));
    }

    /// <summary>
    /// A fixed vector produced by GorillaHR's own hasher (Python's bcrypt
    /// package — see backend/app/core/security.py) must verify directly, per
    /// spec section 3.4 ("import both apps' hashes verbatim"). Generated with
    /// `bcrypt.hashpw(b"s3cret!", bcrypt.gensalt())`, not fabricated, so this
    /// is a real cross-language compatibility guarantee, not just a format
    /// check on a hash this same class produced.
    /// </summary>
    [Fact]
    public void Verifies_a_hash_produced_by_GorillaHRs_own_python_bcrypt()
    {
        const string hrHash = "$2b$12$bf61J.eVHqkIRx/XijV55.rh2IcJipS0.1ppDVRtfMephv2PPifLW";
        Assert.True(BcryptPasswordHasher.Verify("s3cret!", hrHash));
    }

    [Fact]
    public void Verify_returns_false_rather_than_throwing_for_a_malformed_hash()
    {
        Assert.False(BcryptPasswordHasher.Verify("x", "not-a-bcrypt-hash"));
        Assert.False(BcryptPasswordHasher.Verify("x", ""));
    }
}
