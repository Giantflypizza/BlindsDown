namespace BlindsDown.Ui.Forms;

/// <summary>
/// Gives a control a font derived from its parent's (bold, larger) that stays derived. WinForms rescales
/// inherited fonts when a window moves to a monitor with another DPI, but a font assigned to a child
/// control stays at its old size. So instead of assigning a fixed font, the derived font is rebuilt
/// whenever the parent's font changes and after every DPI change.
/// </summary>
internal static class DerivedFont
{
    public static T Follow<T>(T control, FontStyle style, float scale)
        where T : Control
    {
        Font? own = null;
        Control? parent = null;

        void Apply()
        {
            Font? baseFont = control.Parent?.Font;
            if (baseFont is null || control.IsDisposed)
            {
                return;
            }

            var font = new Font(baseFont.FontFamily, baseFont.Size * scale, style, baseFont.Unit);
            control.Font = font;
            own?.Dispose();
            own = font;
        }

        void OnParentFontChanged(object? sender, EventArgs e) => Apply();

        control.ParentChanged += (_, _) =>
        {
            if (parent is not null)
            {
                parent.FontChanged -= OnParentFontChanged;
            }

            parent = control.Parent;
            if (parent is not null)
            {
                parent.FontChanged += OnParentFontChanged;
            }

            Apply();
        };

        // On a real monitor change Windows notifies children after the window itself, so the parent's
        // font is already rescaled here; rebuilding from it also undoes any separate scaling of ours.
        control.DpiChangedAfterParent += (_, _) => Apply();
        control.Disposed += (_, _) =>
        {
            if (parent is not null)
            {
                parent.FontChanged -= OnParentFontChanged;
            }

            own?.Dispose();
            own = null;
        };
        return control;
    }
}
