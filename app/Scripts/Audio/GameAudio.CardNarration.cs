using Godot;
using Gathering;

namespace ArkhamUI;

// The act / agenda page ("新的目标 / 新的密谋 / 场景推进 / 密谋推进", TableScreen.Interlude) is read aloud too (Yunke 11-21:
// "要，接上" — only the story part). The card data keeps the two parts apart: flavor / back_flavor are the story, text /
// back_text the rules. Read in page order: the back of the card that turned over (back_flavor), then the front of the one
// that arrives (flavor). The rules under them are never read (they are not recorded, apart from the "guarded" lines, which
// stay unplayed). Recordings: the same 讯飞「江阅尘-解说」 delivery (voice/narration.json keys card.<code>.flavor / back_flavor).
// The page closing stops the voice. Unlike a story page, a state refresh does not stop it (the page is not a Read question).
public partial class GameAudio
{
    // page: the page's root control (its leaving the tree stops the voice); backOf: the card that turned over; frontOf: the new card
    public static void NarrateCards(Node page, string? backOf, string? frontOf)
    {
        if (!Available) return;
        try { I!.DoNarrateCards(page, backOf, frontOf); } catch (Exception e) { GD.PrintErr("AUDIO_NARRATE_CARDS_FAIL " + e.Message); }
    }

    void DoNarrateCards(Node page, string? backOf, string? frontOf)
    {
        if (!narrLoaded) return;
        var keys = new List<string>();
        if (!string.IsNullOrEmpty(backOf)) keys.Add("card." + CoreCatalog.Normalize(backOf) + ".back_flavor");
        if (!string.IsNullOrEmpty(frontOf)) keys.Add("card." + CoreCatalog.Normalize(frontOf) + ".flavor");
        var items = keys.SelectMany(CueKeysFor).Distinct().ToList();
        var unvoiced = keys.Where(k => !CueKeysFor(k).Any()).ToList();
        GD.Print($"AUDIO_NARRATE_CARDS {backOf ?? "-"}->{frontOf ?? "-"} recordings={items.Count}" + (unvoiced.Count > 0 ? " unvoiced=" + string.Join(",", unvoiced) : ""));
        DoStopNarration(0);                         // whatever was being read stops; narrSig stays empty, so NarrationFollow leaves this alone
        int token = narrToken;
        page.TreeExiting += () => { if (IsInstanceValid(this) && narrToken == token) DoStopNarration(0.4); };
        if (!prefs.Narration || items.Count == 0) return;
        for (int i = 0; i < items.Count; i++)
        {
            int gap = i == items.Count - 1 ? 0
                : narrLinks.TryGetValue(items[i], out var link) && link.Next == items[i + 1] ? link.GapMs
                : 650;                              // back of the old card → front of the new one: a breath between the two
            narrQueue.Enqueue((narrCues[items[i]], gap));
        }
        VoiceDuck(true);
        NextInQueue();
    }
}
