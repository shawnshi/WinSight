using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinSight.Core;
using WinSight.Infrastructure.Media;

internal static class OfficePolicyCases
{
    internal static async Task<int> RunAsync(string host,string fixtureRoot,string root)
    {
        if(Directory.Exists(root))throw new IOException("Fresh policy test directory required.");Directory.CreateDirectory(root);int failed=0;var checks=new List<object>();
        var method=typeof(NativeMediaProcessClient).Assembly.GetType("WinSight.Infrastructure.Media.OfficeNavigationPolicy")!.GetMethod("Validate",BindingFlags.NonPublic|BindingFlags.Static)!;
        void Validate(string path,bool word){using var source=File.OpenRead(path);try{method.Invoke(null,[source,word,CancellationToken.None]);}catch(TargetInvocationException e)when(e.InnerException is not null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();}}
        void Check(string name,Action action,bool deny=false){try{bool refused=false;try{action();}catch(NotSupportedException){refused=true;}if(refused!=deny)throw new InvalidOperationException("Policy outcome differs from expected.");checks.Add(new{name,ok=true});}catch(Exception e){failed++;checks.Add(new{name,ok=false,error=e.ToString()});}}
        string plainWord=Path.Combine(fixtureRoot,"own.docx"),plainExcel=Path.Combine(fixtureRoot,"own.xlsx");Check("field_free_word_accepted",()=>Validate(plainWord,true));Check("formula_free_excel_accepted",()=>Validate(plainExcel,false));
        string Add(string name,string original,string part,string xml){string path=Path.Combine(root,name+Path.GetExtension(original));File.Copy(original,path);using(var zip=ZipFile.Open(path,ZipArchiveMode.Update)){using var writer=new StreamWriter(zip.CreateEntry(part).Open(),new UTF8Encoding(false));writer.Write(xml);}return path;}
        string w="http://schemas.openxmlformats.org/wordprocessingml/2006/main",x="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var simple=Add("simple-field",plainWord,"word/header1.xml",$"<w:hdr xmlns:w='{w}'><w:p><w:fldSimple w:instr='DDEAUTO ignored'/></w:p></w:hdr>");Check("word_simple_field_in_header_refused",()=>Validate(simple,true),true);
        var split=Add("split-field",plainWord,"word/header1.xml",$"<w:hdr xmlns:w='{w}'><w:p><w:r><w:fldChar w:fldCharType='begin'/><w:instrText>DD</w:instrText><w:instrText>EAUTO</w:instrText></w:r></w:p></w:hdr>");Check("word_split_instruction_refused",()=>Validate(split,true),true);
        var excel=Add("formula",plainExcel,"xl/worksheets/sheet99.xml",$"<worksheet xmlns='{x}'><sheetData><row r='1'><c r='A1'><f>WEBSERVICE(\"https://invalid.example\")</f><v>0</v></c></row></sheetData></worksheet>");Check("excel_formula_refused",()=>Validate(excel,false),true);
        var strict=Add("strict-formula",plainExcel,"xl/worksheets/sheet99.xml","<worksheet xmlns='http://purl.oclc.org/ooxml/spreadsheetml/main'><f>1+1</f></worksheet>");Check("strict_namespace_formula_refused",()=>Validate(strict,false),true);
        var named=Add("named",plainExcel,"xl/workbook-extra.xml",$"<workbook xmlns='{x}'><definedNames><definedName name='Auto_Open'>1</definedName></definedNames></workbook>");Check("excel_defined_name_refused",()=>Validate(named,false),true);
        var table=Add("table-formula",plainExcel,"xl/tables/table1.xml",$"<table xmlns='{x}'><calculatedColumnFormula>1+1</calculatedColumnFormula></table>");Check("excel_table_formula_refused",()=>Validate(table,false),true);
        var connections=Add("connections",plainExcel,"xl/connections.xml",$"<connections xmlns='{x}'/>");Check("excel_connections_refused",()=>Validate(connections,false),true);
        var workspace=Path.Combine(root,"native-work");Directory.CreateDirectory(workspace);
        await using(var client=new NativeMediaProcessClient(new(){HostExecutable=host,WorkspaceRoot=workspace}))
        {
            try{string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(simple)));await client.NavigateOfficeAsync(new(simple,hash,new("paragraph",Paragraph:2,Part:"body")));failed++;checks.Add(new{name="native_policy_denial_before_office",ok=false});}
            catch(IOException e){bool message=e.Message.Contains("field-free",StringComparison.Ordinal);bool clean=!Directory.EnumerateFileSystemEntries(workspace).Any();checks.Add(new{name="native_policy_denial_before_office",ok=message&&clean,messageMatched=message,workspaceClean=clean});if(!message||!clean)failed++;}
        }
        await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{success=failed==0,failed,checks,scope="Policy tests andone actual nativeHost request rejectedbeforeOfficebootstrap. NoWinUI orOfficeprocesslaunch, URLstringswereonlyownfixturedata/notfetched. Conservativefield/formula/namedrangeunsupported, not ageneralOfficesandbox."},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"Office policy {checks.Count-failed}/{checks.Count}");return failed==0?0:1;
    }
}
