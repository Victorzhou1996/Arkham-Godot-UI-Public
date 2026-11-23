using System.Text.Json;
using Arkham.NativeHost;

namespace Gathering;

// What the game table needs from a game (Yunke 11-22, online play): the local rules core (PrototypeBackend) on this machine,
// or the host's core reached over the network (the app's RemoteBackend) for a player who joined an online room.
public interface IGameBackend
{
    double LastOperationMs { get; }
    Task<TableState> Open(Guid id, Guid? selected = null, bool followAsked = false);
    Task<TableState> Choice(TableState basis, int index, bool followAsked = false);
    Task<TableState> Answer(TableState basis, JsonElement answer, bool followAsked = false);
    Task<TableState> Amounts(TableState basis, IReadOnlyDictionary<string, int> values);
    Task<TableState> Continue(TableState basis);
    Task<TableState> DefaultSettings(TableState basis);
    Task<TableState> SelectDeck(TableState basis, JsonElement deck);
    Task<TableState> UpgradeDeck(TableState basis, string investigatorId, JsonElement deck);
    Task<TableState> UpgradeDeck(TableState basis, JsonElement deck);
    Task<TableState> KeepDeck(TableState basis, string investigatorId);
    Task<TableState> ReplaceInvestigator(TableState basis, string investigatorId, JsonElement deck);
    Task<TableState> BeginUpgrade(TableState basis);
    Task<TableState?> UndoStep(TableState basis);
    Task<TableState> UndoBoundary(TableState basis, string boundary);
    Task<TableState> UndoToLogRow(TableState basis, long rowSequence);
    Task<TableState> RevertTo(TableState trial, long sequence);
    Task<TableState> Debug(TableState basis, JsonElement command);
    Task<JsonElement> DebugSchema(TableState basis);
    Task<JsonElement> ViewSection(TableState basis, string section);
    Task<(string Code, string Id)[]> ForcedSources(TableState basis, long since);
    Task<ClientLogHistory> Logs(TableState basis);
    HashSet<string> UsedInvestigators(TableState basis);
    Dictionary<string, int> CurrentDeck(TableState basis, string investigatorId);
    JsonElement SavedDocument(TableState basis);
    Task<JsonElement> BuildSchema(string investigator);
    Task<JsonElement> ValidateDeck(string investigator, string schemaId, JsonElement deck);
    Task<JsonElement> PrepareStandaloneDeck(JsonElement deck);
    Task<JsonElement> PureRaw(string method, JsonElement request);
}
