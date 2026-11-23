using Autoclicker.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Autoclicker.Views;

public partial class SettingsView : UserControl
{
    private IInputElement? _previouslyFocusedElement;

    public SettingsView()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty)
        {
            bool isVisible = change.GetNewValue<bool>();
            if (isVisible)
            {
                _previouslyFocusedElement = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsVisible)
                    {
                        BottomCloseButton?.Focus();
                    }
                }, DispatcherPriority.Input);
            }
            else
            {
                RestorePreviousFocus();
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || !IsVisible || e.Key != Key.Escape)
        {
            return;
        }

        // If a MessageDialog is active on top of the Settings panel, let the dialog handle Escape first.
        if (this.FindAncestorOfType<MainView>()?.DataContext is MainViewModel { ActiveDialog: not null })
        {
            return;
        }

        if (DataContext is SettingsViewModel vm && vm.CloseCommand.CanExecute(null))
        {
            e.Handled = true;
            vm.CloseCommand.Execute(null);
        }
    }

    private void RestorePreviousFocus()
    {
        var previous = _previouslyFocusedElement;
        _previouslyFocusedElement = null;

        if (previous is Control { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true } control)
        {
            Dispatcher.UIThread.Post(() => control.Focus(), DispatcherPriority.Input);
        }
    }
}
