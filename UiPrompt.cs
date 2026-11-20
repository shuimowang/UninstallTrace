using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using static DeepUninstaller.Localization;

namespace DeepUninstaller;

internal static class UiPrompt
{
    public static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        MessageBoxResult[] choices = buttons switch
        {
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            _ => [MessageBoxResult.OK],
        };
        var answer = choices[^1];
        var dialog = new Window
        {
            Owner = owner,
            Title = title,
            Width = 480,
            MaxHeight = 550,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Language = XmlLanguage.GetLanguage(Culture.IetfLanguageTag),
        };
        var layout = new DockPanel { Margin = new Thickness(22, 22, 22, 19) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);

        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = choice switch
                {
                    MessageBoxResult.Yes => Text("继续", "Continue"),
                    MessageBoxResult.No => Text("不继续", "Do not continue"),
                    MessageBoxResult.Cancel => Text("取消", "Cancel"),
                    _ => Text("确定", "OK"),
                },
                MinWidth = 84,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = choice == (choices.Contains(defaultResult) ? defaultResult : answer),
                IsCancel = choice == choices[^1],
            };
            if (choice is MessageBoxResult.OK or MessageBoxResult.Yes)
                button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
            button.Click += (_, _) =>
            {
                answer = choice;
                dialog.DialogResult = choice is MessageBoxResult.Yes or MessageBoxResult.OK;
            };
            actions.Children.Add(button);
        }

        var body = new DockPanel();
        if (image != MessageBoxImage.None)
        {
            var messageIcon = new TextBlock
            {
                Text = image switch
                {
                    MessageBoxImage.Warning or MessageBoxImage.Error => "\uE7BA",
                    MessageBoxImage.Question => "\uE897",
                    _ => "\uE946",
                },
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 20,
                Margin = new Thickness(0, 2, 12, 0),
            };
            messageIcon.SetResourceReference(TextBlock.ForegroundProperty,
                image is MessageBoxImage.Warning or MessageBoxImage.Error ? "WarningBrush" : "AccentBrush");
            DockPanel.SetDock(messageIcon, Dock.Left);
            body.Children.Add(messageIcon);
        }

        body.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 22 },
        });
        layout.Children.Add(body);
        dialog.Content = layout;
        dialog.ShowDialog();
        return answer;
    }
}
