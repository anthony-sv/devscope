using System.Security.Cryptography;
using System.Text;
using Devscope.Core.Database;

namespace Devscope.App.Services;

/// <summary>Encrypts saved passwords with DPAPI for the current Windows user: another account or machine cannot read them.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Ties the ciphertext to this purpose, so it cannot be swapped with another app's DPAPI blob.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Devscope.DbConnection");

    public string Protect(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
    }

    public string? Unprotect(string protectedSecret)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Passwords of SQL logins: the saved (encrypted) one, or one typed for this session only. Plain text never leaves
/// memory.
/// </summary>
public sealed class ConnectionPasswords(ISecretProtector protector)
{
    private readonly Dictionary<Guid, string> _session = [];

    /// <summary>The password to connect with, or null when there is none.</summary>
    public string? Get(DbConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_session.TryGetValue(profile.Id, out var password))
        {
            return password;
        }

        return profile.ProtectedPassword.Length > 0 ? protector.Unprotect(profile.ProtectedPassword) : null;
    }

    /// <summary>Whether connecting needs a password that is not known yet.</summary>
    public bool IsMissing(DbConnectionProfile profile) =>
        profile.Authentication == DbAuthentication.SqlLogin && string.IsNullOrEmpty(Get(profile));

    /// <summary>Keeps <paramref name="password"/> for this session; empty forgets it.</summary>
    public void Remember(Guid id, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            _session.Remove(id);
        }
        else
        {
            _session[id] = password;
        }
    }

    public string Protect(string password) => protector.Protect(password);
}
