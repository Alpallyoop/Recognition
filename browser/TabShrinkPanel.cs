using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Recognition.Browser
{
    // The tab row, Chrome style: tabs always overlap their neighbour a little (the selected tab sits on top), a tab is at most MaxTab wide, and once
    // the row is full every tab gets narrower together (long titles are cut with "...") so all of them stay on one row.
    public sealed class TabShrinkPanel : Panel
    {
        private const double MinTab = 72, MaxTab = 220, Overlap = 14;
        private readonly List<double> _natural = new();

        private static double[] Widths(IReadOnlyList<double> nat, double avail)
        {
            var n = nat.Count;
            var w = nat.Select(x => Math.Min(MaxTab, x)).ToArray();
            if (n == 0 || double.IsInfinity(avail) || double.IsNaN(avail)) return w;
            double budget = avail + Overlap * (n - 1);   // the overlaps give back some room
            if (w.Sum() <= budget) return w;
            double remaining = budget; int left = n; double cap = remaining / n;
            foreach (var x in w.OrderBy(v => v))
            {
                if (x * left <= remaining) { remaining -= x; left--; }
                else { cap = remaining / left; break; }
            }
            for (int i = 0; i < n; i++) w[i] = Math.Max(MinTab, Math.Min(w[i], cap));
            return w;
        }

        private List<UIElement> Kids() => InternalChildren.Cast<UIElement>().Where(k => k.Visibility != Visibility.Collapsed).ToList();

        protected override Size MeasureOverride(Size constraint)
        {
            var kids = Kids();
            _natural.Clear();
            foreach (var k in kids) { k.Measure(new Size(double.PositiveInfinity, constraint.Height)); _natural.Add(k.DesiredSize.Width); }
            var w = Widths(_natural, constraint.Width);
            double total = 0, h = 0;
            for (int i = 0; i < kids.Count; i++)
            {
                kids[i].Measure(new Size(w[i], constraint.Height));
                h = Math.Max(h, kids[i].DesiredSize.Height);
                total += w[i] - (i < kids.Count - 1 ? Overlap : 0);
            }
            return new Size(total, h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var kids = Kids();
            if (_natural.Count != kids.Count) return finalSize;
            var w = Widths(_natural, finalSize.Width);
            double x = 0;
            for (int i = 0; i < kids.Count; i++)
            {
                kids[i].Arrange(new Rect(x, 0, w[i], finalSize.Height));
                x += w[i] - Overlap;
            }
            return finalSize;
        }
    }

    // MaxWidth = the width of the row minus room for the "+" button.
    public sealed class MinusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double v = value is double d ? d : 0, p = 0;
            if (parameter != null) double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out p);
            return Math.Max(160, v - p);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
