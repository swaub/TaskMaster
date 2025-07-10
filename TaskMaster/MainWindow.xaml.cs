using System.Windows;
using System.Windows.Input;

namespace TaskMaster
{
    public partial class MainWindow : Window
    {
        private MainViewModel? _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (_viewModel == null) return;

            if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                TaskNameTextBox.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.R && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_viewModel.ResetRoutinesCommand.CanExecute(null))
                {
                    _viewModel.ResetRoutinesCommand.Execute(null);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
            }
            else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
            }
            else if (e.Key == Key.F1)
            {
                ShowHelpDialog();
                e.Handled = true;
            }
        }

        private void ShowHelpDialog()
        {
            var helpText = @"TaskMaster - Keyboard Shortcuts

Navigation:
• Tab - Navigate between controls
• Shift+Tab - Navigate backwards
• Enter - Activate focused control
• Escape - Cancel current operation

Actions:
• Ctrl+N - Focus new task name field
• Ctrl+R - Reset daily routines
• Ctrl+Z - Undo last action
• Ctrl+Y - Redo last undone action
• F1 - Show this help dialog

Accessibility:
• Alt+S - Sort options
• Alt+C - Category filter
• Alt+N - Task name field
• Alt+D - Description field
• Alt+P - Priority slider
• Alt+U - Due date picker
• Alt+E - Category dropdown
• Alt+A - Add task button

Task Management:
• Use checkboxes to mark tasks/routines complete
• Use Delete buttons to remove items
• Use dropdown menus for sorting and filtering";

            MessageBox.Show(helpText, "TaskMaster Help", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel?.Dispose();
            base.OnClosed(e);
        }
    }
}