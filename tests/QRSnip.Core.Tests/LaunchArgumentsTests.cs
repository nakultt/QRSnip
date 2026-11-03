using QRSnip.Core;

namespace QRSnip.Core.Tests;

public class LaunchArgumentsTests
{
    [Theory]
    [InlineData("--scan", true, false, false, false)]
    [InlineData("--SCAN-FULL", false, true, false, false)]
    [InlineData("--camera", false, false, true, false)]
    [InlineData("--background", false, false, false, true)]
    public void Reads_switches(string commandLine, bool scan, bool full, bool camera, bool background)
    {
        var args = LaunchArguments.Parse(commandLine);

        Assert.Equal((scan, full, camera, background), (args.Scan, args.FullScreen, args.Camera, args.Background));
        Assert.False(args.IsPlainLaunch);
    }

    [Fact]
    public void Reads_files_with_spaces_and_bare_paths()
    {
        var args = LaunchArguments.Parse(@"--file ""C:\My Pictures\code.png"" D:\scan.JPG --file ""C:\My Pictures\code.png"" --file C:\any\file.dat");

        Assert.Equal([@"C:\My Pictures\code.png", @"D:\scan.JPG", @"C:\any\file.dat"], args.Files);
    }

    [Fact]
    public void Ignores_the_program_path_unknown_switches_and_their_values()
    {
        // Unpackaged launches report the whole command line, program included.
        var args = LaunchArguments.Parse(@"""C:\Users\me\AppData\Local\QRSnip\current\QRSnip.exe"" --veloapp-updated 1.1.0");

        Assert.True(args.IsPlainLaunch);
    }

    [Theory]
    [InlineData(@"C:\a\photo.heic", true)]
    [InlineData(@"photo.WEBP", true)]
    [InlineData(@"C:\a\notes.txt", false)]
    [InlineData(@"1.1.0", false)]
    [InlineData(@"--x.png", false)]
    public void Recognises_picture_paths(string path, bool expected)
    {
        Assert.Equal(expected, LaunchArguments.IsPicturePath(path));
    }

    [Fact]
    public void Merges_activation_and_process_arguments()
    {
        var merged = LaunchArguments.Parse(@"--scan C:\a.png").Merge(LaunchArguments.Parse([@"C:\A.PNG", "--background", @"C:\b.jpg"]));

        Assert.True(merged.Scan && merged.Background);
        Assert.Equal([@"C:\a.png", @"C:\b.jpg"], merged.Files);
    }

    [Fact]
    public void Empty_command_line_is_a_plain_launch()
    {
        Assert.True(LaunchArguments.Parse((string?)null).IsPlainLaunch);
        Assert.True(LaunchArguments.Parse("   ").IsPlainLaunch);
    }

    [Theory]
    [InlineData(@"a b", new[] { "a", "b" })]
    [InlineData(@"""a b"" c", new[] { "a b", "c" })]
    [InlineData(@"""C:\path with\"" x", new[] { @"C:\path with"" x" })]
    [InlineData(@"""C:\dir\\"" x", new[] { @"C:\dir\", "x" })]
    [InlineData(@"a\\\""b", new[] { @"a\""b" })]
    [InlineData(@"C:\a\b.png", new[] { @"C:\a\b.png" })]
    [InlineData(@"""""", new[] { "" })]
    [InlineData(@"""say """"hi"""""" x", new[] { @"say ""hi""", "x" })]
    public void Splits_like_CommandLineToArgvW(string commandLine, string[] expected)
    {
        Assert.Equal(expected, LaunchArguments.Split(commandLine));
    }
}
