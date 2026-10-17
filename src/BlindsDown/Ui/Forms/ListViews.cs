namespace BlindsDown.Ui.Forms;

/// <summary>ListView helpers shared by the Settings and Activity windows.</summary>
internal static class ListViews
{
    /// <summary>
    /// Keeps the columns fitted: fully when the list is created, and to the list's width whenever it is
    /// resized. Owners call <see cref="FitColumns"/> again after a DPI change or new data, because column
    /// widths are pixels that WinForms does not rescale. When the columns do not fit, the
    /// <paramref name="flexibleColumn"/> gives up width first (its full text stays in the item tooltip).
    /// </summary>
    public static void AttachAutoFit(ListView list, int flexibleColumn = -1)
    {
        // Posted: HandleCreated is raised before WinForms has inserted the items into the native control.
        list.HandleCreated += (_, _) => list.BeginInvoke(() => FitColumns(list, flexibleColumn));
        list.Resize += (_, _) => FitToWidth(list, flexibleColumn);
    }

    /// <summary>Sizes every column to the wider of its header and its content, then fits them to the list's width.</summary>
    public static void FitColumns(ListView list, int flexibleColumn = -1)
    {
        if (!list.IsHandleCreated || list.Columns.Count == 0)
        {
            return;
        }

        list.BeginUpdate();
        try
        {
            int extra = list.LogicalToDeviceUnits(8);
            int headerPadding = list.LogicalToDeviceUnits(14);
            foreach (ColumnHeader column in list.Columns)
            {
                // Header width is measured directly: AutoResizeColumn(HeaderSize) on the last column means "fill".
                list.AutoResizeColumn(column.Index, ColumnHeaderAutoResizeStyle.ColumnContent);
                int header = list.HeaderStyle == ColumnHeaderStyle.None ? 0 : TextRenderer.MeasureText(column.Text, list.Font).Width + headerPadding;
                column.Tag = Math.Max(column.Width, header) + extra;
            }

            FitToWidth(list, flexibleColumn);
        }
        finally
        {
            list.EndUpdate();
        }
    }

    /// <summary>
    /// Pure width arithmetic: the flexible column shrinks (down to a minimum) when the fitted widths are
    /// wider than <paramref name="available"/>, and the last column takes any width that is left over.
    /// Other columns keep their fitted width, so "Muted in software (...)" is never cut off.
    /// </summary>
    public static int[] Distribute(IReadOnlyList<int> fitted, int available, int flexibleColumn, int flexibleMinimum)
    {
        int[] widths = [.. fitted];
        int count = widths.Length;
        if (count == 0)
        {
            return widths;
        }

        if (flexibleColumn >= 0 && flexibleColumn < count)
        {
            int others = widths.Sum() - widths[flexibleColumn];
            int minimum = Math.Min(widths[flexibleColumn], flexibleMinimum);
            widths[flexibleColumn] = Math.Max(minimum, Math.Min(widths[flexibleColumn], available - others));
        }

        int beforeLast = widths.Sum() - widths[count - 1];
        widths[count - 1] = Math.Max(widths[count - 1], available - beforeLast);
        return widths;
    }

    private static void FitToWidth(ListView list, int flexibleColumn)
    {
        if (!list.IsHandleCreated || list.Columns.Count == 0)
        {
            return;
        }

        int[] fitted = list.Columns.Cast<ColumnHeader>().Select(c => c.Tag is int width ? width : c.Width).ToArray();
        int[] widths = Distribute(fitted, list.ClientSize.Width, flexibleColumn, list.LogicalToDeviceUnits(140));
        for (int i = 0; i < widths.Length; i++)
        {
            if (list.Columns[i].Width != widths[i])
            {
                list.Columns[i].Width = widths[i];
            }
        }
    }
}
