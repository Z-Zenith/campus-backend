namespace BackendApi.Services;

public class BcryptPasswordHasher : IPasswordHasher
{
    // Pin the bcrypt work factor explicitly rather than relying on the library default
    // (11 in BCrypt.Net-Next), so it can't silently regress on a version bump and so the
    // cost is auditable here. 12 keeps a margin above the current default. bcrypt embeds
    // the cost in the hash string ($2a$<cost>$...), so Verify reads whatever cost each
    // stored hash was written with — existing hashes (cost 11 or otherwise) still verify.
    private const int WorkFactor = 12;

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
}
