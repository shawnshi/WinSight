using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Media;

internal static class SourceOperationCases
{
    internal static async Task<int> NavigateAsync(string host,string fixtureRoot,string root)
    {
        if(Directory.Exists(root))throw new IOException("Fresh test output required.");Directory.CreateDirectory(root);
        var before=Process.GetProcessesByName("WINWORD").Select(p=>{using(p)return new{p.Id,Started=p.StartTime.ToUniversalTime()};}).ToArray();var outcomes=new List<object>();int failures=0;
        foreach(bool word in new[]{true,false})
        {
            string source=Path.Combine(root,word?"own.docx":"own.xlsx");File.Copy(Path.Combine(fixtureRoot,word?"own.docx":"own.xlsx"),source);
            string workspace=Path.Combine(root,word?"word-work":"excel-work");Directory.CreateDirectory(workspace);
            var location=word?new SourceLocation("paragraph",Paragraph:2,Part:"body"):new SourceLocation("sheet",Sheet:"Case",Range:"B3");
            string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();Process? process=null;
            try
            {
                await using var client=new NativeMediaProcessClient(new(){HostExecutable=host,WorkspaceRoot=workspace});
                var result=await client.NavigateOfficeAsync(new(source,hash,location));process=Process.GetProcessById(result.ProcessId);
                bool live=!process.HasExited;bool clean=!Directory.EnumerateFileSystemEntries(workspace).Any()&&result.CleanupWarning is null;outcomes.Add(new{kind=word?"word":"excel",ok=live&&clean,live,clean,result});if(!live||!clean)failures++;
            }
            catch(Exception e){failures++;outcomes.Add(new{kind=word?"word":"excel",ok=false,error=e.ToString()});}
            finally{if(process is not null){if(!process.HasExited){process.Kill(entireProcessTree:false);if(!process.WaitForExit(5000))throw new IOException("Own handed-off test Office did not stop.");}process.Dispose();}}
        }
        var after=Process.GetProcessesByName("WINWORD").Select(p=>{using(p)return new{p.Id,Started=p.StartTime.ToUniversalTime()};}).ToArray();bool oldPreserved=before.All(p=>after.Contains(p));if(!oldPreserved)failures++;
        await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{success=failures==0,failures,existingWordProcessesPreserved=oldPreserved,beforeWordMetadata=before,afterWordMetadata=after,scope="Production readonly exact Wordparagraph2 / ExcelB3 via owned NativeOM plus actual committeduserhandoff. OnlyownnewOfficeclosed; existingOfficePIDmetadataonly, no realuserdocuments or PowerPointnavigation",outcomes},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"WordExcelNavigation failures={failures}");return failures==0?0:1;
    }
}
