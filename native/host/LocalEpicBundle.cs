using System.IO.Compression;
using System.Text.Json;

namespace Arkham.NativeHost;

public sealed record ImportedEpicBundle(byte[] Event,IReadOnlyList<EpicUndoObject> UndoObjects);

// Only current event + referenced canonical undo objects, not a copied SQLite
// database, not a truncated history, and no independently playable group saves.
public static class LocalEpicBundle
{
    const long MaxObject=64L*1024*1024,MaxTotal=1024L*1024*1024;
    const int MaxFiles=100000;
    static byte[] Read(ZipArchiveEntry entry,long limit)
    {
        if(entry.Length<2||entry.Length>limit)throw new InvalidDataException("事件文件大小超过导入限制");
        using var input=entry.Open();using var output=new MemoryStream();byte[] buffer=new byte[65536];int n;
        while((n=input.Read(buffer))>0){if(output.Length+n>limit)throw new InvalidDataException("事件文件解压超过限制");output.Write(buffer,0,n);}
        if(output.Length!=entry.Length)throw new InvalidDataException("事件文件长度不符");return output.ToArray();
    }
    static void Add(ZipArchive zip,string name,byte[] bytes){var entry=zip.CreateEntry(name,CompressionLevel.Optimal);using var stream=entry.Open();stream.Write(bytes);}
    static HashSet<string> References(byte[] bytes)
    {
        using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=256});return doc.RootElement.GetProperty("groups").EnumerateArray()
            .SelectMany(g=>g.GetProperty("history").EnumerateArray().Select(h=>h.GetProperty("beforeSnapshotSha256").GetString()!)).ToHashSet(StringComparer.Ordinal);
    }
    public static void Export(string path,StoredEpicState state,IReadOnlyList<EpicUndoObject> undo)
    {
        if(!Path.IsPathFullyQualified(path))throw new ArgumentException("事件包需要完整路径");
        var candidate=LocalEpicStore.Candidate(state.Snapshot,state.Parent);if(candidate.Sha256!=state.Sha256)throw new InvalidDataException("事件摘要不符");
        var objects=undo.Select(o=>LocalEpicStore.UndoObject(o.Sha256,state.SourceId,o.Snapshot)).OrderBy(o=>o.Sha256,StringComparer.Ordinal).ToArray();
        var refs=References(state.Snapshot);if(objects.Length!=refs.Count||!refs.SetEquals(objects.Select(o=>o.Sha256)))throw new InvalidDataException("事件撤销对象缺失或重复");
        long total=state.Snapshot.LongLength+objects.Sum(o=>o.Snapshot.LongLength);if(total>MaxTotal||objects.Length+2>MaxFiles)throw new InvalidDataException("完整事件超过导出限制；不会截断历史");
        byte[] manifest=JsonSerializer.SerializeToUtf8Bytes(new{format="arkham-native-epic",version=1,coreSourceId=state.SourceId,eventId=state.EventId,revision=state.Revision,
            eventFile=new{path="event.json",sha256=state.Sha256,bytes=state.Snapshot.LongLength},undo=objects.Select(o=>new{path="undo/"+o.Sha256+".json",sha256=o.Sha256,bytes=o.Snapshot.LongLength}).ToArray()});
        string pending=path+".partial-"+Guid.NewGuid().ToString("N");
        using(var file=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
            using(var zip=new ZipArchive(file,ZipArchiveMode.Create,true)){Add(zip,"manifest.json",manifest);Add(zip,"event.json",state.Snapshot);foreach(var o in objects)Add(zip,"undo/"+o.Sha256+".json",o.Snapshot);}
            file.Flush(true);
        }
        // Never overwrite an existing export. Incomplete files remain explicit
        // .partial objects on a write failure rather than damaging a prior save.
        File.Move(pending,path,false);
    }
    public static ImportedEpicBundle Import(string path,string sourceId)
    {
        if(!Path.IsPathFullyQualified(path))throw new ArgumentException("事件包需要完整路径");
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(file.Length>MaxTotal)throw new InvalidDataException("事件包过大");
        using var zip=new ZipArchive(file,ZipArchiveMode.Read);if(zip.Entries.Count is <2 or >MaxFiles)throw new InvalidDataException("事件包文件数无效");
        var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.Ordinal);long total=0;
        foreach(var entry in zip.Entries){if(entry.FullName.Contains('\\')||entry.FullName.StartsWith('/')||entry.FullName.Split('/').Any(s=>s is "" or "." or "..")||!entries.TryAdd(entry.FullName,entry))throw new InvalidDataException("事件包路径重复或无效");
            if(entry.Length>MaxObject||(total=checked(total+entry.Length))>MaxTotal)throw new InvalidDataException("事件包解压超过限制");}
        byte[] Get(string name,long max)=>Read(entries.TryGetValue(name,out var entry)?entry:throw new InvalidDataException("事件包缺少 "+name),max);
        using var doc=JsonDocument.Parse(Get("manifest.json",4*1024*1024));var m=doc.RootElement;
        if(m.GetProperty("format").GetString()!="arkham-native-epic"||m.GetProperty("version").GetInt32()!=1||m.GetProperty("coreSourceId").GetString()!=sourceId)throw new InvalidDataException("事件包版本或规则核心不同，请使用对应旧版");
        var consumed=new HashSet<string>(StringComparer.Ordinal){"manifest.json"};
        byte[] Object(JsonElement descriptor,string expected){string name=descriptor.GetProperty("path").GetString()!;if(name!=expected||!consumed.Add(name))throw new InvalidDataException("事件清单路径不符或重复");byte[] data=Get(name,MaxObject);if(data.LongLength!=descriptor.GetProperty("bytes").GetInt64()||LocalEpicStore.Hash(data)!=descriptor.GetProperty("sha256").GetString())throw new InvalidDataException("事件清单长度或摘要不符");return data;}
        byte[] snapshot=Object(m.GetProperty("eventFile"),"event.json");var state=LocalEpicStore.Candidate(snapshot,null);
        if(state.SourceId!=sourceId||state.EventId!=m.GetProperty("eventId").GetGuid()||state.Revision!=m.GetProperty("revision").GetInt64())throw new InvalidDataException("事件清单身份不符");
        var objects=new List<EpicUndoObject>();foreach(var o in m.GetProperty("undo").EnumerateArray()){
            string sha=o.GetProperty("sha256").GetString()!;objects.Add(LocalEpicStore.UndoObject(sha,sourceId,Object(o,"undo/"+sha+".json")));
        }
        var refs=References(snapshot);if(consumed.Count!=entries.Count||objects.Count!=refs.Count||!refs.SetEquals(objects.Select(o=>o.Sha256)))throw new InvalidDataException("事件包有未知文件或撤销对象不完整");
        return new(snapshot,objects);
    }
}
