using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VRCOSC.App.Utils;

namespace CrookedToe.Modules.OSCSerialBridge.UI;

public class SerialIncomingRouteModuleSettingView : UserControl
{
    private readonly OSCSerialBridgeModule module;
    private readonly SerialIncomingRouteModuleSetting moduleSetting;
    private readonly StackPanel rowsPanel;

    public SerialIncomingRouteModuleSettingView(OSCSerialBridgeModule module, SerialIncomingRouteModuleSetting moduleSetting)
    {
        this.module = module;
        this.moduleSetting = moduleSetting;

        DataContext = moduleSetting;
        rowsPanel = new StackPanel();
        Content = BuildLayout();
        RefreshRows();
        moduleSetting.Attribute.OnCollectionChanged((_, _) => RefreshRows());
        module.DevicesSetting.Attribute.OnCollectionChanged((_, _) => RefreshRows());
    }

    private FrameworkElement BuildLayout() =>
        SerialSettingsUi.CreateSettingLayout(
            CreateHeaderRow(),
            rowsPanel,
            "Add Route",
            (_, _) =>
            {
                moduleSetting.Add();
                RefreshRows();
            });

    private static List<SerialSettingsUi.DeviceChoice> BuildDeviceChoices(OSCSerialBridgeModule module) =>
        SerialSettingsUi.CreateDeviceChoices(module.DevicesSetting.Attribute.Select(device => device.Name.Value));

    private void RefreshRows()
    {
        rowsPanel.Children.Clear();
        var deviceChoices = BuildDeviceChoices(module);

        foreach (var route in moduleSetting.Attribute)
            rowsPanel.Children.Add(CreateRouteRow(route, deviceChoices, rowsPanel.Children.Count));
    }

    private FrameworkElement CreateRouteRow(SerialIncomingRoute route, List<SerialSettingsUi.DeviceChoice> deviceChoices, int index)
    {
        var grid = CreateGridLayout();

        var nameBox = SerialSettingsUi.CreateTextBox(route, "Name.Value");
        Grid.SetColumn(nameBox, 0);

        var avatarBox = SerialSettingsUi.CreateTextBox(route, "AvatarParameterName.Value");
        Grid.SetColumn(avatarBox, 2);

        var deviceBox = SerialSettingsUi.CreateComboBox(route, "DeviceName.Value");
        deviceBox.ItemsSource = deviceChoices;
        deviceBox.DisplayMemberPath = nameof(SerialSettingsUi.DeviceChoice.Label);
        deviceBox.SelectedValuePath = nameof(SerialSettingsUi.DeviceChoice.Value);
        SerialSettingsUi.BindComboBoxSelectedValue(deviceBox, route, "DeviceName.Value");
        Grid.SetColumn(deviceBox, 4);

        var enabledBox = SerialSettingsUi.CreateCheckBox(route, "Enabled.Value");
        Grid.SetColumn(enabledBox, 6);

        var removeButton = SerialSettingsUi.CreateRemoveButton((sender, args) =>
        {
            moduleSetting.Remove(route);
            RefreshRows();
        });
        Grid.SetColumn(removeButton, 8);

        grid.Children.Add(nameBox);
        grid.Children.Add(avatarBox);
        grid.Children.Add(deviceBox);
        grid.Children.Add(enabledBox);
        grid.Children.Add(removeButton);
        return SerialSettingsUi.CreateRowContainer(grid, index);
    }

    private static Grid CreateHeaderRow()
    {
        var grid = CreateGridLayout();

        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Name", 0));
        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Avatar Parameter", 2));
        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Target Device", 4));
        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Enabled", 6));

        return grid;
    }

    private static Grid CreateGridLayout()
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 84 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star), MinWidth = 110 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star), MinWidth = 100 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        return grid;
    }
}
