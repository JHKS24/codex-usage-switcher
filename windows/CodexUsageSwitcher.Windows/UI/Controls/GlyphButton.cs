namespace CodexUsageSwitcher.Windows.UI.Controls;

internal sealed class GlyphButton : Button
{
    public GlyphButton(string glyph, string accessibleName)
    {
        Text = glyph;
        AccessibleName = accessibleName;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Theme.Hover;
        FlatAppearance.MouseDownBackColor = Theme.Selected;
        BackColor = Theme.Card;
        ForeColor = Theme.Primary;
        Font = Theme.Font(10F, FontStyle.Bold);
        TextAlign = ContentAlignment.MiddleCenter;
        Cursor = Cursors.Hand;
        Margin = new Padding(3, 10, 0, 10);
        Size = new Size(30, 30);
    }
}
