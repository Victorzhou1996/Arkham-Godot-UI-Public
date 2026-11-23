using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Arkham.NativeHost;

/// <summary>
/// A transport container, not a game validator or a cross-database transaction.
/// SaveBytes is the exact original UTF8 payload; the caller must still use
/// LocalGameController.ImportAsync to validate and atomically publish a game.
/// </summary>
public sealed record LocalSaveBundleImport(
    byte[] SaveBytes,string SourceId,bool IsBundle,
    IReadOnlyList<string> InstalledArtHashes,IReadOnlyList<string> MissingLocalArt);

public static class LocalSaveBundle
{
    public const long MaxArchiveBytes=128L*1024*1024;
    public const long MaxUncompressedBytes=256L*1024*1024;
    public const int MaxSaveBytes=32*1024*1024;
    public const int MaxManifestBytes=1024*1024;
    public const int MaxImages=2000;
    public const int MaxEntries=MaxImages+2;
    static readonly UTF8Encoding StrictUtf8=new(false,true);
    static readonly Regex ShaPattern=new("^[0-9a-f]{64}$",RegexOptions.CultureInvariant);
    static readonly Regex ArtPathPattern=new("^art/[0-9a-f]{64}\\.(png|jpg|webp)$",RegexOptions.CultureInvariant);
    sealed record ImageObject(string Sha256,string Mime,byte[] Bytes)
    {
        public string Entry=>"art/"+Sha256+Extension(Mime);
    }
    sealed record SaveInfo(string SourceId,string[] References);

    static InvalidDataException Invalid(string message)=>new("存档包："+message);
    static string Digest(string value)
    {
        if(!ShaPattern.IsMatch(value))throw Invalid("摘要必须是64位小写SHA256。");
        return value;
    }
    static string Extension(string mime)=>mime switch{
        "image/png"=>".png","image/jpeg"=>".jpg","image/webp"=>".webp",
        _=>throw Invalid("不支持的图片格式。")
    };
    static string Source(string value)
    {
        const string prefix="arkham-core-sha256:";
        if(!value.StartsWith(prefix,StringComparison.Ordinal))throw Invalid("核心来源格式不正确。");
        Digest(value[prefix.Length..]);return value;
    }
    static void ImageHeader(byte[] data,string mime)
    {
        if(data.Length==0||data.Length>LocalAuthorLibrary.MaxArtBytes)throw Invalid("单张卡图超过限制。");
        bool valid=mime switch{
            "image/png"=>data.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10}),
            "image/jpeg"=>data.Length>=3&&data[0]==255&&data[1]==216&&data[2]==255,
            "image/webp"=>data.Length>=12&&Encoding.ASCII.GetString(data,0,4)=="RIFF"&&Encoding.ASCII.GetString(data,8,4)=="WEBP",
            _=>false
        };
        if(!valid)throw Invalid("图片头与声明格式不符（本检查不是完整图像解码）。");
    }
    static JsonDocument Document(byte[] bytes,int limit)
    {
        if(bytes.Length==0||bytes.Length>limit)throw Invalid("JSON大小超过限制。");
        _=StrictUtf8.GetString(bytes);
        JsonDocument? doc=null;
        try{
            doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=256,AllowTrailingCommas=false,CommentHandling=JsonCommentHandling.Disallow});
            void Unique(JsonElement value){
                if(value.ValueKind==JsonValueKind.Object){
                    var keys=new HashSet<string>(StringComparer.Ordinal);
                    foreach(var p in value.EnumerateObject()){if(!keys.Add(p.Name))throw Invalid("JSON含重复字段。");Unique(p.Value);}
                }else if(value.ValueKind==JsonValueKind.Array)foreach(var child in value.EnumerateArray())Unique(child);
            }
            Unique(doc.RootElement);return doc;
        }catch{doc?.Dispose();throw;}
    }
    static JsonElement Field(JsonElement value,string field)
    {
        if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty(field,out var result))throw Invalid("缺少字段："+field);
        return result;
    }
    static string Text(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.String)throw Invalid("需要字符串。");
        return value.GetString()!;
    }
    static long Number(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Number||!value.TryGetInt64(out var n))throw Invalid("需要Int64整数。");
        return n;
    }
    static void Fields(JsonElement value,params string[] expected)
    {
        if(value.ValueKind!=JsonValueKind.Object)throw Invalid("需要JSON对象。");
        var actual=value.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal);
        if(!actual.SetEquals(expected))throw Invalid("清单字段不符合版本1。");
    }
    static SaveInfo InspectSave(byte[] bytes,string expectedSourceId,int expectedSnapshotFormat)
    {
        Source(expectedSourceId);
        using var doc=Document(bytes,MaxSaveBytes);var root=doc.RootElement;
        if(Number(Field(root,"saveFormat"))!=1)throw Invalid("未知saveFormat，不自动迁移。");
        if(!Guid.TryParse(Text(Field(root,"gameId")),out var id)||id==Guid.Empty)throw Invalid("无效gameId。");
        var snapshot=Field(root,"snapshot");
        if(Number(Field(snapshot,"snapshotFormat"))!=expectedSnapshotFormat)throw Invalid("快照版本不兼容，不自动迁移。");
        string source=Source(Text(Field(snapshot,"coreSourceId")));
        // another core's save is not refused here: importing it into the core decides (its compatibleImportSourceIds)
        var game=Field(snapshot,"game");if(game.ValueKind!=JsonValueKind.Object)throw Invalid("game必须为对象。");
        var references=new HashSet<string>(StringComparer.Ordinal);
        void Art(JsonElement value){
            if(value.ValueKind==JsonValueKind.Null)return;
            string reference=Text(value);
            if(!reference.StartsWith("sha256:",StringComparison.Ordinal))throw Invalid("卡图槽只接受本地sha256引用，不读取URL、data或文件路径。");
            references.Add(Digest(reference[7..]));
            if(references.Count>MaxImages)throw Invalid("卡图对象过多。");
        }
        // Exact original CustomCard slots: art + def.meta backArt/portrait/
        // portraitBack. def.art is a runtime card identity, NOT an image path.
        // Never recursively scan arbitrary metadata for strings/URLs.
        if(game.TryGetProperty("gameCustomCards",out var cards)){
            if(cards.ValueKind!=JsonValueKind.Object)throw Invalid("gameCustomCards必须为对象。");
            if(cards.EnumerateObject().Take(10001).Count()>10000)throw Invalid("自制卡映射过大。");
            foreach(var row in cards.EnumerateObject()){
                var card=row.Value;var def=Field(card,"def");
                if(def.ValueKind!=JsonValueKind.Object)throw Invalid("自制卡缺少def对象。");
                if(card.TryGetProperty("art",out var art))Art(art);
                if(def.TryGetProperty("meta",out var meta)){
                    if(meta.ValueKind==JsonValueKind.Null)continue;
                    if(meta.ValueKind!=JsonValueKind.Object)throw Invalid("自制卡meta必须为对象。");
                    foreach(string slot in new[]{"backArt","portrait","portraitBack"})
                        if(meta.TryGetProperty(slot,out var value))Art(value);
                }
            }
        }
        return new(source,references.OrderBy(s=>s,StringComparer.Ordinal).ToArray());
    }
    static byte[] ReadLimited(Stream stream,long advertised,int limit)
    {
        if(advertised<0||advertised>limit)throw Invalid("条目解压大小超过限制。");
        using var output=new MemoryStream((int)advertised);var buffer=new byte[64*1024];int n;
        while((n=stream.Read(buffer,0,buffer.Length))!=0){
            if(output.Length+n>limit||output.Length+n>advertised)throw Invalid("实际解压长度超过声明或限额。");
            output.Write(buffer,0,n);
        }
        if(output.Length!=advertised)throw Invalid("条目被截断。");
        return output.ToArray();
    }
    static byte[] ReadEntry(ZipArchiveEntry entry,int limit){using var s=entry.Open();return ReadLimited(s,entry.Length,limit);}
    static void WriteEntry(ZipArchive zip,string name,byte[] bytes)
    {
        // No compression makes the export size bounded and predictable; imports
        // may still be compressed and are checked against decompressed limits.
        var entry=zip.CreateEntry(name,CompressionLevel.NoCompression);
        using var output=entry.Open();output.Write(bytes);
    }

    /// <summary>
    /// Writes a new .arkhamsave. Does not overwrite a destination. All images
    /// are read and checked before writing. A failed publication may leave an
    /// explicitly named .partial-* sibling; it is not a completed save bundle.
    /// </summary>
    public static void Export(string destination,byte[] saveBytes,LocalAuthorLibrary library,
        string expectedSourceId,int expectedSnapshotFormat=2)
    {
        ArgumentNullException.ThrowIfNull(saveBytes);ArgumentNullException.ThrowIfNull(library);
        byte[] save=(byte[])saveBytes.Clone();var info=InspectSave(save,expectedSourceId,expectedSnapshotFormat);
        var images=new List<ImageObject>();long total=save.Length;
        foreach(string hash in info.References){
            var art=library.ReadArt(hash);ImageHeader(art.Bytes,art.Mime);
            if(LocalAuthorLibrary.Hash(art.Bytes)!=hash)throw Invalid("本地图片摘要不符。");
            total=checked(total+art.Bytes.Length);if(total>MaxUncompressedBytes)throw Invalid("总解压大小超过限制。");
            images.Add(new(hash,art.Mime,art.Bytes));
        }
        byte[] manifest=JsonSerializer.SerializeToUtf8Bytes(new{
            format="arkham-save-bundle",version=1,sourceId=info.SourceId,
            save=new{path="save.json",sha256=LocalAuthorLibrary.Hash(save),bytes=save.Length},
            images=images.Select(i=>new{path=i.Entry,sha256=i.Sha256,mime=i.Mime,bytes=i.Bytes.Length}).ToArray()
        });
        if(manifest.Length>MaxManifestBytes||total+manifest.Length>MaxUncompressedBytes
           ||total+manifest.Length+(long)(images.Count+2)*512>MaxArchiveBytes)throw Invalid("存档包超过导出大小限制。");
        string path=Path.GetFullPath(destination);
        if(!path.EndsWith(".arkhamsave",StringComparison.OrdinalIgnoreCase))throw Invalid("导出文件须为.arkhamsave。");
        if(File.Exists(path)||Directory.Exists(path))throw new IOException("目标已存在，不会覆盖。");
        string parent=Path.GetDirectoryName(path)!;if(!Directory.Exists(parent))throw new DirectoryNotFoundException(parent);
        string temporary=Path.Combine(parent,"."+Path.GetFileName(path)+".partial-"+Guid.NewGuid().ToString("N"));
        using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None)){
            using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true)){
                WriteEntry(zip,"manifest.json",manifest);WriteEntry(zip,"save.json",save);
                foreach(var image in images)WriteEntry(zip,image.Entry,image.Bytes);
            }
            if(output.Length>MaxArchiveBytes)throw Invalid("实际压缩包超过限制。");
            output.Flush(true);
        }
        File.Move(temporary,path,false);
    }

    /// <summary>
    /// Validates EVERY bundle entry before the first ImportArt call. Afterwards
    /// an IO failure, or a later game-import rejection, may leave immutable
    /// unreferenced images. No game or author package/revision is created here.
    /// Plain JSON remains supported; absent local images are reported, not
    /// fetched. The original input file is never modified.
    /// </summary>
    public static LocalSaveBundleImport Import(string sourceFile,LocalAuthorLibrary library,
        string expectedSourceId,int expectedSnapshotFormat=2)
    {
        ArgumentNullException.ThrowIfNull(library);Source(expectedSourceId);
        using var input=new FileStream(Path.GetFullPath(sourceFile),FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length==0||input.Length>MaxArchiveBytes)throw Invalid("输入大小超过限制。");
        int first=input.ReadByte(),second=input.ReadByte();input.Position=0;
        if(first!='P'||second!='K'){
            byte[] plain=ReadLimited(input,input.Length,MaxSaveBytes);
            var plainInfo=InspectSave(plain,expectedSourceId,expectedSnapshotFormat);
            var missing=new List<string>();
            foreach(string hash in plainInfo.References){
                try{var art=library.ReadArt(hash);ImageHeader(art.Bytes,art.Mime);}
                catch(KeyNotFoundException){missing.Add(hash);}
            }
            return new(plain,plainInfo.SourceId,false,Array.Empty<string>(),missing.ToArray());
        }
        using var zip=new ZipArchive(input,ZipArchiveMode.Read,true);
        if(zip.Entries.Count is <2 or >MaxEntries)throw Invalid("ZIP条目数量超过限制。");
        var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.Ordinal);
        var insensitive=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var entry in zip.Entries){
            string name=entry.FullName;
            if(name!="manifest.json"&&name!="save.json"&&!ArtPathPattern.IsMatch(name))throw Invalid("ZIP含非白名单路径。");
            if(!insensitive.Add(name)||!entries.TryAdd(name,entry))throw Invalid("ZIP含重复路径。");
            // Reject directories, Unix links/devices and Windows reparse-point
            // flags even though this helper never extracts paths to disk.
            int unixType=(entry.ExternalAttributes>>16)&0xF000;
            if(unixType!=0&&unixType!=0x8000||(entry.ExternalAttributes&0x410)!=0)throw Invalid("ZIP含非普通文件。");
            int limit=name=="manifest.json"?MaxManifestBytes:name=="save.json"?MaxSaveBytes:LocalAuthorLibrary.MaxArtBytes;
            if(entry.Length<0||entry.Length>limit||entry.CompressedLength<0||entry.CompressedLength>MaxArchiveBytes)throw Invalid("ZIP单条目大小超限。");
            total=checked(total+entry.Length);if(total>MaxUncompressedBytes)throw Invalid("ZIP总解压大小超限。");
        }
        if(!entries.TryGetValue("manifest.json",out var manifestEntry)||!entries.TryGetValue("save.json",out var saveEntry))throw Invalid("缺少清单或存档。");
        using var doc=Document(ReadEntry(manifestEntry,MaxManifestBytes),MaxManifestBytes);var manifest=doc.RootElement;
        Fields(manifest,"format","version","sourceId","save","images");
        if(Text(Field(manifest,"format"))!="arkham-save-bundle"||Number(Field(manifest,"version"))!=1)throw Invalid("未知存档包版本，不自动迁移。");
        _=Source(Text(Field(manifest,"sourceId")));   // well-formed; the core decides on import whether it reads this core's saves
        var saveRow=Field(manifest,"save");Fields(saveRow,"path","sha256","bytes");
        if(Text(Field(saveRow,"path"))!="save.json"||Number(Field(saveRow,"bytes"))!=saveEntry.Length)throw Invalid("存档清单路径或长度不符。");
        byte[] save=ReadEntry(saveEntry,MaxSaveBytes);
        if(LocalAuthorLibrary.Hash(save)!=Digest(Text(Field(saveRow,"sha256"))))throw Invalid("存档SHA不符。");
        var info=InspectSave(save,expectedSourceId,expectedSnapshotFormat);
        var rows=Field(manifest,"images");if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>MaxImages)throw Invalid("无效图片清单。");
        var images=new List<ImageObject>();var hashes=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in rows.EnumerateArray()){
            Fields(row,"path","sha256","mime","bytes");
            string hash=Digest(Text(Field(row,"sha256"))),mime=Text(Field(row,"mime")),path=Text(Field(row,"path"));
            if(!hashes.Add(hash)||path!="art/"+hash+Extension(mime)||!entries.TryGetValue(path,out var entry))throw Invalid("图片路径、重复摘要或对象不符。");
            if(Number(Field(row,"bytes"))!=entry.Length)throw Invalid("图片声明长度不符。");
            byte[] bytes=ReadEntry(entry,LocalAuthorLibrary.MaxArtBytes);
            if(LocalAuthorLibrary.Hash(bytes)!=hash)throw Invalid("图片SHA不符。");
            ImageHeader(bytes,mime);images.Add(new(hash,mime,bytes));
        }
        if(entries.Count!=images.Count+2||!hashes.SetEquals(info.References))throw Invalid("图片集合与四槽实际引用不完全一致。");
        // All archive bytes/JSON/paths/source/headers/hashes have now passed.
        // Preflight existing objects too, before adding any new one.
        foreach(var image in images){
            try{
                var old=library.ReadArt(image.Sha256);ImageHeader(old.Bytes,old.Mime);
                if(old.Mime!=image.Mime)throw Invalid("已有内容址图片的格式不一致。");
            }catch(KeyNotFoundException){ /* New immutable object. */ }
        }
        foreach(var image in images)
            if(library.ImportArt(image.Bytes,image.Mime)!=image.Sha256)throw Invalid("作者库返回不一致的内容址。");
        return new(save,info.SourceId,true,images.Select(i=>i.Sha256).ToArray(),Array.Empty<string>());
    }
}


