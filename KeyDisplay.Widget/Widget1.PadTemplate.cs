using System;

namespace KeyDisplay
{
    // 1.4.0：本版不包含手柄底板（模板暂时停用，等形态定稿后再放出）。
    // 保留 GpOutlineBuild 接口，Widget1.xaml.cs 的 ApplyGamepadTemplate() 继续调用它，
    // 因此调用链与"自定义模式不画模板"的逻辑都不受影响，只是不绘制任何内容。
    public sealed partial class Widget1
    {
        private void GpOutlineBuild(string tpl)
        {
            try
            {
                // 本版刻意留空：不绘制机身、不绘制任何圈线。
                var cv = GamepadTemplatePanel;
                if (cv != null) cv.Children.Clear();
            }
            catch (Exception ex) { DiagLog("gamepad template skip fail: " + ex.Message); }
        }
    }
}