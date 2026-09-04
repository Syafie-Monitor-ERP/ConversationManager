using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ConversationManager.UiTests;

/// <summary>
/// Offscreen checks for the two bits of UI that fail silently: the placeholder alignment in the
/// search box, and the inline splitting that picks matched words out of a snippet. A regression in
/// either looks like nothing at all in a diff.
/// </summary>
internal static class Program
{
    private const string Hint = "Which conversation was that?  A branch, a PR number, or something you said...";

    private static int _pass;
    private static int _fail;

    [STAThread]
    private static int Main()
    {
        // An Application is needed for resource lookup; it is never shown.
        var app = new Application();
        var theme = (ResourceDictionary)Application.LoadComponent(
            new Uri("/ConversationManager.UiTests;component/theme.xaml", UriKind.Relative));
        app.Resources.MergedDictionaries.Add(theme);

        var placeholderStyle = (Style)theme["PlaceholderTextBox"];

        Section("The theme carries what the cards need");
        {
            Check("a brush for matched words", theme["Hit"] is SolidColorBrush);
            Check("a converter for weak evidence", theme["DeepBrush"] is BoolToResourceConverter);

            var deep = (BoolToResourceConverter)theme["DeepBrush"];
            // Muted for a command-output hit, accent for a real one: the whole point is that the
            // two never look alike.
            Check("weak and strong hits get different brushes",
                !ReferenceEquals(deep.TrueValue, deep.FalseValue));
            Check("weak is the muted one",
                ReferenceEquals(deep.TrueValue, theme["Muted"]));
        }

        Section("Highlighting a match inside a snippet");
        {
            var hit = (Brush)theme["Hit"];

            var middle = new TextBlock();
            Highlight.Apply(middle, "the duplex setting is ignored", 4, 6, hit);
            var runs = middle.Inlines.OfType<Run>().ToList();
            Check("splits into before, match, after", runs.Count == 3, runs.Count.ToString());
            Check("the matched run is the term",
                runs.Count == 3 && runs[1].Text == "duplex", runs.Count == 3 ? runs[1].Text : "-");
            Check("the matched run is bold",
                runs.Count == 3 && runs[1].FontWeight == FontWeights.Bold);
            Check("the matched run carries the hit brush",
                runs.Count == 3 && ReferenceEquals(runs[1].Foreground, hit));
            Check("no character is lost",
                string.Concat(runs.Select(r => r.Text)) == "the duplex setting is ignored",
                string.Concat(runs.Select(r => r.Text)));
            Check("only the match is bold",
                runs.Count == 3 && runs[0].FontWeight != FontWeights.Bold &&
                runs[2].FontWeight != FontWeights.Bold);

            var atStart = new TextBlock();
            Highlight.Apply(atStart, "duplex is broken", 0, 6, hit);
            Check("a match at the start makes two runs",
                atStart.Inlines.Count == 2, atStart.Inlines.Count.ToString());
            Check("and starts bold",
                atStart.Inlines.OfType<Run>().First().FontWeight == FontWeights.Bold);

            var atEnd = new TextBlock();
            Highlight.Apply(atEnd, "broken is duplex", 10, 6, hit);
            Check("a match at the end makes two runs",
                atEnd.Inlines.Count == 2, atEnd.Inlines.Count.ToString());
            Check("and ends bold",
                atEnd.Inlines.OfType<Run>().Last().FontWeight == FontWeights.Bold);

            var whole = new TextBlock();
            Highlight.Apply(whole, "duplex", 0, 6, hit);
            Check("a whole-string match is one bold run",
                whole.Inlines.Count == 1 &&
                whole.Inlines.OfType<Run>().First().FontWeight == FontWeights.Bold,
                whole.Inlines.Count.ToString());
        }

        Section("Highlighting never trusts the offsets it is given");
        {
            var hit = (Brush)theme["Hit"];

            // A card can be recycled while a stale offset from the previous snippet is attached.
            var past = new TextBlock();
            Highlight.Apply(past, "short", 400, 6, hit);
            Check("an offset past the end falls back to plain text",
                past.Inlines.Count == 1 &&
                past.Inlines.OfType<Run>().First().Text == "short",
                past.Inlines.Count.ToString());

            var over = new TextBlock();
            Highlight.Apply(over, "short", 2, 900, hit);
            Check("a length past the end is clamped, not thrown",
                string.Concat(over.Inlines.OfType<Run>().Select(r => r.Text)) == "short",
                string.Concat(over.Inlines.OfType<Run>().Select(r => r.Text)));

            var none = new TextBlock();
            Highlight.Apply(none, "no match here", 0, 0, hit);
            Check("a zero-length match is plain text", none.Inlines.Count == 1);

            var negative = new TextBlock();
            Highlight.Apply(negative, "no match here", -1, 4, hit);
            Check("a negative offset is plain text", negative.Inlines.Count == 1);

            var empty = new TextBlock();
            Highlight.Apply(empty, "", 0, 3, hit);
            Check("empty text renders nothing", empty.Inlines.Count == 0);

            var nullText = new TextBlock();
            Highlight.Apply(nullText, null, 0, 3, hit);
            Check("null text renders nothing", nullText.Inlines.Count == 0);

            // An ellipsis is one char in a snippet but two bytes; offsets must stay char-based.
            var trimmed = new TextBlock();
            Highlight.Apply(trimmed, "…and then duplex again", 10, 6, hit);
            Check("an elided snippet highlights the right word",
                trimmed.Inlines.OfType<Run>().Any(r => r.Text == "duplex"),
                string.Join("|", trimmed.Inlines.OfType<Run>().Select(r => r.Text)));
        }

        Section("Re-highlighting replaces, never appends");
        {
            var hit = (Brush)theme["Hit"];
            var block = new TextBlock();
            Highlight.Apply(block, "the duplex setting", 4, 6, hit);
            Highlight.Apply(block, "the tray setting", 4, 4, hit);
            Check("only the newest snippet is shown",
                string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text)) == "the tray setting",
                string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text)));

            // Through the attached properties, which is how the cards actually drive it.
            var bound = new TextBlock();
            Highlight.SetBrush(bound, hit);
            Highlight.SetStart(bound, 4);
            Highlight.SetLength(bound, 6);
            Highlight.SetText(bound, "the duplex setting");
            Check("attached properties drive the same split",
                bound.Inlines.Count == 3, bound.Inlines.Count.ToString());
            Check("and the term is picked out",
                bound.Inlines.OfType<Run>().ElementAt(1).Text == "duplex",
                bound.Inlines.OfType<Run>().ElementAt(1).Text);
        }

        Section("The scope switch shows which side is active");
        {
            // This replaced two ComboBoxes whose selected text rendered near-white on the default
            // light combo chrome - invisible, and invisible in a diff too. So: pin it.
            var style = (Style)theme["SegmentButton"];

            var active = BuildSegment(style, isActive: true);
            var inactive = BuildSegment(style, isActive: false);

            var activeFill = SegmentFill(active);
            var inactiveFill = SegmentFill(inactive);

            Check("the active side is filled", activeFill is SolidColorBrush,
                activeFill?.ToString());
            Check("the inactive side is not", inactiveFill is null ||
                (inactiveFill as SolidColorBrush)?.Color.A == 0 ||
                !Equals(activeFill?.ToString(), inactiveFill.ToString()),
                inactiveFill?.ToString());
            Check("the active label clears WCAG AA against its fill",
                Contrast(active.Foreground, activeFill) >= 4.5,
                Contrast(active.Foreground, activeFill).ToString("0.0"));
            Check("the active label is the brighter of the two",
                Luminance(active.Foreground) > Luminance(inactive.Foreground),
                $"{Luminance(active.Foreground):0.00} vs {Luminance(inactive.Foreground):0.00}");
            Check("both sides are the same height",
                Math.Abs(active.ActualHeight - inactive.ActualHeight) < 0.01,
                $"{active.ActualHeight} vs {inactive.ActualHeight}");
        }

        Section("Delete reads as a link until you reach for it");
        {
            // A row of red buttons would shout about the one action nobody usually wants, so the
            // delete links sit quiet with the others and only colour under the pointer. Both
            // halves of that are easy to lose in a theme edit and invisible in a diff.
            var link = (Style)theme["LinkButton"];
            var danger = (Style)theme["DangerLinkButton"];

            var plain = BuildLink(link, "Copy id");
            var remove = BuildLink(danger, "Delete");

            Check("at rest it is the same muted colour as its neighbours",
                Equals(plain.Foreground, remove.Foreground),
                $"{plain.Foreground} vs {remove.Foreground}");
            Check("and the same height, so the row stays level",
                Math.Abs(plain.ActualHeight - remove.ActualHeight) < 0.01,
                $"{plain.ActualHeight} vs {remove.ActualHeight}");

            var hover = HoverTrigger(danger);
            Check("hovering it exists as a state at all", hover is not null);
            Check("and turns the word red",
                Colour(hover, Control.ForegroundProperty) == ((SolidColorBrush)theme["Red"]).Color,
                Colour(hover, Control.ForegroundProperty).ToString());
            Check("the plain link never goes red",
                Colour(HoverTrigger(link), Control.ForegroundProperty) !=
                ((SolidColorBrush)theme["Red"]).Color);
        }

        Section("Placeholder shows and hides");
        {
            var empty = Build(placeholderStyle, "");
            var hint = FindChild<TextBlock>(empty);
            Check("hint exists inside the template", hint is not null);
            Check("visible when empty", hint?.Visibility == Visibility.Visible, hint?.Visibility.ToString());
            Check("text comes from Tag", hint?.Text == Hint, hint?.Text);

            var typed = Build(placeholderStyle, "166597");
            Check("hidden once text is typed",
                FindChild<TextBlock>(typed)?.Visibility == Visibility.Collapsed);
        }

        Section("Caret sits exactly where the placeholder starts");
        {
            var empty = Build(placeholderStyle, "");
            var hint = FindChild<TextBlock>(empty)!;
            var hintX = hint.TransformToAncestor(empty).Transform(new Point(0, 0)).X;
            var caretX = Build(placeholderStyle, "").GetRectFromCharacterIndex(0).X;

            Check($"hint and caret share an x ({hintX:0.##} vs {caretX:0.##})",
                Math.Abs(hintX - caretX) < 0.5, $"{Math.Abs(hintX - caretX):0.##}px apart");
        }

        Section("The TextBoxView inset that HintMargin compensates for");
        {
            // HintMargin adds a hard-coded 2px. If WPF ever changes it, this fails loudly instead
            // of the hint quietly drifting off the caret.
            var offsets = new List<double>();
            foreach (var fontSize in new double[] { 12, 13, 14, 20 })
            foreach (var pad in new double[] { 0, 6, 10 })
            {
                var box = Build(placeholderStyle, "W", fontSize, new Thickness(pad, 8, 30, 8));
                offsets.Add(box.GetRectFromCharacterIndex(0).X - (box.BorderThickness.Left + pad));
            }

            Check($"inset is 2px everywhere (min {offsets.Min():0.##}, max {offsets.Max():0.##})",
                offsets.All(o => Math.Abs(o - 2) < 0.01),
                string.Join(",", offsets.Distinct().Select(o => o.ToString("0.##"))));
        }

        Section("The search box does not change size as you type");
        {
            var empty = Build(placeholderStyle, "");
            var typed = Build(placeholderStyle, "166597");
            Check("same width empty and typed",
                Math.Abs(empty.ActualWidth - typed.ActualWidth) < 0.01,
                $"{empty.ActualWidth} vs {typed.ActualWidth}");
            Check("fills the space it is given",
                Math.Abs(empty.ActualWidth - 620) < 0.01, empty.ActualWidth.ToString());
            Check("right padding leaves room for the clear button",
                empty.Padding.Right >= 28, empty.Padding.Right.ToString());
        }

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private static TextBox Build(
        Style style, string text, double fontSize = 14, Thickness? padding = null)
    {
        var box = new TextBox
        {
            Style = style,
            Tag = Hint,
            FontSize = fontSize,
            Padding = padding ?? new Thickness(10, 8, 30, 8),
            Text = text,
        };

        // Mirrors the header: a stretched Grid capped at 620.
        var host = new Grid { Width = 620, Height = 44 };
        host.Children.Add(box);
        host.Measure(new Size(620, 44));
        host.Arrange(new Rect(0, 0, 620, 44));
        host.UpdateLayout();
        return box;
    }

    /// <summary>A card's link button, laid out offscreen the way the action row builds it.</summary>
    private static Button BuildLink(Style style, string caption)
    {
        var button = new Button { Style = style, Content = caption };
        var host = new Border { Width = 120, Height = 26, Child = button };
        host.Measure(new Size(120, 26));
        host.Arrange(new Rect(0, 0, 120, 26));
        host.UpdateLayout();
        return button;
    }

    /// <summary>The template trigger that fires while the pointer is over the button.</summary>
    private static Trigger? HoverTrigger(Style style)
    {
        var template = style.Setters.OfType<Setter>()
            .Where(s => s.Property == Control.TemplateProperty)
            .Select(s => s.Value as ControlTemplate)
            .LastOrDefault(t => t is not null);

        return template?.Triggers.OfType<Trigger>()
            .FirstOrDefault(t => t.Property == UIElement.IsMouseOverProperty);
    }

    /// <summary>The colour a trigger paints onto one property, or transparent if it sets none.</summary>
    private static Color Colour(Trigger? trigger, DependencyProperty property)
    {
        var setter = trigger?.Setters.OfType<Setter>()
            .LastOrDefault(s => s.Property == property && s.TargetName is null);

        return setter?.Value is SolidColorBrush brush ? brush.Color : Colors.Transparent;
    }

    /// <summary>A segment button laid out offscreen, active or not, as the header uses it.</summary>
    private static Button BuildSegment(Style style, bool isActive)
    {
        var button = new Button { Style = style, Content = "your prompts", Tag = isActive };
        var host = new Border { Width = 140, Height = 34, Child = button };
        host.Measure(new Size(140, 34));
        host.Arrange(new Rect(0, 0, 140, 34));
        host.UpdateLayout();
        return button;
    }

    /// <summary>The fill the template applied, which is where the active state actually shows.</summary>
    private static Brush? SegmentFill(Button button) =>
        (button.Template.FindName("Bd", button) as Border)?.Background;

    /// <summary>
    /// WCAG relative luminance. The channels have to be linearised first - comparing raw sRGB
    /// bytes badly understates the contrast of light text on a dark fill, which is every colour
    /// pair in this app.
    /// </summary>
    private static double Luminance(Brush? brush)
    {
        if (brush is not SolidColorBrush s) return 0;

        static double Channel(byte v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(s.Color.R) +
               0.7152 * Channel(s.Color.G) +
               0.0722 * Channel(s.Color.B);
    }

    /// <summary>WCAG contrast ratio, enough to catch text drawn on top of its own colour.</summary>
    private static double Contrast(Brush? a, Brush? b)
    {
        var la = Luminance(a) + 0.05;
        var lb = Luminance(b) + 0.05;
        return la > lb ? la / lb : lb / la;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private static void Section(string title) => Console.WriteLine($"\n{title}");

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : "  -> " + detail)}");
        }
    }
}
