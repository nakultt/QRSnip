using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QRSnip.Services;

namespace QRSnip;

/// <summary>What was clicked on a <see cref="ResultCard"/>.</summary>
public enum ResultAction
{
    Open,
    Copy,
    CopySecret,
}

public sealed class ResultActionEventArgs(HistoryEntry entry, ResultAction action) : EventArgs
{
    public HistoryEntry Entry { get; } = entry;

    public ResultAction Action { get; } = action;
}

/// <summary>
/// Shows one decoded code with Open, Copy and (for Wi-Fi) Copy password. The host decides what
/// the actions do, e.g. the overlay closes itself before opening a link.
/// </summary>
public sealed partial class ResultCard : UserControl
{
    public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(
        nameof(Entry), typeof(object), typeof(ResultCard), new PropertyMetadata(null, (d, _) => ((ResultCard)d).Update()));

    public ResultCard()
    {
        InitializeComponent();
    }

    public event EventHandler<ResultActionEventArgs>? ActionInvoked;

    public HistoryEntry? Entry
    {
        get => GetValue(EntryProperty) as HistoryEntry;
        set => SetValue(EntryProperty, value);
    }

    private void Update()
    {
        if (Entry is not { } entry)
        {
            return;
        }

        KindIcon.Glyph = entry.Glyph;
        KindText.Text = entry.KindLabel;
        TitleText.Text = entry.Content.Title;
        PayloadText.Text = entry.Text;
        OpenText.Text = entry.OpenLabel;
        OpenButton.Visibility = entry.CanOpen ? Visibility.Visible : Visibility.Collapsed;
        SecretButton.Visibility = entry.HasSecret ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => Raise(ResultAction.Open);

    private void OnCopyClick(object sender, RoutedEventArgs e) => Raise(ResultAction.Copy);

    private void OnCopySecretClick(object sender, RoutedEventArgs e) => Raise(ResultAction.CopySecret);

    private void Raise(ResultAction action)
    {
        if (Entry is { } entry)
        {
            ActionInvoked?.Invoke(this, new ResultActionEventArgs(entry, action));
        }
    }
}
