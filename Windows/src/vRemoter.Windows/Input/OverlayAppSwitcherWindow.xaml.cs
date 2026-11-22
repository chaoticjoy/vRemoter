using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using vRemoter.Core.Localization;
using vRemoter.Windows.SystemSupport;

namespace vRemoter.Windows.Input;

public partial class OverlayAppSwitcherWindow : Window
{
    private static readonly Brush SelectedBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x2F, 0x26));
    private static readonly Brush SelectedBorder = new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0x87));
    private static readonly Brush NormalBg = new SolidColorBrush(Color.FromRgb(0x1D, 0x20, 0x24));
    private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(0x2B, 0x2E, 0x34));
    private static readonly Brush PrimaryText = new SolidColorBrush(Color.FromRgb(0xED, 0xF0, 0xF2));
    private static readonly Brush SecondaryText = new SolidColorBrush(Color.FromRgb(0x8C, 0x91, 0x9B));

    public OverlayAppSwitcherWindow()
    {
        InitializeComponent();
        WindowsVersionInfo.ApplyDarkWindowFrame(this);
        HeaderText.Text = L10n.Text("切换应用（1 秒后自动切换）", "Switch App (auto-commits in 1s)");
    }

    public void UpdateItems(IReadOnlyList<SwitcherWindowItem> items, int selectedIndex)
    {
        ItemsHost.Children.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool selected = i == selectedIndex;

            var card = new Border
            {
                Width = 140,
                Margin = new Thickness(4),
                Padding = new Thickness(10, 9, 10, 9),
                CornerRadius = new CornerRadius(10),
                Background = selected ? SelectedBg : NormalBg,
                BorderBrush = selected ? SelectedBorder : NormalBorder,
                BorderThickness = new Thickness(selected ? 1.5 : 1)
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.ProcessName) ? item.Title : item.ProcessName,
                Foreground = PrimaryText,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(new TextBlock
            {
                Text = item.Title,
                Foreground = SecondaryText,
                FontSize = 10.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            card.Child = stack;
            ItemsHost.Children.Add(card);
        }
    }
}

