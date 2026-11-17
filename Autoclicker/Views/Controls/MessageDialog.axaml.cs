using System.Windows.Input;
using Autoclicker.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Autoclicker.Views.Controls;

public partial class MessageDialog : UserControl
{
    private static readonly IBrush InfoBorderBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush WarningBorderBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush ErrorBorderBrush = new SolidColorBrush(Color.Parse("#EF4444"));

    public static readonly StyledProperty<DialogMessage?> MessageProperty =
        AvaloniaProperty.Register<MessageDialog, DialogMessage?>(nameof(Message));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<MessageDialog, ICommand?>(nameof(CloseCommand));

    private IInputElement? _previouslyFocusedElement;

    public MessageDialog()
    {
        InitializeComponent();
    }

    public DialogMessage? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MessageProperty)
        {
            ApplyDialogMessage(change.GetNewValue<DialogMessage?>());
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (!e.Handled && Message is not null && e.Key is Key.Enter or Key.Escape)
        {
            e.Handled = true;
            ExecuteCloseCommand();
        }
    }

    private void OnOkButtonClick(object? sender, RoutedEventArgs e)
    {
        ExecuteCloseCommand();
    }

    private void ApplyDialogMessage(DialogMessage? message)
    {
        if (message is null)
        {
            OkButton.IsDefault = false;
            OkButton.IsCancel = false;
            IsVisible = false;
            RestorePreviousFocus();
            return;
        }

        // Capture current focused element before moving focus into the modal
        _previouslyFocusedElement = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

        (SeverityIconText.Text, CardBorder.BorderBrush) = message.Severity switch
        {
            DialogSeverity.Info => ("ℹ️", InfoBorderBrush),
            DialogSeverity.Warning => ("⚠️", WarningBorderBrush),
            DialogSeverity.Error => ("⛔", ErrorBorderBrush),
            _ => ("ℹ️", InfoBorderBrush)
        };

        TitleText.Text = message.Title;
        MessageText.Text = message.Message;
        OkButton.Content = string.IsNullOrWhiteSpace(message.ConfirmText) ? "OK" : message.ConfirmText;

        OkButton.IsDefault = true;
        OkButton.IsCancel = true;
        IsVisible = true;

        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
            {
                OkButton.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void ExecuteCloseCommand()
    {
        if (CloseCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
        else
        {
            SetCurrentValue(MessageProperty, null);
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
