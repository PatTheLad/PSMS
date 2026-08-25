using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PSMS.Core.Abstractions;
using PSMS.Core.Models;

namespace PSMS.Core.Storage;

public sealed class FileConnectionStore : IConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _storePath;
    private readonly string _keyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileConnectionStore()
    {
        var root = GetAppDataRoot();
        Directory.CreateDirectory(root);
        _storePath = Path.Combine(root, "connections.json");
        _keyPath = Path.Combine(root, "secret.key");
    }

    public async Task<IReadOnlyList<ConnectionDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ConnectionDefinition connection, string? plaintextPassword, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = (await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var existing = list.FirstOrDefault(c => c.Id == connection.Id);

            if (!string.IsNullOrEmpty(plaintextPassword))
            {
                connection.EncryptedPassword = Encrypt(plaintextPassword);
            }
            else if (existing is not null)
            {
                connection.EncryptedPassword = existing.EncryptedPassword;
            }

            if (existing is null)
            {
                list.Add(connection);
            }
            else
            {
                var index = list.IndexOf(existing);
                list[index] = connection;
            }

            await WriteUnlockedAsync(list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = (await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false))
                .Where(c => c.Id != id)
                .ToList();
            await WriteUnlockedAsync(list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? DecryptPassword(ConnectionDefinition connection)
    {
        if (string.IsNullOrWhiteSpace(connection.EncryptedPassword))
        {
            return null;
        }

        try
        {
            return Decrypt(connection.EncryptedPassword);
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<ConnectionDefinition>> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(_storePath);
        var data = await JsonSerializer.DeserializeAsync<List<ConnectionDefinition>>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return data ?? [];
    }

    private async Task WriteUnlockedAsync(List<ConnectionDefinition> connections, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_storePath);
        await JsonSerializer.SerializeAsync(stream, connections, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static string GetAppDataRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "PSMS");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "psms");
    }

    private string Encrypt(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        var key = GetOrCreateAesKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, bytes, cipher, tag);

        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length + tag.Length, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    private string Decrypt(string encryptedBase64)
    {
        var payload = Convert.FromBase64String(encryptedBase64);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var bytes = ProtectedData.Unprotect(payload, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }

        var key = GetOrCreateAesKey();
        var nonce = payload.AsSpan(0, 12);
        var tag = payload.AsSpan(12, 16);
        var cipher = payload.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] GetOrCreateAesKey()
    {
        if (File.Exists(_keyPath))
        {
            return File.ReadAllBytes(_keyPath);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_keyPath, key);
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Best-effort permissions.
        }

        return key;
    }
}
