namespace ArkhamUI;

// Where updates come from and the key they must be signed with (docs/HOT-UPDATE-DESIGN.md). Both servers carry the same
// files; the player picks which one this computer downloads from (Yunke 11-23: "两台服务器都要上传，具体从谁下载取决于用户的
// 选择"). The private half of this key is only on the release computer (~/.arkham/update-signing-key.pem,
// tools/release/update_release.py); a manifest not signed with it is never used.
public sealed record UpdateLine(string Id, string Label, string Base);

public static class UpdatePolicy
{
    public const string Product = "arkham-ui";
    public const string KeyId = "arkham-ui-f58eb0ae28b1";
    public const string PublicKey = @"-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAv+/nGKpzVcJA0iavT8kM
vPSlZ11RweUfke2TKFvp51K6Ix2iSKoWJWBSeRuxn4KmvjOeLtRkgv1y0tE+efpJ
Gmhn7c232GNg75X0c2KtxfHUvSHp4ygKMFJ0Q5CgRNlwutXUysQizc6YD7zTMR80
ZZjSmUHo/aLHizKSLqueTFiGAoShN6ZHqNn0/cPoBsSY8lzrK28WBX8P0j4XiPO0
/gEYhEngVFYRm3oOyjvtlbfbMcD3Gk/6JBbOejjvJV40YXNvzOEIObUfOvdhMRfs
zUozwL77Qbo6e3efVzds0yiQ59qxsRLUG5zrYdMKL7lIiFz68AqZfjNkjYQBDWzl
SMtXOkVox3JdnRpUtP06LD1lybh/asnVYDdDKkBbeor4GaYy/NHUESHL8B2XAlTf
ODH5z+OoA6EudPj6XxVZHWrphkYHRwV5oQiYzLXcjm7/+F7TNzRTeGSX1giiVefD
fNlRZFIYelIKzSkJ2QTUCzNlpwebhJI9Ml4/3MYvLnvVAgMBAAE=
-----END PUBLIC KEY-----";

    // the domestic server is reached by its address; its certificate is not checked (it cannot name the address), which is
    // safe because nothing downloaded is used unless the signed manifest vouches for its hash
    public const string DomesticHost = "<DOMESTIC_SERVER_IP>";
    public static readonly UpdateLine Domestic = new("domestic", "国内", "https://" + DomesticHost + "/ui-updates/");
    public static readonly UpdateLine Overseas = new("overseas", "海外", "https://<OVERSEAS_SERVER_HOST>/ui-updates/");
    public static readonly UpdateLine[] Lines = { Domestic, Overseas };
}
