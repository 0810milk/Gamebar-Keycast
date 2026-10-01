using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Shapes;

namespace KeyDisplay
{
    // 1.5：组合模式的手柄模板（纯描边，照用户提供的 Xbox 线稿风格）
    // 机身是一条连续闭合轮廓（顶部圆角 + 两侧握把下垂 + 中间浅凹），
    // 机身内只有四个圆圈：左摇杆 / ABXY / 十字键 / 右摇杆；圈内按品牌画四瓣或圆点。
    // 自定义模式不画任何模板（由 ApplyGamepadTemplate 提前返回）。
    public sealed partial class Widget1
    {
        private void GpOutlineBuild(string tpl)
        {
            try
            {
                var cv = GamepadTemplatePanel;
                if (cv == null) return;
                cv.Children.Clear();
                bool ps = tpl == "ps", sw = tpl == "switch";
                var stroke = BorderB();
                const double thick = 1.4;

                // 机身参数：Xbox 宽体 / PS 窄长 / Switch 偏方
                double xL = ps ? 16 : (sw ? 10 : 4);
                double xR = ps ? 160 : (sw ? 166 : 172);
                double yTop = ps ? 14 : (sw ? 18 : 16);
                double rc = ps ? 24 : (sw ? 30 : 26);
                double yWaist = ps ? 82 : (sw ? 76 : 80);
                double rG = ps ? 28 : (sw ? 26 : 30);
                double dip = ps ? 14 : 16;
                double lgx = xL + rG, rgx = xR - rG;

                var pts = new List<Point>();
                for (int i = 0; i <= 14; i++)                        // 左上圆角 π→1.5π
                {
                    double a = Math.PI * (1 + i / 14.0 * 0.5);
                    pts.Add(new Point(xL + rc + rc * Math.Cos(a), yTop + rc + rc * Math.Sin(a)));
                }
                pts.Add(new Point(xR - rc, yTop));                   // 顶边
                for (int i = 0; i <= 14; i++)                        // 右上圆角 -π/2→0
                {
                    double a = -Math.PI / 2 + i / 14.0 * (Math.PI / 2);
                    pts.Add(new Point(xR - rc + rc * Math.Cos(a), yTop + rc + rc * Math.Sin(a)));
                }
                pts.Add(new Point(xR, yWaist));                      // 右侧下行
                for (int i = 1; i <= 26; i++)                        // 右握把 0→π（底部下垂）
                {
                    double a = i / 26.0 * Math.PI;
                    pts.Add(new Point(rgx + rG * Math.Cos(a), yWaist + rG * Math.Sin(a)));
                }
                double ax = rgx - rG, bx = lgx + rG;
                for (int i = 1; i <= 20; i++)                        // 中央浅下凹
                {
                    double t = i / 21.0;
                    pts.Add(new Point(ax + (bx - ax) * t, yWaist + dip * Math.Sin(Math.PI * t)));
                }
                for (int i = 26; i >= 0; i--)                        // 左握把 π→0
                {
                    double a = i / 26.0 * Math.PI;
                    pts.Add(new Point(lgx - rG * Math.Cos(a), yWaist + rG * Math.Sin(a)));
                }

                var body = new Polygon
                {
                    Stroke = stroke,
                    StrokeThickness = thick,
                    Fill = _transparent,
                    Opacity = 0.9,
                    IsHitTestVisible = false
                };
                foreach (var p in pts) body.Points.Add(p);
                cv.Children.Add(body);

                // 四个圆圈（位置与部件坐标一致，保证按键落在圈里）
                GpOutlineRing(cv, 126, 48, 21, stroke, thick);       // ABXY
                GpOutlineRing(cv, 34, 44, 18, stroke, thick);        // 左摇杆
                GpOutlineRing(cv, 34, 84, 18, stroke, thick);        // 十字键
                GpOutlineRing(cv, 126, 88, 16, stroke, thick);       // 右摇杆

                // 圈内：ABXY 四瓣 / 十字键四瓣；Switch 用四个小圆点
                GpOutlinePetals(cv, 126, 48, 10, sw ? 3.4 : 5.0, stroke, thick);
                GpOutlinePetals(cv, 34, 84, 9, sw ? 3.2 : 4.5, stroke, thick);

                // PlayStation：中央触摸板（细线圆角矩形）
                if (ps)
                {
                    var pad = new Rectangle
                    {
                        Width = 52,
                        Height = 16,
                        RadiusX = 6,
                        RadiusY = 6,
                        Stroke = stroke,
                        StrokeThickness = thick,
                        Fill = _transparent,
                        IsHitTestVisible = false,
                        Opacity = 0.9
                    };
                    Canvas.SetLeft(pad, 62);
                    Canvas.SetTop(pad, 62);
                    cv.Children.Add(pad);
                }
            }
            catch (Exception ex) { DiagLog("gamepad outline build fail: " + ex.Message); }
        }

        private void GpOutlineRing(Canvas cv, double cx, double cy, double r, Windows.UI.Xaml.Media.Brush stroke, double thick)
        {
            try
            {
                var e = new Ellipse
                {
                    Width = r * 2,
                    Height = r * 2,
                    Stroke = stroke,
                    StrokeThickness = thick,
                    Fill = _transparent,
                    IsHitTestVisible = false,
                    Opacity = 0.9
                };
                Canvas.SetLeft(e, cx - r);
                Canvas.SetTop(e, cy - r);
                cv.Children.Add(e);
            }
            catch (Exception ex) { DiagLog("gamepad ring fail: " + ex.Message); }
        }

        private void GpOutlinePetals(Canvas cv, double cx, double cy, double d, double pr, Windows.UI.Xaml.Media.Brush stroke, double thick)
        {
            try
            {
                for (int k = 0; k < 4; k++)
                {
                    double ang = -Math.PI / 2 + k * Math.PI / 2;
                    double px = cx + Math.Cos(ang) * d, py = cy + Math.Sin(ang) * d;
                    var e = new Ellipse
                    {
                        Width = pr * 2,
                        Height = pr * 2,
                        Stroke = stroke,
                        StrokeThickness = thick,
                        Fill = _transparent,
                        IsHitTestVisible = false,
                        Opacity = 0.9
                    };
                    Canvas.SetLeft(e, px - pr);
                    Canvas.SetTop(e, py - pr);
                    cv.Children.Add(e);
                }
            }
            catch (Exception ex) { DiagLog("gamepad petals fail: " + ex.Message); }
        }
    }
}