using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VRCOSC.App.Utils;

namespace CrookedToe.Modules.OSCSerialBridge.UI;

public class SerialMappingModuleSettingView : UserControl
{
    private readonly OSCSerialBridgeModule module;
    private readonly SerialMappingModuleSetting moduleSetting;
    private readonly StackPanel rowsPanel;

    public SerialMappingModuleSettingView(OSCSerialBridgeModule module, SerialMappingModuleSetting moduleSetting)
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
            "Add Mapping",
            (_, _) =>
            {
                moduleSetting.Add();
                RefreshRows();
            });

    private void RefreshRows()
    {
        rowsPanel.Children.Clear();
        var deviceChoices = BuildDeviceChoices();

        foreach (var mapping in moduleSetting.Attribute)
            rowsPanel.Children.Add(CreateMappingRow(mapping, deviceChoices, rowsPanel.Children.Count));
    }

    private List<SerialSettingsUi.DeviceChoice> BuildDeviceChoices()
    {
        var deviceNames = moduleSetting.Attribute
            .Select(mapping => mapping.DeviceNameFilter.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Concat(module.DevicesSetting.Attribute
            .Select(device => device.Name.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name)));

        return SerialSettingsUi.CreateDeviceChoices(deviceNames, "Any configured device");
    }

    private FrameworkElement CreateMappingRow(SerialParameterMapping mapping, List<SerialSettingsUi.DeviceChoice> deviceChoices, int index)
    {
        var grid = CreateGridLayout();

        var nameBox = SerialSettingsUi.CreateTextBox(mapping, "Name.Value");
        Grid.SetColumn(nameBox, 0);

        var deviceBox = SerialSettingsUi.CreateComboBox(mapping, "DeviceNameFilter.Value");
        deviceBox.ItemsSource = deviceChoices;
        deviceBox.DisplayMemberPath = nameof(SerialSettingsUi.DeviceChoice.Label);
        deviceBox.SelectedValuePath = nameof(SerialSettingsUi.DeviceChoice.Value);
        SerialSettingsUi.BindComboBoxSelectedValue(deviceBox, mapping, "DeviceNameFilter.Value");
        Grid.SetColumn(deviceBox, 2);

        var sourceBox = SerialSettingsUi.CreateTextBox(mapping, "SourceName.Value");
        Grid.SetColumn(sourceBox, 4);

        var avatarBox = SerialSettingsUi.CreateTextBox(mapping, "AvatarParameterName.Value");
        Grid.SetColumn(avatarBox, 6);

        var enabledBox = SerialSettingsUi.CreateCheckBox(mapping, "Enabled.Value");
        Grid.SetColumn(enabledBox, 8);

        var removeButton = SerialSettingsUi.CreateRemoveButton((sender, args) =>
        {
            moduleSetting.Remove(mapping);
            RefreshRows();
        });
        Grid.SetColumn(removeButton, 10);

        grid.Children.Add(nameBox);
        grid.Children.Add(deviceBox);
        grid.Children.Add(sourceBox);
        grid.Children.Add(avatarBox);
        grid.Children.Add(enabledBox);
        grid.Children.Add(removeButton);
        return SerialSettingsUi.CreateRowContainer(grid, index);
    }

    private static Grid CreateHeaderRow()
    {
        var grid = CreateGridLayout();

        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Name", 0));
        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Source Device", 2));
        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Packet Name", 4));
        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Avatar Parameter", 6));
        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Enabled", 8));

        return grid;
    }

    private static Grid CreateGridLayout()
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star), MinWidth = 72 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 88 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 88 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star), MinWidth = 110 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        return grid;
    }
}
