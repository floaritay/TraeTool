using System.Windows.Controls;

namespace TraeTool.Views;

public partial class UsagePage : System.Windows.Controls.UserControl
{
    public UsagePage()
    {
        InitializeComponent();
    }

    /// <summary>范围下拉：Tag 为天数字符串（ComboBoxItem.Tag 与 int 属性不直接绑定转换）。</summary>
    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as System.Windows.Controls.ComboBox)?.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            item.Tag is string tag && int.TryParse(tag, out var days) &&
            DataContext is TraeTool.ViewModels.UsagePageViewModel vm &&
            vm.RangeDays != days)
        {
            vm.RangeDays = days;
        }
    }
}
