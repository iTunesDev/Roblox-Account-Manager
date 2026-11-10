using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Saves accounts and settings to %LOCALAPPDATA%\RobloxAccountManager\accounts.dat.
/// The whole file is encrypted with Windows DPAPI (CurrentUser scope), so it can only be
/// decrypted by the same Windows user on the same PC. Nothing is sent off the machine.
/// </summary>
public class SecureStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RobloxAccountManager.v1.store");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public string Folder { get; }
    public string FilePath { get; }

    public SecureStore()
    {
        Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxAccountManager");
        FilePath = Path.Combine(Folder, "accounts.dat");
    }

    public StoreData Load()
    {
        if (!File.Exists(FilePath)) return new StoreData();
        var encrypted = File.ReadAllBytes(FilePath);
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<StoreData>(plain, JsonOpts) ?? new StoreData();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Save(StoreData data)
    {
        Directory.CreateDirectory(Folder);
        var plain = JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, FilePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
