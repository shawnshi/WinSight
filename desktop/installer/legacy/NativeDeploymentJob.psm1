Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Initialize-NativeJobApi {
    if ('WinSight.DeploymentJobApi' -as [type]) { return }
    # Emit only native signatures, not C# compilation or an installed dependency.
    $assemblyName = [Reflection.AssemblyName]::new('WinSight.DeploymentJobApi')
    if ($null -ne [AppDomain].GetMethod('DefineDynamicAssembly', [type[]]@([Reflection.AssemblyName],[Reflection.Emit.AssemblyBuilderAccess]))) {
        $assembly = [AppDomain]::CurrentDomain.DefineDynamicAssembly($assemblyName, [Reflection.Emit.AssemblyBuilderAccess]::Run)
    } else {
        $assembly = [Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly($assemblyName, [Reflection.Emit.AssemblyBuilderAccess]::Run)
    }
    $builder = $assembly.DefineDynamicModule('Native').DefineType('WinSight.DeploymentJobApi', [Reflection.TypeAttributes]'Public,Sealed,Abstract')
    $signatures = @(
        @{name='CreateJobObjectW';result=[IntPtr];args=[type[]]@([IntPtr],[string])},
        @{name='SetInformationJobObject';result=[bool];args=[type[]]@([IntPtr],[int],[IntPtr],[uint32])},
        @{name='AssignProcessToJobObject';result=[bool];args=[type[]]@([IntPtr],[IntPtr])},
        @{name='TerminateJobObject';result=[bool];args=[type[]]@([IntPtr],[uint32])},
        @{name='QueryInformationJobObject';result=[bool];args=[type[]]@([IntPtr],[int],[IntPtr],[uint32],[IntPtr])},
        @{name='CloseHandle';result=[bool];args=[type[]]@([IntPtr])},
        @{name='GetTickCount';result=[uint32];args=[type[]]@()}
    )
    foreach ($signature in $signatures) {
        $method = $builder.DefinePInvokeMethod($signature.name, 'kernel32.dll', [Reflection.MethodAttributes]'Public,Static,PinvokeImpl', [Reflection.CallingConventions]::Standard, $signature.result, $signature.args, [Runtime.InteropServices.CallingConvention]::Winapi, [Runtime.InteropServices.CharSet]::Unicode)
        $method.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
        $attribute = [Runtime.InteropServices.DllImportAttribute]
        $fields = [Reflection.FieldInfo[]]@($attribute.GetField('SetLastError'))
        $method.SetCustomAttribute([Reflection.Emit.CustomAttributeBuilder]::new($attribute.GetConstructor([type[]]@([string])), [object[]]@('kernel32.dll'), $fields, [object[]]@($true)))
    }
    $null = $builder.CreateType()
}

function New-NativeDeploymentJob {
    Initialize-NativeJobApi
    $job = [WinSight.DeploymentJobApi]::CreateJobObjectW([IntPtr]::Zero, $null)
    if ($job -eq [IntPtr]::Zero) { throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
    $size = 112; if ([IntPtr]::Size -eq 8) { $size = 144 }
    $buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal($size)
    try {
        for ($i=0; $i -lt $size; $i++) { [Runtime.InteropServices.Marshal]::WriteByte($buffer, $i, 0) }
        [Runtime.InteropServices.Marshal]::WriteInt32($buffer, 16, 0x2000) # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (-not [WinSight.DeploymentJobApi]::SetInformationJobObject($job, 9, $buffer, $size)) { throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
    } catch { $null = [WinSight.DeploymentJobApi]::CloseHandle($job); throw }
    finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer) }
    $job
}

function Get-NativeJobActiveProcesses([IntPtr]$Job) {
    $buffer = [Runtime.InteropServices.Marshal]::AllocHGlobal(48)
    try {
        if (-not [WinSight.DeploymentJobApi]::QueryInformationJobObject($Job, 1, $buffer, 48, [IntPtr]::Zero)) { throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
        [Runtime.InteropServices.Marshal]::ReadInt32($buffer, 40)
    } finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer) }
}

function Get-NativeTick {
    # Inno and every PowerShell runtime must share the same native clock domain.
    # Managed Environment.TickCount is not a cross-runtime interchange format.
    Initialize-NativeJobApi
    [long][WinSight.DeploymentJobApi]::GetTickCount()
}

function Get-NativeDeadlineRemaining([long]$StartTick, [int]$DeadlineMilliseconds) {
    $elapsed = ((Get-NativeTick) - $StartTick + 4294967296L) % 4294967296L
    [int][Math]::Max(0, $DeadlineMilliseconds - $elapsed)
}

Export-ModuleMember -Function New-NativeDeploymentJob,Get-NativeJobActiveProcesses,Get-NativeTick,Get-NativeDeadlineRemaining
