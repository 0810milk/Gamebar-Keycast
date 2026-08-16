using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace KeyDisplay
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
        }

        // 调试入口：独立启动时打开小组件 UI（无 Game Bar 宿主，_widget 为 null）
        private void OpenWidget_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Widget1), null);
        }
    }
}