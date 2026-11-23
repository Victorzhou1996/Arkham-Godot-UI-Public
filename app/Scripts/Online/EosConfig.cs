#if ARKHAM_EOS
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArkhamUI;

// EOS client config (it holds the Client Secret). Its values are never printed, logged, written to evidence or copied
// into the repository in plain text.
// Yunke 11-22 ("用1+2"): shipped games carry it sealed (res://online/eos.dat, made by tools/eos_seal.py) so players are
// online right after install without seeing the key in the package; the key's own protection is the Client Policy
// on the Epic portal. A developer file in ~/.arkham still wins when present.
public sealed record EosConfig(string ProductId, string SandboxId, string DeploymentId, string ClientId, string ClientSecret)
{
    public static string DefaultPath => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".arkham", "eos-config.local.json");
    public const string SealedPath = "res://online/eos.dat";

    // null when neither the developer file nor the sealed copy gives a complete config; reason says which, without any value
    public static EosConfig? Load(out string reason)
    {
        reason = "";
        string path = DefaultPath;
        // QA: --eos-sealed-only tests the packaged copy on a machine that also has the developer file
        bool sealedOnly = Array.IndexOf(Godot.OS.GetCmdlineUserArgs(), "--eos-sealed-only") >= 0;
        if (!sealedOnly && File.Exists(path))
        {
            try { return Parse(File.ReadAllBytes(path), out reason, "EOS 开发配置文件"); }
            catch (Exception) { reason = "联机未配置：EOS 开发配置文件读取失败"; return null; } // no exception text: it can quote file content
        }
        if (!Godot.FileAccess.FileExists(SealedPath)) { reason = "联机未配置：没有找到联机配置"; return null; }
        try
        {
            byte[]? plain = Unseal(Godot.FileAccess.GetFileAsBytes(SealedPath));
            if (plain is null) { reason = "联机未配置：安装包里的联机配置已损坏"; return null; }
            try { return Parse(plain, out reason, "安装包里的联机配置"); }
            finally { Array.Clear(plain); }
        }
        catch (Exception) { reason = "联机未配置：安装包里的联机配置读取失败"; return null; }
    }

    static EosConfig? Parse(byte[] json, out string reason, string what)
    {
        reason = "";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Get(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
        var cfg = new EosConfig(Get("productId"), Get("sandboxId"), Get("deploymentId"), Get("clientId"), Get("clientSecret"));
        if (cfg.ProductId.Length == 0 || cfg.SandboxId.Length == 0 || cfg.DeploymentId.Length == 0 || cfg.ClientId.Length == 0 || cfg.ClientSecret.Length == 0)
        {
            reason = "联机未配置：" + what + "不完整";
            return null;
        }
        return cfg;
    }

    // Sealed layout: "AHE1" | salt[16] | tag[16] | cipher. key = SHA256(salt|A|B|C); stream block i = SHA256(key|i LE32);
    // tag = HMAC-SHA256(key, cipher)[..16]. Obfuscation only (anyone with this code can open it); keep in step with tools/eos_seal.py.
    static readonly byte[] A = { 0x9A, 0x55, 0x67, 0xC8, 0xF3, 0x06, 0x1E, 0x83, 0x87, 0x72, 0xFA, 0x26, 0x8A, 0x2D, 0x28, 0xB7 };
    static byte[] B => new byte[] { 0x09, 0x41, 0x16, 0xC9, 0x97, 0xA3, 0xF7, 0xD2, 0x08, 0x11, 0x79, 0x94, 0xE8, 0x1C, 0x5A, 0x95 };
    static byte[] C()
    {
        var c = new byte[] { 0xF1, 0xF0, 0x2E, 0xB5, 0xDB, 0x10, 0x53, 0x44, 0xEE, 0x4D, 0xB9, 0xB7, 0xF1, 0xBD, 0xBA, 0x3E };
        return c;
    }

    static byte[]? Unseal(byte[] blob)
    {
        if (blob.Length < 36 || blob[0] != (byte)'A' || blob[1] != (byte)'H' || blob[2] != (byte)'E' || blob[3] != (byte)'1') return null;
        var salt = blob.AsSpan(4, 16);
        var tag = blob.AsSpan(20, 16);
        var cipher = blob.AsSpan(36);
        var seed = new byte[16 + 48];
        salt.CopyTo(seed);
        A.CopyTo(seed, 16); B.CopyTo(seed, 32); C().CopyTo(seed, 48);
        byte[] key = SHA256.HashData(seed);
        Array.Clear(seed);
        byte[] mac = HMACSHA256.HashData(key, cipher);
        if (!CryptographicOperations.FixedTimeEquals(mac.AsSpan(0, 16), tag)) { Array.Clear(key); return null; }
        var plain = new byte[cipher.Length];
        var block = new byte[36];
        key.CopyTo(block, 0);
        for (int i = 0, n = 0; i < cipher.Length; n++)
        {
            BitConverter.TryWriteBytes(block.AsSpan(32), n);
            if (!BitConverter.IsLittleEndian) block.AsSpan(32, 4).Reverse();
            byte[] ks = SHA256.HashData(block);
            for (int j = 0; j < 32 && i < cipher.Length; j++, i++) plain[i] = (byte)(cipher[i] ^ ks[j]);
        }
        Array.Clear(key); Array.Clear(block);
        return plain;
    }
}
#endif
