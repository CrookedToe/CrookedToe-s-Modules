using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VRCOSC.App.Utils;

namespace CrookedToe.Modules.OSCSerialBridge.UI;

public class SerialDeviceModuleSettingView : UserControl
{
    private readonly SerialDeviceModuleSetting moduleSetting;
    private readonly StackPanel rowsPanel;

    public SerialDeviceModuleSettingView(OSCSerialBridgeModule _, SerialDeviceModuleSetting moduleSetting)
    {
        this.moduleSetting = moduleSetting;
        DataContext = moduleSetting;

        rowsPanel = new StackPanel();
        Content = BuildLayout();
        RefreshRows();
        moduleSetting.Attribute.OnCollectionChanged((_, _) => RefreshRows());
    }

    private FrameworkElement BuildLayout() =>
        SerialSettingsUi.CreateSettingLayout(
            CreateHeaderRow(),
            rowsPanel,
            "Add Device",
            (_, _) =>
            {
                moduleSetting.Add();
                RefreshRows();
            });

    private static string[] GetAvailablePorts(SerialDeviceModuleSetting moduleSetting)
    {
        var ports = SerialPort.GetPortNames()
            .Concat(moduleSetting.Attribute
                .Select(device => device.PortName.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ports.Length == 0 ? ["COM1"] : ports;
    }

    private void RefreshRows()
    {
        rowsPanel.Children.Clear();
        var availablePorts = GetAvailablePorts(moduleSetting);

        foreach (var device in moduleSetting.Attribute)
            rowsPanel.Children.Add(CreateDeviceRow(device, availablePorts, rowsPanel.Children.Count));
    }

    private static Grid CreateHeaderRow()
    {
        var grid = CreateGridLayout();

        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Name", 0));
        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Port", 2));
        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Baud", 4));
        grid.Children.Add(SerialSettingsUi.CreateHeaderText("Enabled", 6));

        return grid;
    }

    private FrameworkElement CreateDeviceRow(SerialDeviceConfig device, string[] availablePorts, int index)
    {
        var grid = CreateGridLayout();

        var nameBox = SerialSettingsUi.CreateTextBox(device, "Name.Value");
        Grid.SetColumn(nameBox, 0);

        var portBox = SerialSettingsUi.CreateComboBox(device, "PortName.Value");
        portBox.ItemsSource = availablePorts;
        Grid.SetColumn(portBox, 2);

        var baudBox = SerialSettingsUi.CreateTextBox(device, "BaudRate.Value");
        Grid.SetColumn(baudBox, 4);

        var enabledBox = SerialSettingsUi.CreateCheckBox(device, "Enabled.Value");
        Grid.SetColumn(enabledBox, 6);

        var removeButton = SerialSettingsUi.CreateRemoveButton((sender, args) =>
        {
            moduleSetting.Remove(device);
            RefreshRows();
        });
        Grid.SetColumn(removeButton, 8);

        grid.Children.Add(nameBox);
        grid.Children.Add(portBox);
        grid.Children.Add(baudBox);
        grid.Children.Add(enabledBox);
        grid.Children.Add(removeButton);
        return SerialSettingsUi.CreateRowContainer(grid, index);
    }

    private static Grid CreateGridLayout()
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 96 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        return grid;
    }

}
