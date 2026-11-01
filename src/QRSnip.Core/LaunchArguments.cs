using System.Text;

namespace QRSnip.Core;

/// <summary>What QRSnip was asked to do on its command line.</summary>
/// <remarks>
/// <c>--scan</c> starts a scan in the last used mode, <c>--scan-full</c> a full screen scan,
/// <c>--camera</c> opens the camera, <c>--file &lt;path&gt;</c> scans an image file (repeatable;
/// bare picture paths are accepted too, as File Explorer's <i>Open with</i> passes them) and
/// <c>--background</c> starts in the notification area.
/// </remarks>
public sealed record LaunchArguments(bool Scan, bool FullScreen, bool Camera, bool Background, IReadOnlyList<string> Files)
{
    public static LaunchArguments Empty { get; } = new(false, false, false, false, []);

    /// <summary>True when nothing specific was asked for, so the main window should open.</summary>
    public bool IsPlainLaunch => !Scan && !FullScreen && !Camera && !Background && Files.Count == 0;

    /// <summary>Parses a raw command line (without the program name) using the Windows quoting rules.</summary>
    public static LaunchArguments Parse(string? commandLine) => Parse(Split(commandLine ?? string.Empty));

    /// <summary>Parses arguments that were already split, e.g. from <c>Environment.GetCommandLineArgs()</c>.</summary>
    public static LaunchArguments Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool scan = false, full = false, camera = false, background = false;
        var files = new List<string>();

        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var arg = e.Current;
            switch (arg.ToLowerInvariant())
            {
                case "--scan":
                    scan = true;
                    break;
                case "--scan-full":
                case "--full-screen":
                    full = true;
                    break;
                case "--camera":
                    camera = true;
                    break;
                case "--background":
                    background = true;
                    break;
                case "--file":
                    if (e.MoveNext() && e.Current.Length > 0)
                    {
                        files.Add(e.Current);
                    }

                    break;
                default:
                    // Bare arguments count only when they name a picture: unpackaged launches
                    // report the whole command line (program path included), and switches
                    // added by Velopack or Windows may carry values.
                    if (IsPicturePath(arg))
                    {
                        files.Add(arg);
                    }

                    break;
            }
        }

        return new LaunchArguments(scan, full, camera, background, files.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Picture formats Windows can usually decode (HEIC and WebP need codecs from the Store).</summary>
    public static IReadOnlyList<string> PictureExtensions { get; } =
        [".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".ico"];

    public static bool IsPicturePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !path.StartsWith('-')
        && PictureExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Splits a command line like <c>CommandLineToArgvW</c> does.</summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasArg = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    // 2n backslashes + quote: n backslashes, then the quote toggles quoting.
                    // 2n+1 backslashes + quote: n backslashes and a literal quote.
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }

                hasArg = true;
            }
            else if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"'); // "" inside quotes is a literal quote
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                hasArg = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasArg)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasArg = false;
                }
            }
            else
            {
                current.Append(c);
                hasArg = true;
            }
        }

        if (hasArg)
        {
            args.Add(current.ToString());
        }

        return args;
    }
}
