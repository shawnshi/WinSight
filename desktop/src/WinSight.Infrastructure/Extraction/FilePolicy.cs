namespace WinSight.Infrastructure.Extraction;

internal static class FilePolicy
{
    internal static readonly HashSet<string> Documents = Set("txt text md markdown rtf docx doc odt html htm");
    internal static readonly HashSet<string> Images = Set("jpg jpeg jpe jfif png gif bmp tif tiff webp heic heif avif psd tga pnm ppm pgm cr2 nef arw dng raf orf rw2");
    internal static readonly HashSet<string> Audio = Set("mp3 m4a m4b aac wav wave flac ogg oga opus aiff aif wma amr caf mka");
    internal static readonly HashSet<string> Video = Set("mp4 m4v mov mkv webm avi wmv flv 3gp 3g2 mpg mpeg m2v ogv mxf m2ts");
    internal static readonly HashSet<string> Sources = Set("py pyi pyx c h cc cpp cxx hpp hh hxx m mm swift ts tsx mts cts js jsx mjs cjs go rs java kt kts scala groovy rb php cs fs sh bash zsh fish ps1 lua pl pm r jl dart sql vue svelte astro html htm css scss sass less gradle cmake mk proto graphql gql tf hcl nix zig hs ex exs erl clj cljs elm ml mli vim el lisp asm s metal glsl hlsl wgsl cu cuh ino v sv tex");
    private static readonly HashSet<string> CodeText = Set("md markdown rst adoc org txt yaml yml toml ipynb");
    private static readonly HashSet<string> CodeNames = Set("Makefile GNUmakefile Dockerfile Containerfile Justfile Rakefile Gemfile Podfile Brewfile Procfile Vagrantfile Jenkinsfile");
    private static readonly HashSet<string> SecretTypes = Set("pem key p12 pfx keystore jks ppk kdbx gpg asc crt cer der p8 mobileprovision");
    private static readonly string[] SecretPrefixes = ["id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".env", "secret", "credential", ".netrc", ".npmrc", ".pypirc", "htpasswd", ".htpasswd"];
    internal static readonly HashSet<string> SkippedFolders = Set("node_modules bower_components venv site-packages __pycache__ DerivedData Pods Carthage");
    internal static readonly HashSet<string> CodeSkippedFolders = Set("vendor vendored third_party third-party thirdparty 3rdparty external extern deps build dist out target obj coverage htmlcov generated __generated__ wandb mlruns lightning_logs");
    private static readonly HashSet<string> Packages = Set("app framework bundle plugin appex xpc kext xcodeproj xcworkspace playground photoslibrary musiclibrary tvlibrary imovielibrary fcpbundle logicx band pkg dmg noindex");
    internal static readonly string[] ProjectMarkers = [".git", "package.json", "Package.swift", "pyproject.toml", "setup.py", "requirements.txt", "Cargo.toml", "go.mod", "pom.xml", "build.gradle", "build.gradle.kts", "Gemfile", "composer.json", "CMakeLists.txt"];

    internal static HashSet<string> Set(string words) => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
    internal static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    internal static bool IsSecret(string path) => SecretTypes.Contains(Extension(path)) || SecretPrefixes.Any(p => Path.GetFileName(path).StartsWith(p, StringComparison.OrdinalIgnoreCase));
    internal static string? Kind(string path, bool code)
    {
        if (IsSecret(path)) return null;
        var ext = Extension(path);
        var name = Path.GetFileName(path);
        if (code)
        {
            if (name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".min.mjs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase)
                || new[] { "license", "licence", "copying", "notice" }.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return null;
            return Sources.Contains(ext) || CodeText.Contains(ext) || CodeNames.Contains(name) || name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) ? "code" : null;
        }
        if (Documents.Contains(ext)) return "doc";
        if (ext == "pdf") return "pdf";
        if (Images.Contains(ext)) return IsScreenshot(path) ? "screenshot" : "image";
        if (Video.Contains(ext)) return "video";
        if (Audio.Contains(ext)) return "audio";
        return null;
    }
    internal static bool IsScreenshot(string path) => new[] { "Screenshot", "Screen Shot", "CleanShot", "SCR-" }.Any(p => Path.GetFileName(path).StartsWith(p, StringComparison.OrdinalIgnoreCase));
    internal static bool SkipFolder(string path, bool code) => Path.GetFileName(path).StartsWith('.') || Packages.Contains(Extension(path))
        || SkippedFolders.Contains(Path.GetFileName(path)) || (code && CodeSkippedFolders.Contains(Path.GetFileName(path)));
    internal static bool Inside(string path, string folder) => path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static bool Under(string path, string folder) => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || Inside(path, folder);
    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("Only absolute, non-device filesystem paths are allowed.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    internal static bool Unsafe(FileAttributes attributes) => (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Device | (FileAttributes)0x00440000)) != 0;
}
