using Microsoft.Win32;
using WinSight.Core.Diagnostics;

namespace WinSight.Infrastructure.Runtime;

/// <summary>Where the per-user login-item command is stored. The default is HKCU\...\Run; tests use memory.</summary>
public interface IAutostartStore
{
    string? Read();
    void Write(string? command);
}

/// <summary>
/// Makes the "open at login" setting real. The login item starts the app with <c>--autostart</c> so it lives in the tray;
/// an ordinary launch always shows the window. The command is repaired on every start because the installed path can
/// change between versioned deployments.
/// </summary>
public sealed class AutostartRegistration
{
    public const string AutostartArgument = "--autostart";
    private readonly IAutostartStore store;

    public AutostartRegistration(IAutostartStore? store = null) => this.store = store ?? new RegistryAutostartStore();

    public static string CommandFor(string executablePath, string dataDirectory, string defaultDataDirectory)
    {
        var command = "\"" + executablePath + "\" " + AutostartArgument;
        if (!string.Equals(Path.GetFullPath(dataDirectory).TrimEnd('\\'), Path.GetFullPath(defaultDataDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            command += " --data-dir \"" + dataDirectory + "\"";
        return command;
    }

    public bool IsRegistered => store.Read() is not null;

    /// <summary>Brings the login item in line with <paramref name="enabled"/>; returns true when it changed.</summary>
    public bool Apply(bool enabled, string command)
    {
        try
        {
            var current = store.Read();
            if (enabled)
            {
                if (string.Equals(current, command, StringComparison.Ordinal)) return false;
                store.Write(command);
                AppLog.Info("login item registered");
                return true;
            }
            if (current is null) return false;
            store.Write(null);
            AppLog.Info("login item removed");
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            AppLog.Warn("could not update the login item", error);
            return false;
        }
    }
}

internal sealed class RegistryAutostartStore : IAutostartStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinSight";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public void Write(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (command is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
