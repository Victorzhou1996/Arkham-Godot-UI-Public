using Godot;

namespace ArkhamUI;

// Starts online play where it is compiled in (ArkhamApp.csproj defines ARKHAM_EOS when the EOS SDK is found). Without the
// SDK, or without the developer config, Online.Service stays null and every online entry stays disabled.
public static class OnlineBoot
{
    public static void Start(Node host)
    {
#if ARKHAM_EOS
        EosOnlineService.Start(host);
#endif
        OnlineGame.QaBoot(host);   // QA: two games on one machine over a local link (--online-loop=…)
#if ARKHAM_EOS
#endif
    }
}
