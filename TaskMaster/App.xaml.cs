using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace TaskMaster
{
    public partial class App : Application
    {
        private static AdvancedLoggingService? _logger;

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                _logger = new AdvancedLoggingService();
                _logger.LogInfo("TaskMaster application starting");

                SetupGlobalExceptionHandling();

                if (SingleInstanceManager.IsAnotherInstanceRunning())
                {
                    _logger.LogWarning("Another instance of TaskMaster is already running");
                    Shutdown();
                    return;
                }

                _logger.LogInfo("Single instance verified, proceeding with startup");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to initialize logging system: {ex.Message}",
                    "Critical Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                
                Shutdown();
                return;
            }

            try
            {
                _logger.LogInfo("Starting data validation");
                
                var validationService = new StartupValidationService();
                
                validationService.ValidateEnvironment();
                
                if (!validationService.IsValid)
                {
                    var errorMessage = string.Join(Environment.NewLine, validationService.Errors);
                    _logger.LogCritical($"Startup validation failed: {errorMessage}");
                    
                    MessageBox.Show(
                        $"TaskMaster encountered critical errors during startup validation:{Environment.NewLine}{Environment.NewLine}{errorMessage}{Environment.NewLine}{Environment.NewLine}The application will now close.",
                        "Critical Startup Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    
                    Shutdown();
                    return;
                }
                
                _logger.LogInfo("Data validation completed successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogCritical($"Unexpected startup error: {ex.Message}", ex);
                
                MessageBox.Show(
                    $"TaskMaster encountered an unexpected error during startup:{Environment.NewLine}{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}The application will now close.",
                    "Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                
                Shutdown();
                return;
            }

            _logger.LogInfo("Application startup completed successfully");

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _logger?.LogInfo("Application shutting down");
                SingleInstanceManager.ReleaseMutex();
                _logger?.LogInfo("Application shutdown completed");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error during application shutdown: {ex.Message}", ex);
            }
            finally
            {
                _logger?.Dispose();
                base.OnExit(e);
            }
        }

        private void SetupGlobalExceptionHandling()
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                try
                {
                    var exception = e.ExceptionObject as Exception;
                    _logger?.LogCritical($"Unhandled domain exception: {exception?.Message}", exception);
                    
                    if (e.IsTerminating)
                    {
                        _logger?.LogCritical("Application is terminating due to unhandled exception");
                    }
                }
                catch
                {
                }
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                try
                {
                    _logger?.LogError($"Unobserved task exception: {e.Exception.Message}", e.Exception);
                    e.SetObserved();
                }
                catch
                {
                }
            };

            DispatcherUnhandledException += (sender, e) =>
            {
                try
                {
                    _logger?.LogError($"Unhandled dispatcher exception: {e.Exception.Message}", e.Exception);
                    
                    var result = MessageBox.Show(
                        $"An unexpected error occurred:{Environment.NewLine}{Environment.NewLine}{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}Would you like to continue running the application?",
                        "Unexpected Error",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Error);
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        e.Handled = true;
                        _logger?.LogInfo("User chose to continue after unhandled exception");
                    }
                    else
                    {
                        _logger?.LogInfo("User chose to exit after unhandled exception");
                        Shutdown();
                    }
                }
                catch (Exception logEx)
                {
                    try
                    {
                        MessageBox.Show(
                            $"Critical error in exception handler: {logEx.Message}",
                            "Critical Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    catch
                    {
                    }
                    
                    Shutdown();
                }
            };

            _logger?.LogInfo("Global exception handling configured");
        }
    }
}
