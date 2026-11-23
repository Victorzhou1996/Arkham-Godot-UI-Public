using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gathering;

/// <summary>Read-only public-share import. Never saves or drops unsupported deck fields.</summary>
public static class PublicDeckImport
{
    // Public contract confirmed against the upstream v1-public-share-proxy test:
    // https://github.com/arkham-build/arkham.build/blob/main/backend/src/tests/v1-public-share-proxy.spec.ts
    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(20) };

    public static Uri Endpoint(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("请填写公开的 arkham.build 牌组链接。");
        var match = Regex.Match(uri.AbsolutePath, @"\A/(decklist|deck|share)(?:/view)?/([A-Za-z0-9_-]{1,128})/?\z", RegexOptions.IgnoreCase);
        if (uri.IdnHost is "arkham.build" or "www.arkham.build" && match.Success)
            return new Uri("https://api.arkham.build/v1/public/share/" + match.Groups[2].Value
                + (match.Groups[1].Value.Equals("decklist", StringComparison.OrdinalIgnoreCase) ? "?type=decklist" : ""));
        if (uri.IdnHost == "api.arkham.build" && Regex.IsMatch(uri.AbsolutePath, @"\A/v1/public/share/[A-Za-z0-9_-]{1,128}\z")
            && uri.Query is "" or "?type=decklist") return uri;
        throw new InvalidDataException("仅支持公开的 arkham.build 牌组链接。");
    }

    public static async Task<JsonElement> Fetch(string input, CancellationToken cancellationToken = default)
    {
        var endpoint = Endpoint(input);
        try
        {
            using var response = await Client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new InvalidDataException("未找到公开牌组，请检查链接与公开分享状态。");
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("牌组来源暂时无法读取，请稍后重试。");
            if (response.Content.Headers.ContentLength is > 1_048_576) throw new InvalidDataException("来源牌组内容过大，无法读取。");
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] bytes = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 1_048_576) throw new InvalidDataException("来源牌组内容过大，无法读取。");
                buffer.Write(bytes, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var deck = document.RootElement;
            if (J.String(deck, "investigator_code").Length == 0 || J.Get(deck, "slots").ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("来源返回的牌组格式无法识别。");
            var normalized = JsonNode.Parse(deck.GetRawText())!.AsObject();
            normalized["investigator_code"] = CoreCatalog.Normalize(J.String(deck, "investigator_code"));
            foreach (string field in new[] { "slots", "sideSlots", "ignoreDeckLimitSlots" })
            {
                if (J.Get(deck, field).ValueKind != JsonValueKind.Object) continue;
                var slots = new Dictionary<string, int>();
                foreach (var slot in J.Props(J.Get(deck, field)))
                {
                    if (!slot.Value.TryGetInt32(out var quantity) || quantity < 0 || quantity > 99)
                        throw new InvalidDataException("来源卡牌数量无效：" + slot.Name);
                    // Explicit zero entries carry no cards; preserve positive unknown codes.
                    if (quantity > 0)
                    {
                        string code = CoreCatalog.Normalize(slot.Name);
                        slots[code] = checked(slots.GetValueOrDefault(code) + quantity);
                    }
                }
                normalized[field] = JsonSerializer.SerializeToNode(slots);
            }
            return JsonSerializer.SerializeToElement(normalized);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        { throw new InvalidDataException("牌组来源连接失败，请检查网络后重试。", error); }
        catch (JsonException error)
        { throw new InvalidDataException("来源返回的牌组格式无法识别。", error); }
    }
}
