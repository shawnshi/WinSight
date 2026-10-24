using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using WinSight.Infrastructure.Media;

namespace WinSight.InferenceHost;
internal sealed record OfficeHandoffResult(int Protocol,string InputPath,string State,bool Exact,int ProcessId,long WindowHandle,string Target);
internal static partial class NativeMediaCommand
{
    private delegate bool WindowEnumerator(IntPtr window,IntPtr data);
    [DllImport("user32.dll")]private static extern bool EnumWindows(WindowEnumerator callback,IntPtr data);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr window,WindowEnumerator callback,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassNameW(IntPtr window,StringBuilder text,int length);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("oleacc.dll")]private static extern int AccessibleObjectFromWindow(IntPtr window,uint objectId,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out object value);
    internal static Task<OfficeHandoffResult> NavigateOfficeAsync(string requestPath)
    {
        var completion=new TaskCompletionSource<OfficeHandoffResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{completion.SetResult(NavigateOffice(requestPath));}catch(Exception e){completion.SetException(e);}});
        thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();return completion.Task;
    }
    private static OfficeHandoffResult NavigateOffice(string requestPath)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));var token=deadline.Token;
        using var request=OpenPinnedFile(ValidateLocalPath(requestPath,GetDriveType),MaxRequestBytes);var bytes=new byte[(int)request.Length];request.ReadExactly(bytes);
        using var json=JsonDocument.Parse(bytes);var root=json.RootElement;
        CheckOfficeMembers(root,["protocol","inputPath","outputDirectory","sourceSha256","location"]);
        if(root.GetProperty("protocol").GetInt32()!=1)throw new JsonException("Unsupported Office navigation protocol.");
        string input=ValidateLocalPath(root.GetProperty("inputPath").GetString()!,GetDriveType),output=ValidateLocalPath(root.GetProperty("outputDirectory").GetString()!,GetDriveType);
        string hash=root.GetProperty("sourceSha256").GetString()!;if(hash.Length!=64||!hash.All(Uri.IsHexDigit))throw new InvalidDataException("Office source fingerprint missing.");
        var loc=root.GetProperty("location");CheckOfficeMembers(loc,["type","page","paragraph","sheet","range","part"]);
        string extension=Path.GetExtension(input).ToLowerInvariant();
        if(extension==".pptx")return NavigatePowerPointSource(input,output,hash,loc,token);
        bool word=extension==".docx";
        int paragraph=word&&loc.GetProperty("paragraph").ValueKind==JsonValueKind.Number?loc.GetProperty("paragraph").GetInt32():0;
        string? sheet=loc.GetProperty("sheet").GetString(),range=loc.GetProperty("range").GetString();
        if(word?(loc.GetProperty("type").GetString()!="paragraph"||loc.GetProperty("part").GetString()!="body"||paragraph is <1 or >5000)
            :extension!=".xlsx"||loc.GetProperty("type").GetString()!="sheet"||sheet is null||sheet.Length is <1 or >31||range is null||!OfficeRangeValid(range))
            throw new NotSupportedException("Only verified Word body paragraphs and Excel sheet rectangles support precise handoff.");
        using var source=OpenPinnedFile(input,MaxInputBytes);
        if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(source),Convert.FromHexString(hash)))throw new IOException("Source changed since indexing; no Office application opened.");source.Position=0;
        OfficeNavigationPolicy.Validate(source,word,token);
        byte[]? expectedPrefix=null;
        using(var zip=new ZipArchive(source,ZipArchiveMode.Read,leaveOpen:true))
        {
            if(word)
            {
                using var data=zip.GetEntry("word/document.xml")!.Open();var xml=XDocument.Load(data);XNamespace w="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                var paragraphs=xml.Root!.Element(w+"body")!.Descendants(w+"p").Take(paragraph).ToArray();if(paragraphs.Length!=paragraph)throw new InvalidDataException("Indexed paragraph outside current source.");
                expectedPrefix=ParagraphDigest(paragraphs.Select(p=>string.Concat(p.Descendants().Where(t=>t.Name==w+"t"||t.Name==w+"tab"||t.Name==w+"br"||t.Name==w+"cr").Where(t=>!t.Ancestors().Any(a=>a.Name==w+"del"||a.Name==w+"moveFrom")).Select(t=>t.Name==w+"t"?t.Value:t.Name==w+"tab"?"\t":"\n"))));
            }
        }
        string exeName=word?"WINWORD.EXE":"EXCEL.EXE";
        using var installed=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\"+exeName);
        var executable=ValidateLocalPath(installed?.GetValue(null) as string??throw new NotSupportedException("Required Office application is not installed."),GetDriveType);
        if(!Path.GetFileName(executable).Equals(exeName,StringComparison.OrdinalIgnoreCase))throw new IOException("Unexpected Office executable.");
        using var executablePin=OpenPinnedFile(executable,128L*1024*1024);PowerPointPolicy.VerifyPublisher(executable);
        ValidateAncestors(output,includeLeaf:false);if(!CreateDirectory(output,IntPtr.Zero))throw new IOException("Office output directory must be new.");
        using var directoryPin=CreateFile(output,0x80,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);if(directoryPin.IsInvalid)throw new IOException("Cannot pin Office task directory.");ValidateAncestors(output,includeLeaf:true);
        var bootstrap=Path.Combine(output,word?"bootstrap.docx":"bootstrap.xlsx");CreateOfficeBootstrap(bootstrap,word);
        WindowsNativeMediaProcess? office=null;var references=new List<object>();bool handed=false;
        try
        {
            office=WindowsNativeMediaProcess.Start(executable,bootstrap,Path.GetDirectoryName(executable)!,TimeSpan.FromSeconds(5),false,NativeMediaOperation.OfficeNavigationServer);
            IntPtr hwnd=IntPtr.Zero;string windowClass=word?"_WwG":"EXCEL7";
            while(hwnd==IntPtr.Zero)
            {
                token.ThrowIfCancellationRequested();
                EnumWindows((top,_)=>
                {
                    GetWindowThreadProcessId(top,out uint pid);if(!office.ContainsProcess(pid))return true;
                    EnumChildWindows(top,(child,_)=>{GetWindowThreadProcessId(child,out uint cp);if(!office.ContainsProcess(cp))return true;var cls=new StringBuilder(128);GetClassNameW(child,cls,128);if(cls.ToString()==windowClass){hwnd=child;return false;}return true;},IntPtr.Zero);
                    return hwnd==IntPtr.Zero;
                },IntPtr.Zero);
                if(hwnd==IntPtr.Zero&&token.WaitHandle.WaitOne(30))token.ThrowIfCancellationRequested();
            }
            var iid=new Guid("00020400-0000-0000-C000-000000000046");Marshal.ThrowExceptionForHR(AccessibleObjectFromWindow(hwnd,0xfffffff0,ref iid,out object native));references.Add(native);
            var window=new OfficeObject(native,references);var app=window.Object("Application");GetWindowThreadProcessId(hwnd,out uint bootstrapPid);if(!office.ContainsProcess(bootstrapPid))throw new IOException("Native object model window not owned.");
            if(!string.Equals(Path.TrimEndingDirectorySeparator(app.Get<string>("Path")),Path.GetDirectoryName(executable),StringComparison.OrdinalIgnoreCase))throw new IOException("Office installation identity mismatch.");
            OfficeObject target,active;
            app.Set("AutomationSecurity",3);
            try
            {
                if(word)
                {
                    var documents=app.Object("Documents");target=documents.NamedObject("Open",["FileName","ReadOnly","AddToRecentFiles","Visible","OpenAndRepair","NoEncodingDialog"],[input,true,false,true,false,true]);
                    if(!target.Get<bool>("ReadOnly"))throw new IOException("Word did not open the source read-only.");
                    target.Call("Activate");active=app.Object("ActiveWindow");var content=target.Object("Content");var paragraphs=content.Object("Paragraphs");
                    if(paragraphs.Get<int>("Count")<paragraph)throw new InvalidDataException("Word source paragraph count mismatch.");
                    var visible=new List<string>();OfficeObject? selected=null;
                    for(int n=1;n<=paragraph;n++){token.ThrowIfCancellationRequested();var p=paragraphs.Object("Item",true,n);var r=p.Object("Range");visible.Add(r.Get<string>("Text"));selected=r;}
                    if(!CryptographicOperations.FixedTimeEquals(expectedPrefix!,ParagraphDigest(visible)))throw new InvalidDataException("Word paragraph structure/text no longer maps exactly; no wrong paragraph selected.");
                    selected!.Call("Select");var selection=active.Object("Selection");if(selection.Get<int>("Start")!=selected.Get<int>("Start")||selection.Get<int>("End")!=selected.Get<int>("End"))throw new IOException("Word selection oracle failed.");
                    window.Object("Document").Call("Close",0);
                }
                else
                {
                    app.Set("EnableEvents",false);var books=app.Object("Workbooks");
                    target=books.NamedObject("Open",["Filename","UpdateLinks","ReadOnly","IgnoreReadOnlyRecommended","Notify","AddToMru","CorruptLoad"],[input,0,true,true,false,false,0]);
                    if(!target.Get<bool>("ReadOnly"))throw new IOException("Excel did not open the source read-only.");
                    var sheets=target.Object("Worksheets");var tab=sheets.Object("Item",false,sheet);tab.Call("Activate");var rectangle=tab.Object("Range",false,range);rectangle.Call("Select");var selection=app.Object("Selection");
                    if(selection.Get<int>("Row")!=rectangle.Get<int>("Row")||selection.Get<int>("Column")!=rectangle.Get<int>("Column")||selection.Object("Rows").Get<int>("Count")!=rectangle.Object("Rows").Get<int>("Count")||selection.Object("Columns").Get<int>("Count")!=rectangle.Object("Columns").Get<int>("Count"))throw new IOException("Excel selection oracle failed.");
                    // The bootstrap workbook is only the one opened on the explicitly-owned window.
                    window.Object("ActiveSheet").Object("Parent").Call("Close",false);
                    app.Set("EnableEvents",true);active=app.Object("ActiveWindow");
                }
            }
            finally{app.Set("AutomationSecurity",2);}
            token.ThrowIfCancellationRequested();
            IntPtr userWindow=new(active.Get<int>("Hwnd"));GetWindowThreadProcessId(userWindow,out uint userPid);if(!office.ContainsProcess(userPid))throw new IOException("Precise-open target is not owned.");
            string label=word?$"Word paragraph {paragraph}":$"Excel '{sheet}'!{range}";
            var result=new OfficeHandoffResult(1,input,"handed-off",true,checked((int)userPid),userWindow.ToInt64(),label);
            using(var receipt=new FileStream(Path.Combine(output,"handoff.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))JsonSerializer.Serialize(receipt,result,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
            office.HandoffForUser(userPid);handed=true;return result;
        }
        finally
        {
            var released=new HashSet<object>(ReferenceEqualityComparer.Instance);
            for(int n=references.Count-1;n>=0;n--)if(released.Add(references[n])&&Marshal.IsComObject(references[n]))Marshal.FinalReleaseComObject(references[n]);
            if(office is not null&&!handed)
            {
                using var stop=new CancellationTokenSource();stop.Cancel();
                try{office.RunAsync(65536,TimeSpan.FromSeconds(5),stop.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){ }
            }
            if(office is{Settled:true}or{DetachedForUser:true})office.Dispose();else if(office is not null)MediaRetention.Keep(office);
        }
    }
    private static byte[] ParagraphDigest(IEnumerable<string> values)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var value in values){string text=value.Replace("\r","",StringComparison.Ordinal).Replace("\a","",StringComparison.Ordinal).Replace("\v","\n",StringComparison.Ordinal).Trim();var bytes=Encoding.UTF8.GetBytes(text);hash.AppendData(BitConverter.GetBytes(bytes.Length));hash.AppendData(bytes);}return hash.GetHashAndReset();
    }
    private static bool OfficeRangeValid(string range)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(range,@"^[A-Z]{1,3}[1-9][0-9]{0,6}(:[A-Z]{1,3}[1-9][0-9]{0,6})?$",System.Text.RegularExpressions.RegexOptions.CultureInvariant))return false;
        int firstRow=0,firstColumn=0;foreach(var cell in range.Split(':')){int col=0,n=0;while(n<cell.Length&&char.IsAsciiLetterUpper(cell[n]))col=col*26+cell[n++]-'A'+1;int row=int.Parse(cell[n..],System.Globalization.CultureInfo.InvariantCulture);if(col>16384||row>1048576||row<firstRow||col<firstColumn)return false;firstRow=row;firstColumn=col;}return true;
    }
    private static void CheckOfficeMembers(JsonElement element,string[] names)
    {
        if(element.ValueKind!=JsonValueKind.Object)throw new JsonException("Office request object required.");var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in element.EnumerateObject())if(!names.Contains(p.Name,StringComparer.Ordinal)||!seen.Add(p.Name))throw new JsonException("Unknown/duplicate Office request member.");if(seen.Count!=names.Length)throw new JsonException("Missing Office request member.");
    }
    private static void CreateOfficeBootstrap(string path,bool word)
    {
        string rel="http://schemas.openxmlformats.org/package/2006/relationships",office="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var entries=new Dictionary<string,string>{["_rels/.rels"]=$"<Relationships xmlns=\"{rel}\"><Relationship Id=\"r1\" Type=\"{office}/officeDocument\" Target=\"{(word?"word/document.xml":"xl/workbook.xml")}\"/></Relationships>"};
        string prefix="<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>";
        if(word){entries["[Content_Types].xml"]=prefix+"<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>";entries["word/document.xml"]="<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>WinSight navigation bootstrap</w:t></w:r></w:p><w:sectPr/></w:body></w:document>";}
        else{entries["[Content_Types].xml"]=prefix+"<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>";entries["xl/workbook.xml"]=$"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"{office}\"><sheets><sheet name=\"WinSightBootstrap\" sheetId=\"1\" r:id=\"r1\"/></sheets></workbook>";entries["xl/_rels/workbook.xml.rels"]=$"<Relationships xmlns=\"{rel}\"><Relationship Id=\"r1\" Type=\"{office}/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>";entries["xl/worksheets/sheet1.xml"]="<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData/></worksheet>";}
        using var zip=ZipFile.Open(path,ZipArchiveMode.Create);foreach(var entry in entries){using var writer=new StreamWriter(zip.CreateEntry(entry.Key).Open(),new UTF8Encoding(false));writer.Write(entry.Value);}
    }
}
