using System.Windows;
using System.Windows.Controls;
using PocketNAS.Shared;

namespace PocketNAS.PC.Views;

public partial class ModulePlaceholder : UserControl
{
    private readonly string _module;

    public ModulePlaceholder()
    {
        InitializeComponent();
        _module = "系统";
    }

    public ModulePlaceholder(string module, string heading, string description) : this()
    {
        _module = module;
        TxtHeading.Text = heading;
        TxtDesc.Text = description;
    }

    private void BtnPing_Click(object sender, RoutedEventArgs e)
        => LogService.Success(_module, "日志链路正常，模块视图已挂载");
}
