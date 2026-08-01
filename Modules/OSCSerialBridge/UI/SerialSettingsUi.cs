using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace CrookedToe.Modules.OSCSerialBridge.UI;

internal static class SerialSettingsUi
{
    public sealed record DeviceChoice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    public static Border CreateHeaderContainer(UIElement child)
    {
        var border = new Border
        {
            Child = child,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 6)
        };
        border.SetResourceReference(Border.BackgroundProperty, "CBackground1");
        return border;
    }

    public static Border CreateRowContainer(UIElement child, int index)
    {
        var border = new Border
        {
            Child = child,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };

        border.SetResourceReference(Border.BackgroundProperty, index % 2 == 0 ? "CBackground3" : "CBackground4");
        border.SetResourceReference(Border.BorderBrushProperty, "CBackground1");
        return border;
    }

    public static TextBlock CreateHeaderText(string text, int column)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, "CForeground2");
        Grid.SetColumn(textBlock, column);
        return textBlock;
    }

    public static TextBox CreateTextBox(object source, string path)
    {
        var textBox = new TextBox
        {
            MinHeight = 28,
            Padding = new Thickness(6, 4, 6, 4),
            BorderThickness = new Thickness(1)
        };
        ApplyInputStyle(textBox);
        textBox.SetBinding(TextBox.TextProperty, CreateBinding(source, path));
        return textBox;
    }

    public static ComboBox CreateComboBox(object source, string path)
    {
        var comboBox = new ComboBox
        {
            MinHeight = 28,
            Padding = new Thickness(8, 4, 8, 4),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = CreateComboBoxTemplate()
        };
        ApplyInputStyle(comboBox);
        BindComboBoxSelectedValue(comboBox, source, path);

        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, ResolveBrush("CForeground1", Colors.White)));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, ResolveBrush("CBackground4", Color.FromRgb(26, 26, 26))));
        itemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, ResolveBrush("CBackground1", Color.FromRgb(60, 60, 60))));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        var hoverTrigger = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, ResolveBrush("CBackground3", Color.FromRgb(36, 36, 36))));
        itemStyle.Triggers.Add(hoverTrigger);

        var selectedTrigger = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, ResolveBrush("CBackground1", Color.FromRgb(54, 54, 54))));
        itemStyle.Triggers.Add(selectedTrigger);

        comboBox.ItemContainerStyle = itemStyle;

        return comboBox;
    }

    public static void BindComboBoxSelectedValue(ComboBox comboBox, object source, string path)
    {
        BindingOperations.ClearBinding(comboBox, Selector.SelectedValueProperty);
        comboBox.SetBinding(Selector.SelectedValueProperty, CreateBinding(source, path));
    }

    public static CheckBox CreateCheckBox(object source, string path)
    {
        var checkBox = new CheckBox
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        checkBox.SetResourceReference(CheckBox.ForegroundProperty, "CForeground1");
        checkBox.SetBinding(ToggleButton.IsCheckedProperty, CreateBinding(source, path));
        return checkBox;
    }

    public static Button CreateActionButton(string text, RoutedEventHandler onClick, double minWidth = 80)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = minWidth,
            MinHeight = 28,
            Padding = new Thickness(10, 4, 10, 4),
            BorderThickness = new Thickness(1)
        };
        button.SetResourceReference(Control.BackgroundProperty, "CBackground1");
        button.SetResourceReference(Control.ForegroundProperty, "CForeground1");
        button.SetResourceReference(Control.BorderBrushProperty, "CBackground5");
        button.Click += onClick;
        return button;
    }

    public static Button CreateRemoveButton(RoutedEventHandler onClick)
    {
        var button = CreateActionButton("X", onClick, 32);
        button.Padding = new Thickness(0);
        button.FontWeight = FontWeights.Bold;
        return button;
    }

    public static ScrollViewer CreateScrollViewer(UIElement content)
    {
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
            MinHeight = 180
        };
    }

    public static FrameworkElement CreateSettingLayout(
        UIElement headerRow,
        UIElement rowsContent,
        string addButtonText,
        RoutedEventHandler onAddClick,
        double addButtonWidth = 96)
    {
        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = CreateHeaderContainer(headerRow);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var rowsScroller = CreateScrollViewer(rowsContent);
        Grid.SetRow(rowsScroller, 1);
        root.Children.Add(rowsScroller);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var addButton = CreateActionButton(addButtonText, onAddClick, addButtonWidth);
        addButton.Margin = new Thickness(0, 0, 8, 0);
        buttonPanel.Children.Add(addButton);

        Grid.SetRow(buttonPanel, 2);
        root.Children.Add(buttonPanel);

        return root;
    }

    public static List<DeviceChoice> CreateDeviceChoices(IEnumerable<string> deviceNames, string? emptyChoiceLabel = null)
    {
        var choices = new List<DeviceChoice>();
        if (!string.IsNullOrWhiteSpace(emptyChoiceLabel))
            choices.Add(new DeviceChoice(string.Empty, emptyChoiceLabel));

        choices.AddRange(deviceNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new DeviceChoice(name, name)));

        return choices;
    }

    private static Binding CreateBinding(object source, string path)
    {
        return new Binding(path)
        {
            Source = source,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        };
    }

    private static void ApplyInputStyle(Control control)
    {
        control.SetResourceReference(Control.BackgroundProperty, "CBackground5");
        control.SetResourceReference(Control.ForegroundProperty, "CForeground1");
        control.SetResourceReference(Control.BorderBrushProperty, "CBackground1");
    }

    private static ControlTemplate CreateComboBoxTemplate()
    {
        var template = new ControlTemplate(typeof(ComboBox));

        var outerBorder = new FrameworkElementFactory(typeof(Border));
        outerBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        outerBorder.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        outerBorder.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        outerBorder.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });

        var layoutPanel = new FrameworkElementFactory(typeof(DockPanel));
        layoutPanel.SetValue(DockPanel.LastChildFillProperty, true);

        var dropDownButton = new FrameworkElementFactory(typeof(ToggleButton));
        dropDownButton.SetValue(FrameworkElement.NameProperty, "ToggleButton");
        dropDownButton.SetValue(DockPanel.DockProperty, Dock.Right);
        dropDownButton.SetValue(FrameworkElement.WidthProperty, 28d);
        dropDownButton.SetValue(Control.BackgroundProperty, ResolveBrush("CBackground1", Color.FromRgb(54, 54, 54)));
        dropDownButton.SetValue(Control.BorderBrushProperty, ResolveBrush("CBackground1", Color.FromRgb(54, 54, 54)));
        dropDownButton.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        dropDownButton.SetValue(ToggleButton.FocusableProperty, false);
        dropDownButton.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
        dropDownButton.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
        {
            RelativeSource = RelativeSource.TemplatedParent,
            Mode = BindingMode.TwoWay
        });

        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.SetValue(TextBlock.TextProperty, "▼");
        arrow.SetValue(TextBlock.FontSizeProperty, 10d);
        arrow.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        arrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(TextBlock.ForegroundProperty, ResolveBrush("CForeground1", Colors.White));
        dropDownButton.AppendChild(arrow);

        var selectedContent = new FrameworkElementFactory(typeof(ContentPresenter));
        selectedContent.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 4, 0));
        selectedContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        selectedContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        selectedContent.SetBinding(TextElement.ForegroundProperty, new Binding("Foreground") { RelativeSource = RelativeSource.TemplatedParent });
        selectedContent.SetBinding(ContentPresenter.ContentProperty, new Binding("SelectionBoxItem") { RelativeSource = RelativeSource.TemplatedParent });
        selectedContent.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding("SelectionBoxItemTemplate") { RelativeSource = RelativeSource.TemplatedParent });
        selectedContent.SetBinding(ContentPresenter.ContentStringFormatProperty, new Binding("SelectionBoxItemStringFormat") { RelativeSource = RelativeSource.TemplatedParent });

        var selectedContentHitArea = new FrameworkElementFactory(typeof(ToggleButton));
        selectedContentHitArea.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        selectedContentHitArea.SetValue(Control.BorderBrushProperty, Brushes.Transparent);
        selectedContentHitArea.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        selectedContentHitArea.SetValue(Control.PaddingProperty, new Thickness(0));
        selectedContentHitArea.SetBinding(Control.ForegroundProperty, new Binding("Foreground") { RelativeSource = RelativeSource.TemplatedParent });
        selectedContentHitArea.SetValue(ToggleButton.FocusableProperty, false);
        selectedContentHitArea.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
        selectedContentHitArea.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
        {
            RelativeSource = RelativeSource.TemplatedParent,
            Mode = BindingMode.TwoWay
        });
        selectedContentHitArea.AppendChild(selectedContent);

        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.SetValue(FrameworkElement.NameProperty, "PART_Popup");
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen") { RelativeSource = RelativeSource.TemplatedParent });

        var popupBorder = new FrameworkElementFactory(typeof(Border));
        popupBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        popupBorder.SetValue(Border.PaddingProperty, new Thickness(2));
        popupBorder.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        popupBorder.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        popupBorder.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
        popupBorder.SetBinding(FrameworkElement.MinWidthProperty, new Binding("ActualWidth") { RelativeSource = RelativeSource.TemplatedParent });

        var scrollViewer = new FrameworkElementFactory(typeof(ScrollViewer));
        scrollViewer.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scrollViewer.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scrollViewer.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scrollViewer.SetValue(FrameworkElement.MaxHeightProperty, 240d);

        var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        scrollViewer.AppendChild(itemsPresenter);
        popupBorder.AppendChild(scrollViewer);
        popup.AppendChild(popupBorder);

        layoutPanel.AppendChild(dropDownButton);
        layoutPanel.AppendChild(selectedContentHitArea);
        layoutPanel.AppendChild(popup);
        outerBorder.AppendChild(layoutPanel);
        template.VisualTree = outerBorder;

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65d));
        template.Triggers.Add(disabledTrigger);

        return template;
    }

    private static Brush ResolveBrush(string resourceKey, Color fallback)
    {
        return Application.Current.TryFindResource(resourceKey) as Brush ?? new SolidColorBrush(fallback);
    }
}
