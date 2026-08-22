using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WHPO_UI.Controls
{
    /// <summary>
    /// Panel que coloca sus hijos en filas y los envuelve a la siguiente
    /// cuando no entran en el ancho disponible. Cada hijo conserva el
    /// tamaño natural de su contenido (a diferencia de ItemsWrapGrid,
    /// que mide con restricciones de ItemsControl).
    /// </summary>
    public sealed class WrapPanel : Panel
    {
        public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
            nameof(Spacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(0.0, OnSpacingChanged));

        public double Spacing
        {
            get => (double)GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }

        private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((WrapPanel)d).InvalidateMeasure();

        protected override Size MeasureOverride(Size availableSize)
        {
            var constraint = new Size(double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width,
                                      double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : availableSize.Height);

            double totalWidth = 0;
            double totalHeight = 0;
            double rowWidth = 0;
            double rowHeight = 0;
            double spacing = Spacing;

            foreach (var child in Children)
            {
                child.Measure(constraint);

                var childSize = child.DesiredSize;
                if (rowWidth + childSize.Width > constraint.Width && rowWidth > 0)
                {
                    // Salta a la siguiente fila
                    totalWidth = Math.Max(totalWidth, rowWidth);
                    totalHeight += rowHeight + spacing;
                    rowWidth = 0;
                    rowHeight = 0;
                }

                rowWidth += childSize.Width + (rowWidth > 0 ? spacing : 0);
                rowHeight = Math.Max(rowHeight, childSize.Height);
            }

            totalWidth = Math.Max(totalWidth, rowWidth);
            totalHeight += rowHeight;

            return new Size(totalWidth, totalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            double y = 0;
            double rowHeight = 0;
            double availableWidth = finalSize.Width;
            double spacing = Spacing;

            foreach (var child in Children)
            {
                var childSize = child.DesiredSize;
                if (x + childSize.Width > availableWidth && x > 0)
                {
                    x = 0;
                    y += rowHeight + spacing;
                    rowHeight = 0;
                }

                child.Arrange(new Rect(x, y, childSize.Width, childSize.Height));
                x += childSize.Width + spacing;
                rowHeight = Math.Max(rowHeight, childSize.Height);
            }

            return finalSize;
        }
    }
}
