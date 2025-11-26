using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MeetingAI.Host
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        public static Window? MainWindow { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            // 全局未处理异常捕获
            this.UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // 记录异常
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeetingAI",
                "crash.log");

            try
            {
                var logDir = System.IO.Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CRASH\n" +
                                $"Message: {e.Message}\n" +
                                $"Exception: {e.Exception}\n" +
                                $"StackTrace: {e.Exception?.StackTrace}\n\n";

                System.IO.File.AppendAllText(logPath, logMessage);
            }
            catch
            {
                // 忽略日志写入错误
            }

            // 标记为已处理，防止程序崩溃
            e.Handled = true;

            // 显示错误对话框
            var message = $"Application Error:\n{e.Message}\n\nException Type: {e.Exception?.GetType().Name}\n\nLog: {logPath}";
            System.Diagnostics.Debug.WriteLine(message);

            // 尝试显示消息框（如果可能）
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Application Error",
                    Content = message,
                    CloseButtonText = "OK",
                    XamlRoot = MainWindow?.Content?.XamlRoot
                };

                if (dialog.XamlRoot != null)
                {
                    _ = dialog.ShowAsync();
                }
            }
            catch
            {
                // 如果无法显示对话框，至少记录了日志
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();
        }
    }
}
