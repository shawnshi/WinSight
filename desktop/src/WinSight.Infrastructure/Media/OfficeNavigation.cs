using System.Text.Json;
using System.Text.RegularExpressions;
using WinSight.Core;

namespace WinSight.Infrastructure.Media;
public sealed record NativeOfficeNavigationRequest(string SourcePath,string SourceSha256,SourceLocation Location)
{
    internal void Validate()
    {
        if(SourceSha256.Length!=64||!SourceSha256.All(Uri.IsHexDigit))throw new ArgumentException("Office navigation requires a current source fingerprint.");
        if(SupportsLocation(SourcePath,Location))return;
        throw new NotSupportedException("Precise open requires a supported Word body paragraph, Excel sheet range or PowerPoint slide.");
    }
    internal static bool SupportsLocation(string path,SourceLocation location)
    {
        var extension=Path.GetExtension(path).ToLowerInvariant();
        return extension==".docx"&&location is{Type:"paragraph",Part:"body",Paragraph:>=1 and <=5000}
            ||extension==".xlsx"&&location is{Type:"sheet",Sheet:{Length:>=1 and <=31},Range:{} range}&&ValidRange(range)
            ||extension==".pptx"&&location is{Type:"slide",Page:>=1 and <=5000};
    }
    internal static bool ValidRange(string range)
    {
        if(!Regex.IsMatch(range,@"^[A-Z]{1,3}[1-9][0-9]{0,6}(:[A-Z]{1,3}[1-9][0-9]{0,6})?$",RegexOptions.CultureInvariant))return false;
        int row0=0,col0=0;
        foreach(var cell in range.Split(':'))
        {
            int col=0,n=0;while(n<cell.Length&&char.IsAsciiLetterUpper(cell[n]))col=checked(col*26+cell[n++]-'A'+1);
            if(!int.TryParse(cell.AsSpan(n),out var row)||col>16384||row>1048576||row<row0||col<col0)return false;row0=row;col0=col;
        }
        return true;
    }
}
public sealed record NativeOfficeNavigationResult(int ProcessId,long WindowHandle,string Target,string? CleanupWarning=null)
{
    internal static NativeOfficeNavigationResult Parse(string payload,NativeOfficeNavigationRequest request)
    {
        using var doc=JsonDocument.Parse(payload,new JsonDocumentOptions{MaxDepth=8});var r=doc.RootElement;
        if(r.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Invalid Office handoff response.");var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in r.EnumerateObject())if(p.Name is not ("protocol" or "inputPath" or "state" or "exact" or "processId" or "windowHandle" or "target")||!seen.Add(p.Name))throw new InvalidDataException("Unknown/duplicate Office response member.");
        if(seen.Count!=7||r.GetProperty("protocol").GetInt32()!=1||r.GetProperty("state").GetString()!="handed-off"||!r.GetProperty("exact").GetBoolean()
            ||!string.Equals(r.GetProperty("inputPath").GetString(),request.SourcePath,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Office handoff identity/state mismatch.");
        int pid=r.GetProperty("processId").GetInt32();long hwnd=r.GetProperty("windowHandle").GetInt64();string target=r.GetProperty("target").GetString()!;
        if(pid<=0||hwnd==0||target is null||target.Length>1024||target.Contains('\0'))throw new InvalidDataException("Invalid Office handoff metadata.");
        return new(pid,hwnd,target);
    }
}
