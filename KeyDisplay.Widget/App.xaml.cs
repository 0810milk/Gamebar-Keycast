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

        private XboxGameBarWidget widget1 = null;

        public App()
        {
            Instance = this;
            this.InitializeComponent();
            this.Suspending += OnSuspending;
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
                // 若 IsLaunchActivation 为 true，表示 Game Bar 正在启动小组件的新实例，
                // 必须新建并持有 XboxGameBarWidget（每次小组件打开都是一个新实例）。
                // 否则是后续激活，保持既有实例即可。
                if (widgetArgs.IsLaunchActivation)
                {
                    var rootFrame = new Frame();
                    rootFrame.NavigationFailed += OnNavigationFailed;
                    Window.Current.Content = rootFrame;

                    widget1 = new XboxGameBarWidget(
                        widgetArgs,
                        Window.Current.CoreWindow,
                        rootFrame);
                    rootFrame.Navigate(typeof(Widget1));

                    Window.Current.Closed += Widget1Window_Closed;

                    Window.Current.Activate();
                }
            }
        }

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
    }
}