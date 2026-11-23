using System.Reflection;
using System.Text.Json;
using Gathering;

// What the rules core can start (later cycles, 11-22): NativeHost's public surface and a pure call by name.
// Usage: Gathering.Probe <root> --pure <method> [json request]   |   --host-methods
static class CoreListProbe
{
    public static void HostMethods()
    {
        var asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Arkham.NativeHost.dll"));
        foreach (var t in asm.GetExportedTypes())
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                Console.WriteLine(t.FullName + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
    }
    public static async Task Pure(string root, string method, string request)
    {
        var data = Path.Combine(root, "evidence", "probe-pure-" + DateTime.UtcNow.ToString("HHmmss"));
        Directory.CreateDirectory(data);
        await using var backend = new PrototypeBackend(root, data); await backend.Ready;
        try
        {
            var r = await backend.PureRaw(method, JsonDocument.Parse(request).RootElement.Clone());
            var text = r.GetRawText(); File.WriteAllText(Path.Combine(data, method + ".json"), text);
            Console.WriteLine("OK " + method + " " + text.Length + " :: " + text[..Math.Min(1500, text.Length)]);
        }
        catch (Exception e) { Console.WriteLine("FAIL " + method + " :: " + e.Message[..Math.Min(600, e.Message.Length)]); }
    }
}
