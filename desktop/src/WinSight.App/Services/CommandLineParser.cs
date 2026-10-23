namespace WinSight.App.Services;

public static class CommandLineParser
{
    public static bool TryParse(
        string[] args,
        out string dataDir,
        out bool isSmokeTest,
        out bool isToggle,
        out string? errorMessage)
    {
        dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSight",
            "NativePreview");
        isSmokeTest = false;
        isToggle = false;
        errorMessage = null;

        bool dataDirSeen = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (string.Equals(arg, "--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (dataDirSeen)
                {
                    errorMessage = "Duplicate argument: --data-dir may only be specified once.";
                    return false;
                }
                if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                {
                    errorMessage = "Missing value for --data-dir.";
                    return false;
                }
                string candidate = args[++i];
                if (!Path.IsPathFullyQualified(candidate))
                {
                    errorMessage = "--data-dir must be an absolute path.";
                    return false;
                }
                dataDir = Path.GetFullPath(candidate);
                dataDirSeen = true;
            }
            else if (string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase))
            {
                isSmokeTest = true;
            }
            else if (string.Equals(arg, "--toggle", StringComparison.OrdinalIgnoreCase))
            {
                isToggle = true;
            }
            else
            {
                errorMessage = $"Unknown argument: {arg}";
                return false;
            }
        }

        if (!Path.IsPathFullyQualified(dataDir))
        {
            errorMessage = "--data-dir must be an absolute path.";
            return false;
        }

        return true;
    }
}
