using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace ConversationManager;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>When true, the false case collapses instead of hiding.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}

public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value is string s ? !string.IsNullOrWhiteSpace(s) : value is not null;
        if (Invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Turns a TextBox's Padding into the margin a placeholder hint needs to sit exactly where the
/// caret does. WPF's TextBoxView draws text a fixed 2px further left-inset than Padding alone
/// accounts for, so a hint positioned on Padding lands 2px to the left of the first character.
/// </summary>
public sealed class HintMarginConverter : IValueConverter
{
    /// <summary>The TextBoxView inset. Pinned by a test; change only if that test says so.</summary>
    public double LeftInset { get; set; } = 2;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Thickness padding) return new Thickness(LeftInset, 0, 0, 0);
        return new Thickness(padding.Left + LeftInset, padding.Top, padding.Right, padding.Bottom);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Picks one of two resources by a boolean, keeping colour choices out of code-behind.</summary>
public sealed class BoolToResourceConverter : IValueConverter
{
    public object? TrueValue { get; set; }
    public object? FalseValue { get; set; }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b ? TrueValue : FalseValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Draws a snippet with the matched words picked out.
///
/// A bound string cannot carry formatting, so the run structure is built here from the offsets
/// the search already worked out. Without it the user has to re-find their own search term in
/// every result line - which is most of the work of scanning a list of hits.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlight),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty StartProperty = DependencyProperty.RegisterAttached(
        "Start", typeof(int), typeof(Highlight), new PropertyMetadata(0, OnChanged));

    public static readonly DependencyProperty LengthProperty = DependencyProperty.RegisterAttached(
        "Length", typeof(int), typeof(Highlight), new PropertyMetadata(0, OnChanged));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached(
        "Brush", typeof(Brush), typeof(Highlight), new PropertyMetadata(null, OnChanged));

    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);

    public static void SetStart(DependencyObject o, int v) => o.SetValue(StartProperty, v);
    public static int GetStart(DependencyObject o) => (int)o.GetValue(StartProperty);

    public static void SetLength(DependencyObject o, int v) => o.SetValue(LengthProperty, v);
    public static int GetLength(DependencyObject o) => (int)o.GetValue(LengthProperty);

    public static void SetBrush(DependencyObject o, Brush? v) => o.SetValue(BrushProperty, v);
    public static Brush? GetBrush(DependencyObject o) => (Brush?)o.GetValue(BrushProperty);

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock block) return;
        Apply(block, GetText(block), GetStart(block), GetLength(block), GetBrush(block));
    }

    /// <summary>
    /// Rebuilds the inlines. Public and static so the layout tests can check the split without
    /// standing up a window.
    /// </summary>
    public static void Apply(TextBlock block, string? text, int start, int length, Brush? brush)
    {
        block.Inlines.Clear();
        if (string.IsNullOrEmpty(text)) return;

        // Offsets come from a search over the same string, but a snippet can be rebuilt while a
        // stale offset is still attached, so they are clamped rather than trusted.
        if (length <= 0 || start < 0 || start >= text.Length)
        {
            block.Inlines.Add(new Run(text));
            return;
        }
        length = Math.Min(length, text.Length - start);

        if (start > 0) block.Inlines.Add(new Run(text[..start]));

        block.Inlines.Add(new Run(text.Substring(start, length))
        {
            Foreground = brush ?? block.Foreground,
            FontWeight = FontWeights.Bold,
        });

        var end = start + length;
        if (end < text.Length) block.Inlines.Add(new Run(text[end..]));
    }
}
