using HttpClient = System.Net.Http.HttpClient;
using System.Text.RegularExpressions;
using Godot;

namespace ArkhamUI;

// Importing a deck by its link (Yunke 11-21: "可以做联网，从arkham db还有arkhambuild导入卡组的功能吗"). Only when the player
// asks for it (the import window's 联网导入): one GET of the deck's public JSON, which then goes through the same import as a
// pasted JSON (BuildDecks.Import — build's own rules, our official Chinese card names).
//   ArkhamDB    https://arkhamdb.com/decklist/view/<n>/…  → arkhamdb.com/api/public/decklist/<n>  (a published deck)
//               https://arkhamdb.com/deck/view/<n>        → arkhamdb.com/api/public/deck/<n>      (a private deck the owner
//                                                           made public in their ArkhamDB settings)
//               a bare number                             → tried as a deck, then as a published deck
//   arkham.build https://arkham.build/share/<id>, deck/view/<id>, decklist/view/<id> → the engine's PublicDeckImport
//                (api.arkham.build/v1/public/share/<id>, the same call build's own share page makes; a deck kept only in
//                someone's browser is not there and comes back as 未找到公开牌组)
public static class OnlineImport
{
    static readonly HttpClient http = MakeClient();
    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ArkhamUI-DeckImport/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    public static bool LooksLikeLink(string s)
    {
        s = s.Trim();
        return s.Contains("arkhamdb.com", StringComparison.OrdinalIgnoreCase) || s.Contains("arkham.build", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(s, @"^\d{1,9}$");
    }

    // the addresses to try for what was pasted, in order; or why there is none
    public static (List<string> Urls, string? Error) Targets(string input)
    {
        var s = input.Trim();
        var urls = new List<string>();
        Match m;
        if ((m = Regex.Match(s, @"arkham\.build/share/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)).Success)
            urls.Add("https://api.arkham.build/v1/public/share/" + m.Groups[1].Value);
        else if ((m = Regex.Match(s, @"/decklist/view/(\d+)", RegexOptions.IgnoreCase)).Success)
            urls.Add("https://arkhamdb.com/api/public/decklist/" + m.Groups[1].Value);
        else if ((m = Regex.Match(s, @"/deck/view/(\d+)(?:[/?#]|$)", RegexOptions.IgnoreCase)).Success)
            urls.Add("https://arkhamdb.com/api/public/deck/" + m.Groups[1].Value);
        else if (Regex.IsMatch(s, @"arkham\.build/deck/view/[0-9a-f-]{8,}", RegexOptions.IgnoreCase))
            return (urls, "这是只保存在 arkham.build 浏览器里的牌组，没有公开地址。请在 arkham.build 里点“分享”生成分享链接（arkham.build/share/…），或导出 JSON 后粘贴。");
        else if ((m = Regex.Match(s, @"^(\d{1,9})$")).Success)
        {
            urls.Add("https://arkhamdb.com/api/public/deck/" + m.Groups[1].Value);
            urls.Add("https://arkhamdb.com/api/public/decklist/" + m.Groups[1].Value);
        }
        else return (urls, "认不出这个链接。支持 ArkhamDB 的牌组链接（decklist/view/… 或 deck/view/…）、ArkhamDB 牌组编号，以及 arkham.build 的分享链接（arkham.build/share/…）。");
        return (urls, null);
    }

    // the deck's JSON, or why it could not be had
    public static async Task<(string? Json, string? Error)> Fetch(string input)
    {
        // arkham.build links: the engine's reader of build's public share API (Gathering.PublicDeckImport — checks the link,
        // caps the size, normalises the card codes); ArkhamDB links and numbers below
        if (Regex.IsMatch(input, @"(^|//|\.)arkham\.build/", RegexOptions.IgnoreCase))
        {
            try { var deck = await Gathering.PublicDeckImport.Fetch(input.Trim()); if (App.I.QaRunning) GD.Print("QA_ONLINE build " + input); return (deck.GetRawText(), null); }
            catch (InvalidDataException e) { return (null, e.Message); }
            catch (Exception e) { return (null, "读取失败：" + e.Message); }
        }
        var (urls, error) = Targets(input);
        if (error is not null) return (null, error);
        string last = "";
        foreach (var url in urls)
        {
            try
            {
                using var res = await http.GetAsync(url);
                var body = await res.Content.ReadAsStringAsync();
                if (App.I.QaRunning) GD.Print("QA_ONLINE " + url + " " + (int)res.StatusCode + " " + body.Length);
                if (res.IsSuccessStatusCode && body.TrimStart().StartsWith("{")) return (body, null);
                // ArkhamDB answers a deck that does not exist (or is not public) with 200 and an empty body
                last = (int)res.StatusCode == 404 || (res.IsSuccessStatusCode && body.Trim().Length == 0) ? "找不到这个牌组（如果是 ArkhamDB 的私人牌组，需要作者在 ArkhamDB 设置里开启“公开牌组”）。"
                     : "服务器返回 " + (int)res.StatusCode + "。";
            }
            catch (TaskCanceledException) { last = "连接超时，请检查网络后再试。"; }
            catch (Exception e) { last = "无法连接：" + e.Message; }
        }
        return (null, last);
    }
}
