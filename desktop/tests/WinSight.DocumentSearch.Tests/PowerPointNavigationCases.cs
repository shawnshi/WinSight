using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Configuration;
using WinSight.Infrastructure.Runtime;
using WinSight.Infrastructure.Storage;

internal static class PowerPointNavigationCases
{
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    internal static async Task<int> RunAsync(string host,string fixture,string root)
    {
        if(Directory.Exists(root))throw new IOException("Fresh production navigation evidence directory required.");Directory.CreateDirectory(root);
        var checks=new List<object>();int failed=0;void Check(string name,bool ok,object? data=null){checks.Add(new{name,ok,data});if(!ok)failed++;}
        var before=Process.GetProcessesByName("WINWORD").Select(p=>{using(p)return new{p.Id,Started=p.StartTime.ToUniversalTime()};}).ToArray();
        var active=Process.GetProcessesByName("POWERPNT");try{if(active.Length!=0)throw new IOException("PowerPoint busy; do not run test against existing Office.");}finally{foreach(var p in active)p.Dispose();}
        string sources=Path.Combine(root,"sources"),data=Path.Combine(root,"data");Directory.CreateDirectory(sources);Directory.CreateDirectory(data);string source=Path.Combine(sources,"own-hidden.pptx");File.Copy(fixture,source);
        string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();var anchor=new SourceAnchor(new("slide",Page:2),"body");SearchHit hit;
        await new JsonSettingsStore(Path.Combine(data,"settings.json")).SaveAsync(new AppSettings{Folders=[sources],InferenceExecutable=host,BackgroundIndexingEnabled=false});
        await using(var repository=new SqliteIndexRepository(Path.Combine(data,"index/index.sqlite")))
        {
            var file=new FileInfo(source);await repository.UpsertFileAsync(new(0,source,"doc",file.Length,(file.LastWriteTimeUtc-DateTime.UnixEpoch).TotalSeconds,"done",null,JsonSerializer.Serialize(new{sourceSha256V1=hash}),[new("page","text",2,2,"Own hidden slide",null,anchor)]));var row=(await repository.GetFileAsync(source))!;hit=new(row.Id,source,"doc",1,"keyword","Own hidden slide",2,2,"page",anchor);
        }
        DesktopBackend? backend=null;Process? handed=null;
        try
        {
            backend=await DesktopBackend.CreateAsync(new(data,EnableBackgroundIndexing:false));
            await using(var preview=await backend.PreviewSourceAsync(hit)){Check("headless_current_slide_preview_advertises_typed_navigation",preview.SourceCurrent&&preview.CanNavigate&&preview.Source?.Location.Page==2);}
            var opened=await backend.NavigateSourceAsync(hit);Check("actual_readonly_hidden_slide2_selection_and_handoff",opened.Exact&&opened.ProcessId is >0,opened);
            if(opened.ProcessId is not {} pid)throw new InvalidOperationException("No owned handoff PID");handed=Process.GetProcessById(pid);Check("target_alive_after_native_worker_and_client_disposal",!handed.HasExited);
            var handle=handed.MainWindowHandle;GetWindowThreadProcessId(handle,out uint owner);Check("target_window_pid_matches_handoff",handle!=IntPtr.Zero&&owner==pid,new{pid,owner});
            bool refused=false;try{await backend.NavigateSourceAsync(hit);}catch(IOException e){refused=e.Message.Contains("already running",StringComparison.OrdinalIgnoreCase);}Check("busy_real_own_powerpoint_refused_without_attachment",refused&&!handed.HasExited);
            await backend.DisposeAsync();backend=null;Check("handed_target_survives_backend_disposal",!handed.HasExited);
            Check("navigation_task_directory_clean",!Directory.EnumerateFileSystemEntries(Path.Combine(data,"source-navigation")).Any());Check("readonly_source_bytes_unchanged",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).Equals(hash,StringComparison.OrdinalIgnoreCase));
        }
        catch(Exception e){failed++;checks.Add(new{name="production_ppt_navigation",ok=false,error=e.ToString()});}
        finally
        {
            if(backend is not null)try{await backend.DisposeAsync();}catch(Exception e){failed++;checks.Add(new{name="backend_cleanup",ok=false,error=e.ToString()});}
            if(handed is not null){if(!handed.HasExited){handed.Kill(entireProcessTree:false);if(!handed.WaitForExit(5000))throw new IOException("Exact own synthetic PowerPoint failed to settle.");}handed.Dispose();}
        }
        var after=Process.GetProcessesByName("WINWORD").Select(p=>{using(p)return new{p.Id,Started=p.StartTime.ToUniversalTime()};}).ToArray();Check("existing_word_process_identity_preserved",before.All(p=>after.Contains(p)));
        var remaining=Process.GetProcessesByName("POWERPNT");try{Check("own_powerpoint_closed_after_test",remaining.Length==0);}finally{foreach(var p in remaining)p.Dispose();}
        await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{success=failed==0,failed,checks,beforeWordMetadata=before,afterWordMetadata=after,scope="Actual production DesktopBackend/SQLite/singlePPTpreview and read-only original ownPPT slide2 navigation, worker-owned COM/solePID/frame plus exact-slide oracle, userhandoff, actualownbusydenial, backenddisposalsurvival and exactownPIDcleanup. NoWinUI/NativeOMclaim, unrelatedOfficecontent/index/settings ormodel. Idleexclusivecondition required, not asandbox."},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"PPT production navigation {checks.Count-failed}/{checks.Count}");return failed==0?0:1;
    }
}
