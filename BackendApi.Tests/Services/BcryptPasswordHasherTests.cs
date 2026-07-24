using BackendApi.Services;

namespace BackendApi.Tests.Services;

public class BcryptPasswordHasherTests
{
    private readonly BcryptPasswordHasher _hasher = new();

    [Fact]
    public void Verify_ReturnsTrue_ForMatchingPassword()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.True(_hasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForWrongPassword()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.False(_hasher.Verify("wrong password", hash));
    }

    [Fact]
    public void Hash_ProducesDifferentOutput_ForSamePassword()
    {
        var first = _hasher.Hash("same-password");
        var second = _hasher.Hash("same-password");

        Assert.NotEqual(first, second);
    }

    // fix: pin the bcrypt work factor explicitly (>= the library default of 11). bcrypt
    // embeds the cost in the hash as $2<x>$<cost>$..., so index [2] after splitting on '$'
    // is the two-digit cost regardless of the $2a$/$2b$ prefix variant.
    [Fact]
    public void Hash_UsesPinnedWorkFactor_12()
    {
        var hash = _hasher.Hash("some-password");

        var cost = hash.Split('$')[2];
        Assert.Equal("12", cost);
    }

    // bcrypt reads the cost from each stored hash, so a hash written at a lower cost (e.g.
    // the prior default of 11) still verifies after the work factor is pinned to 12.
    [Fact]
    public void Verify_StillAccepts_HashWrittenAtLowerWorkFactor()
    {
        var legacyHash = BCrypt.Net.BCrypt.HashPassword("legacy-password", 11);

        Assert.True(_hasher.Verify("legacy-password", legacyHash));
    }
}
