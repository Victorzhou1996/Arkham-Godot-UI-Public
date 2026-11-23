using System.Text.Json;
namespace Arkham.NativeHost;
public interface ILanRoomGateway
{
    Task<Guid> ConnectAsync(LanHello hello);
    JsonElement HelloPacket();
    Task<JsonElement> HandleAsync(Guid connection,LanClientFrame frame);
    Task DisconnectAsync(Guid connection);
}
