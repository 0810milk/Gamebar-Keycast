using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace KeyDisplay
{
    /// <summary>
    /// 提供特定于应用程序的行为，以补充默认的 Application 类。
    /// </summary>
    sealed partial class App : Application
    {
        public static App Instance { get; private set; }

        private XboxGameBarWidget widget1 = null;          // 主小组件
        private XboxGameBarWidget settingsWidget = null;   // 0.9.5：设置子窗口（官方 settings widget）

        public App()
        {
            Instance = this;
            this.InitializeComponent();
            this.Suspending += OnSuspending;
            // 兜底诊断：任何未处理异常记录到 diag.txt（不阻止进程崩溃，仅留证据）
            this.UnhandledException += (s, e) =>
            {
                try
                {
                    DiagLog("unhandled: " + e.Exception.ToString());
                }
                catch
                {
                }
            };
        }

        public void CloseWidget()
        {
            var w = widget1;
            if (w != null)
            {
                w.Close();
                widget1 = null;
            }
        }

        /// <summary>0.9.5：关闭设置子窗口（设置页「完成」按钮调用）</summary>
        public void CloseSettings()
        {
            var w = settingsWidget;
            if (w != null)
            {
                try { w.Close(); } catch { }
                settingsWidget = null;
            }
        }

        /// <summary>当前 Game Bar 小组件实例（可能为 null，如独立启动）。</summary>
        public XboxGameBarWidget Widget
        {
            get { return widget1; }
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            XboxGameBarWidgetActivatedEventArgs widgetArgs = null;
            if (args.Kind == ActivationKind.Protocol)
            {
                var protocolArgs = args as IProtocolActivatedEventArgs;
                string scheme = protocolArgs.Uri.Scheme;
                if (scheme.Equals("ms-gamebarwidget"))
                {
                    widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
                }
            }
            if (widgetArgs != null)
            {
                string extId = null;
                try { extId = widgetArgs.AppExtensionId; } catch { }
                DiagLog("activate launch=" + widgetArgs.IsLaunchActivation + " ext=" + extId);
                if (widgetArgs.IsLaunchActivation)
                {
                    var rootFrame = new Frame();
                    rootFrame.NavigationFailed += OnNavigationFailed;
                    Window.Current.Content = rootFrame;

                    // 0.9.5：按 AppExtensionId 区分主小组件与设置子窗口（官方 settings widget 机制）
                    if (extId == "KeyDisplaySettings")
                    {
                        settingsWidget = new XboxGameBarWidget(
                            widgetArgs,
                            Window.Current.CoreWindow,
                            rootFrame);
                        rootFrame.Navigate(typeof(SettingsPage), settingsWidget);
                        Window.Current.Closed += SettingsWindow_Closed;
                    }
                    else
                    {
                        widget1 = new XboxGameBarWidget(
                            widgetArgs,
                            Window.Current.CoreWindow,
                            rootFrame);
                        rootFrame.Navigate(typeof(Widget1), widget1);
                        Window.Current.Closed += Widget1Window_Closed;
                    }

                    Window.Current.Activate();
                }
            }
        }

        private void SettingsWindow_Closed(object sender, CoreWindowEventArgs e)
        {
            settingsWidget = null;
            Window.Current.Closed -= SettingsWindow_Closed;
        }

        // 主小组件窗口关闭：释放引用（原实现，勿删——OnActivated 里挂接）
        private void Widget1Window_Closed(object sender, CoreWindowEventArgs e)
        {
            widget1 = null;
            Window.Current.Closed -= Widget1Window_Closed;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                Window.Current.Activate();
            }
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();

            widget1 = null;

            deferral.Complete();
        }

        private static void DiagLog(string msg)
        {
            try
            {
                var dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "diag.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
            }
            catch
            {
            }
        }
    }
}