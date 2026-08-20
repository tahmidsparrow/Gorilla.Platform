using Gorilla.IAM.Auth;

namespace Gorilla.IAM.Tests;

/// <summary>
/// Mirrors Recruitment.Gorilla's own PasswordHasherTests.cs — this hasher is a
/// verbatim port, so imported RG hashes must verify identically here.
/// </summary>
public class Pbkdf2PasswordHasherTests
{
    [Fact]
    public void Hash_then_Verify_round_trips()
    {
        var hash = Pbkdf2PasswordHasher.Hash("s3cret!");
        Assert.True(Pbkdf2PasswordHasher.Verify("s3cret!", hash));
    }

    [Fact]
    public void Verify_rejects_the_wrong_password()
    {
        var hash = Pbkdf2PasswordHasher.Hash("s3cret!");
        Assert.False(Pbkdf2PasswordHasher.Verify("wrong", hash));
    }

    [Fact]
    public void Verify_returns_false_for_a_malformed_hash()
    {
        Assert.False(Pbkdf2PasswordHasher.Verify("x", "not-a-valid-hash"));
        Assert.False(Pbkdf2PasswordHasher.Verify("x", ""));
    }

    [Fact]
    public void Hashing_the_same_password_twice_differs_random_salt()
    {
        Assert.NotEqual(Pbkdf2PasswordHasher.Hash("same"), Pbkdf2PasswordHasher.Hash("same"));
    }

    /// <summary>
    /// Pins the exact "iterations.saltB64.hashB64" shape — 100,000 iterations,
    /// a 16-byte salt, a 32-byte hash — that RG's imported credentials are
    /// stored in. Cross-repo verification against RG's actual class is
    /// deliberately not done here: the spec (section 2) is explicit that
    /// PasswordHasher.cs is *copied*, not referenced, precisely so this
    /// project never takes a build-time dependency across the repo boundary.
    /// A drift in this shape would silently break every imported RG
    /// credential without either round-trip test above noticing, since both
    /// hash and verify with the same (possibly-drifted) code.
    /// </summary>
    [Fact]
    public void Hash_format_matches_the_iterations_dot_salt_dot_hash_shape_RG_hashes_use()
    {
        var parts = Pbkdf2PasswordHasher.Hash("s3cret!").Split('.');

        Assert.Equal(3, parts.Length);
        Assert.Equal("100000", parts[0]);
        Assert.Equal(16, Convert.FromBase64String(parts[1]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[2]).Length);
    }
}
