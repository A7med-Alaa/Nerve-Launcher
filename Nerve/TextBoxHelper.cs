using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Nerve
{
    public static class TextBoxHelper
    {
        public static string GetPlaceholder(DependencyObject obj) =>
                (string)obj.GetValue(PlaceholderProperty);

        public static void SetPlaceholder(DependencyObject obj, string value) =>
            obj.SetValue(PlaceholderProperty, value);

        public static readonly DependencyProperty PlaceholderProperty =
            DependencyProperty.RegisterAttached(
                "Placeholder",
                typeof(string),
                typeof(TextBoxHelper),
                new FrameworkPropertyMetadata(
                    defaultValue: null,
                    propertyChangedCallback: OnPlaceholderChanged)
                );

        private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBox textBoxControl)
            {
                if (!textBoxControl.IsLoaded)
                {
                    // Ensure that the events are not added multiple times
                    textBoxControl.Loaded -= TextBoxControl_Loaded;
                    textBoxControl.Loaded += TextBoxControl_Loaded;
                }

                textBoxControl.TextChanged -= TextBoxControl_TextChanged;
                textBoxControl.TextChanged += TextBoxControl_TextChanged;

                // If the adorner exists, invalidate it to draw the current text
                if (GetOrCreateAdorner(textBoxControl, out PlaceholderAdorner adorner))
                    adorner.InvalidateVisual();

                
            }
        }

        private static void TextBoxControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBoxControl)
            {
                textBoxControl.Loaded -= TextBoxControl_Loaded;
                GetOrCreateAdorner(textBoxControl, out _);

                // Inside your helper, ensure we listen to size changes
                textBoxControl.SizeChanged += (s, e) => {
                    if (GetOrCreateAdorner(textBoxControl, out PlaceholderAdorner adorner))
                        adorner.InvalidateVisual(); // Forces OnRender to run again with new dimensions
                };
            }

        }

        private static void TextBoxControl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBoxControl
                && GetOrCreateAdorner(textBoxControl, out PlaceholderAdorner adorner))
            {
                // Control has text. Hide the adorner.
                if (textBoxControl.Text.Length > 0)
                    adorner.Visibility = Visibility.Hidden;

                // Control has no text. Show the adorner.
                else
                    adorner.Visibility = Visibility.Visible;
            }
        }

        private static bool GetOrCreateAdorner(TextBox textBoxControl, out PlaceholderAdorner adorner)
        {
            // Get the adorner layer
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(textBoxControl);

            // If null, it doesn't exist or the control's template isn't loaded
            if (layer == null)
            {
                adorner = null;
                return false;
            }

            // Layer exists, try to find the adorner
            adorner = layer.GetAdorners(textBoxControl)?.OfType<PlaceholderAdorner>().FirstOrDefault();

            // Adorner never added to control, so add it
            if (adorner == null)
            {
                adorner = new PlaceholderAdorner(textBoxControl);
                layer.Add(adorner);
            }

            return true;
        }

        public class PlaceholderAdorner : Adorner
        {
            public PlaceholderAdorner(TextBox textBox) : base(textBox) { }

            protected override void OnRender(DrawingContext drawingContext)
            {
                TextBox textBoxControl = (TextBox)AdornedElement;
                string placeholderValue = TextBoxHelper.GetPlaceholder(textBoxControl);

                if (string.IsNullOrEmpty(placeholderValue))
                    return;

                FormattedText text = new FormattedText(
                        placeholderValue,
                        System.Globalization.CultureInfo.CurrentCulture,
                        textBoxControl.FlowDirection,
                        new Typeface(textBoxControl.FontFamily,
                                     textBoxControl.FontStyle,
                                     textBoxControl.FontWeight,
                                     textBoxControl.FontStretch),
                        textBoxControl.FontSize,
                        SystemColors.InactiveCaptionBrush,
                        VisualTreeHelper.GetDpi(textBoxControl).PixelsPerDip);

                // Constraint for the text width based on internal padding
                text.MaxTextWidth = Math.Max(textBoxControl.ActualWidth - textBoxControl.Padding.Left - textBoxControl.Padding.Right, 10);
                text.MaxLineCount = 1;
                text.Trimming = TextTrimming.CharacterEllipsis;

                // --- VERTICAL ONLY CENTERING ---
                // Horizontal: Use the Padding.Left defined in XAML
                double xOffset = textBoxControl.Padding.Left;

                // Vertical: Calculate the center of the TextBox minus half the text height
                double yOffset = (textBoxControl.ActualHeight - text.Height) / 2;

                Point renderingOffset = new Point(xOffset, yOffset);

                drawingContext.DrawText(text, renderingOffset);
            }
        }
    }
}
