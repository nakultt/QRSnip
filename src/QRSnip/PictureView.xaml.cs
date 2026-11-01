using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using QRSnip.Core;
using QRSnip.Services;
using Windows.Foundation;

namespace QRSnip;

/// <summary>
/// Scans pictures (files, drops, pastes) and shows each one with its codes outlined, followed
/// by the same result cards as the capture overlay. Pictures are read locally and only kept
/// in memory while shown.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The token source is cancelled and replaced for each scan; it holds no unmanaged resources.")]
public sealed partial class PictureView : UserControl
{
    /// <summary>Previews are reduced to this size; the scan itself uses the full picture.</summary>
    private const int PreviewSize = 1600;

    private CancellationTokenSource? _cancel;

    public PictureView()
    {
        InitializeComponent();
    }

    /// <summary>Reads and scans the pictures one after another, adding found codes to history.</summary>
    internal async Task ShowAsync(IReadOnlyList<IImageSource> sources, AppStorage storage)
    {
        Clear();
        var cancel = _cancel = new CancellationTokenSource();
        var token = cancel.Token;
        Heading.Text = sources.Count == 1 ? sources[0].Name : $"{sources.Count} pictures";
        Loading.Visibility = Visibility.Visible;

        try
        {
            foreach (var source in sources)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                LoadingText.Text = sources.Count > 1 ? $"Reading {source.Name}…" : "Reading picture…";
                try
                {
                    var (preview, width, found) = await Task.Run(
                        async () =>
                        {
                            var image = await source.GetImageAsync(token);
                            var codes = QrScanner.ScanPicture(image, token);
                            return (ImageOps.Shrink(image, PreviewSize), image.Width, codes);
                        },
                        token);

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    storage.AddToHistory(found);
                    Sections.Children.Add(BuildSection(source.Name, preview, width, found, sources.Count > 1));
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    return; // another picture was opened, or the page was left
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                    Sections.Children.Add(BuildMessage(source.Name, "", "Couldn't read this picture", PictureDecoder.Describe(ex)));
                }
            }
        }
        finally
        {
            if (_cancel == cancel)
            {
                Loading.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Stops any scan in progress and drops the pictures.</summary>
    public void Clear()
    {
        _cancel?.Cancel();
        _cancel = null;
        Sections.Children.Clear();
        StatusText.Text = string.Empty;
    }

    private StackPanel BuildSection(string name, BgraImage preview, int fullWidth, IReadOnlyList<ScanResult> found, bool showName)
    {
        var section = new StackPanel { Spacing = 10 };
        if (showName)
        {
            section.Children.Add(new TextBlock { Text = name, Style = Resource<Style>("BodyStrongTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis });
        }

        section.Children.Add(BuildPicture(preview, (double)preview.Width / fullWidth, found));

        if (found.Count == 0)
        {
            section.Children.Add(BuildMessage(null, "", "No QR code found", "Try a sharper or larger picture, or crop it closer to the code."));
            return section;
        }

        for (var i = 0; i < found.Count; i++)
        {
            var card = new ResultCard
            {
                Entry = new HistoryEntry { Text = found[i].Text, ScannedAt = DateTimeOffset.Now, Number = found.Count > 1 ? i + 1 : null },
            };
            card.ActionInvoked += OnResultAction;
            section.Children.Add(new Border { Style = Resource<Style>("CardStyle"), Padding = new Thickness(0), Child = card });
        }

        return section;
    }

    /// <summary>The picture with an outline (and, for several codes, a number) on each code.</summary>
    private static Border BuildPicture(BgraImage preview, double scale, IReadOnlyList<ScanResult> found)
    {
        var bitmap = new WriteableBitmap(preview.Width, preview.Height);
        preview.Pixels.CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();

        var surface = new Grid { Width = preview.Width, Height = preview.Height };
        surface.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill });
        var marks = new Canvas();
        surface.Children.Add(marks);

        var accent = Resource<Brush>("AccentFillColorDefaultBrush");
        var outlines = new List<Polygon>();
        var badges = new List<FrameworkElement>();
        for (var i = 0; i < found.Count; i++)
        {
            if (found[i].Corners.Count < 3)
            {
                continue;
            }

            var points = new PointCollection();
            foreach (var c in found[i].Corners)
            {
                points.Add(new Point(c.X * scale, c.Y * scale));
            }

            var outline = new Polygon { Points = points, Stroke = accent, StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round };
            marks.Children.Add(outline);
            outlines.Add(outline);

            if (found.Count > 1)
            {
                var badge = new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new CornerRadius(12),
                    Background = accent,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 12,
                        Foreground = Resource<Brush>("TextOnAccentFillColorPrimaryBrush"),
                    },
                };
                var bounds = found[i].Bounds;
                Canvas.SetLeft(badge, (bounds.X * scale) - 12);
                Canvas.SetTop(badge, (bounds.Y * scale) - 12);
                marks.Children.Add(badge);
                badges.Add(badge);
            }
        }

        // Small pictures stay at their real size; large ones shrink to fit.
        var box = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 360, Child = surface };
        box.SizeChanged += (_, _) =>
        {
            // Keep outlines and numbers the same size on screen however much the picture shrinks.
            var factor = box.ActualWidth > 0 ? Math.Max(1, preview.Width / box.ActualWidth) : 1;
            foreach (var outline in outlines)
            {
                outline.StrokeThickness = 3 * factor;
            }

            foreach (var badge in badges)
            {
                badge.RenderTransform = new ScaleTransform { ScaleX = factor, ScaleY = factor };
            }
        };

        return new Border { Style = Resource<Style>("CardStyle"), Padding = new Thickness(8), Child = box };
    }

    private static StackPanel BuildMessage(string? name, string glyph, string title, string detail)
    {
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(4, 8, 4, 8) };
        if (name is not null)
        {
            panel.Children.Add(new TextBlock { Text = name, Style = Resource<Style>("BodyStrongTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis });
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20, Foreground = Resource<Brush>("TextFillColorSecondaryBrush") });
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Style = Resource<Style>("BodyStrongTextBlockStyle") });
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Resource<Brush>("TextFillColorSecondaryBrush"),
            Style = Resource<Style>("CaptionTextBlockStyle"),
        });
        return panel;
    }

    private async void OnResultAction(object? sender, ResultActionEventArgs e)
    {
        switch (e.Action)
        {
            case ResultAction.Open:
                await ResultPresenter.OpenAsync(e.Entry.Content);
                break;
            case ResultAction.Copy:
                ResultPresenter.Copy(e.Entry.Text);
                Flash("Copied to clipboard");
                break;
            case ResultAction.CopySecret when e.Entry.Content.Secret is { } secret:
                ResultPresenter.Copy(secret);
                Flash("Password copied");
                break;
        }
    }

    private async void Flash(string message)
    {
        StatusText.Text = message;
        await Task.Delay(1800);
        if (StatusText.Text == message)
        {
            StatusText.Text = string.Empty;
        }
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];
}
