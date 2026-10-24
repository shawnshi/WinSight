using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinSight.Infrastructure.Media;

namespace WinSight.InferenceHost;
internal static partial class NativeMediaCommand
{
    private static OfficeHandoffResult NavigatePowerPointSource(string input,string output,string hash,JsonElement location,CancellationToken token)
    {
        if(location.GetProperty("type").GetString()!="slide"||location.GetProperty("page").ValueKind!=JsonValueKind.Number||location.GetProperty("page").GetInt32() is <1 or >5000)
            throw new NotSupportedException("PowerPoint exact open requires an indexed slide ordinal.");
        int number=location.GetProperty("page").GetInt32();
        using var source=OpenPinnedFile(input,MaxInputBytes);
        if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source),Convert.FromHexString(hash)))throw new IOException("Source changed since indexing; PowerPoint was not opened.");source.Position=0;
        PowerPointPolicy.ValidatePackage(source,token);
        using var mutex=new Mutex(false,PowerPointPolicy.AutomationMutex);bool acquired;
        try{acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
        if(!acquired)throw new IOException("PowerPoint automation busy; no instance attached. Do not open PowerPoint during the exclusive interval.");
        try
        {
            if(PowerPointPolicy.Busy)throw new IOException("PowerPoint is already running; precise open deferred without attaching.");
            string executable=ValidateLocalPath(PowerPointPolicy.InstalledPath()??throw new NotSupportedException("PowerPoint is not installed."),GetDriveType);
            using var executablePin=OpenPinnedFile(executable,128L*1024*1024);PowerPointPolicy.VerifyPublisher(executable);
            ValidateAncestors(output,includeLeaf:false);if(!CreateDirectory(output,IntPtr.Zero))throw new IOException("New owned PowerPoint handoff directory required.");
            using var directoryPin=CreateFile(output,0x80,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);if(directoryPin.IsInvalid)throw new IOException("Cannot pin PowerPoint handoff directory.");ValidateAncestors(output,includeLeaf:true);
            WindowsNativeMediaProcess? office=null;var references=new List<object>();bool handed=false;
            try
            {
                office=WindowsNativeMediaProcess.Start(executable,"",Path.GetDirectoryName(executable)!,TimeSpan.FromSeconds(5),false,NativeMediaOperation.OfficeNavigationServer);
                IntPtr frame=IntPtr.Zero;
                while(frame==IntPtr.Zero)
                {
                    token.ThrowIfCancellationRequested();
                    EnumWindows((window,_)=>{GetWindowThreadProcessId(window,out uint pid);if(!office.ContainsProcess(pid))return true;var cls=new StringBuilder(128);GetClassNameW(window,cls,128);if(cls.ToString().StartsWith("PPTFrameClass",StringComparison.Ordinal)){frame=window;return false;}return true;},IntPtr.Zero);
                    if(frame==IntPtr.Zero&&token.WaitHandle.WaitOne(30))token.ThrowIfCancellationRequested();
                }
                VerifySolePowerPoint(office);
                var activated=Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application",true)!)!;references.Add(activated);var app=new OfficeObject(activated,references);
                VerifySolePowerPoint(office);
                if(!string.Equals(Path.TrimEndingDirectorySeparator(app.Get<string>("Path")),Path.GetDirectoryName(executable),StringComparison.OrdinalIgnoreCase))throw new IOException("PowerPoint application path mismatch.");
                app.Set("Visible",-1);var decks=app.Object("Presentations");if(decks.Get<int>("Count")!=0)throw new IOException("Unexpected deck in the new owned PowerPoint instance.");
                OfficeObject deck;try{app.Set("AutomationSecurity",3);deck=decks.Object("Open",true,input,-1,0,-1);}finally{app.Set("AutomationSecurity",2);}
                if(deck.Get<int>("ReadOnly")!=-1||!string.Equals(deck.Get<string>("FullName"),input,StringComparison.OrdinalIgnoreCase))throw new IOException("PowerPoint source identity/read-only check failed.");
                var slides=deck.Object("Slides");if(slides.Get<int>("Count")<number)throw new InvalidDataException("Indexed slide no longer exists.");
                var windows=deck.Object("Windows");if(windows.Get<int>("Count")!=1||decks.Get<int>("Count")!=1)throw new IOException("Unexpected PowerPoint document/window count.");
                var window=windows.Object("Item",true,1);var view=window.Object("View");view.Call("GotoSlide",number);
                if(view.Object("Slide").Get<int>("SlideIndex")!=number)throw new IOException("PowerPoint exact-slide selection oracle failed.");
                VerifySolePowerPoint(office);GetWindowThreadProcessId(frame,out uint owner);if(!office.ContainsProcess(owner))throw new IOException("PowerPoint frame ownership changed.");token.ThrowIfCancellationRequested();
                var result=new OfficeHandoffResult(1,input,"handed-off",true,checked((int)owner),frame.ToInt64(),$"PowerPoint slide {number}");
                using(var receipt=new FileStream(Path.Combine(output,"handoff.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))JsonSerializer.Serialize(receipt,result,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
                office.HandoffForUser(owner);handed=true;return result;
            }
            finally
            {
                try{var released=new HashSet<object>(ReferenceEqualityComparer.Instance);for(int n=references.Count-1;n>=0;n--)if(released.Add(references[n])&&Marshal.IsComObject(references[n]))Marshal.FinalReleaseComObject(references[n]);}
                finally
                {
                    if(office is not null&&!handed){using var stop=new CancellationTokenSource();stop.Cancel();try{office.RunAsync(65536,TimeSpan.FromSeconds(5),stop.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){ }}
                    if(office is{Settled:true}or{DetachedForUser:true})office.Dispose();else if(office is not null)MediaRetention.Keep(office);
                }
            }
        }
        finally{mutex.ReleaseMutex();}
    }
    private static void VerifySolePowerPoint(WindowsNativeMediaProcess owner)
    {
        var processes=Process.GetProcessesByName("POWERPNT");
        try{if(processes.Length!=1||!owner.ContainsProcess(checked((uint)processes[0].Id)))throw new IOException("PowerPoint idle-exclusive ownership changed; no foreign instance may be used.");}
        finally{foreach(var process in processes)process.Dispose();}
    }
}
