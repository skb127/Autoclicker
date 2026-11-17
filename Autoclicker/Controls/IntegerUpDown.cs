using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Autoclicker.Controls;

/// <summary>
/// A <see cref="NumericUpDown"/> tailored for non-nullable <see cref="int"/> bindings.
/// Defers pushing typed text until committed (Enter, LostFocus, or spinner action) so
/// intermediate digits never trigger range adjustments, suppresses transient null/parse
/// validation errors, and restores the last valid integer when cleared.
/// </summary>
public class IntegerUpDown : NumericUpDown
{
    public static readonly StyledProperty<int> IntValueProperty =
        AvaloniaProperty.Register<IntegerUpDown, int>(
            nameof(IntValue),
            defaultValue: 0,
            defaultBindingMode: BindingMode.TwoWay);

    private bool _isUpdatingValueFromInt;
    private bool _isSpinning;

    public IntegerUpDown()
    {
        FormatString = "0";
        ClipValueToMinMax = true;
        Increment = 1;
        ParsingNumberStyle = NumberStyles.Integer;
    }

    /// <summary>
    /// Integer value bound to the ViewModel. Never emits <c>null</c>.
    /// </summary>
    public int IntValue
    {
        get => GetValue(IntValueProperty);
        set => SetValue(IntValueProperty, value);
    }

    // Inherit the standard Fluent NumericUpDown control template and styling.
    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    protected override void OnInitialized()
    {
        base.OnInitialized();

        // Populate Value and Text on startup even when the bound IntValue equals the default 0.
        if (Value is null)
        {
            SyncValueFromIntValue();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IntValueProperty ||
            ((change.Property == MinimumProperty || change.Property == MaximumProperty) && !IsKeyboardFocusWithin))
        {
            SyncValueFromIntValue();
        }
    }

    protected override void OnValueChanged(decimal? oldValue, decimal? newValue)
    {
        base.OnValueChanged(oldValue, newValue);

        if (_isUpdatingValueFromInt)
        {
            return;
        }

        // Push immediately on spinner/wheel changes or programmatic Value updates,
        // but defer while the user is actively typing inside the text box.
        if (_isSpinning || !IsKeyboardFocusWithin)
        {
            CommitCurrentValueToIntValue();
        }
    }

    protected override void OnSpin(SpinEventArgs e)
    {
        _isSpinning = true;
        try
        {
            base.OnSpin(e);
        }
        finally
        {
            _isSpinning = false;
        }

        CommitCurrentValueToIntValue();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Enter)
        {
            CommitCurrentValueToIntValue();
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);

        // Only commit once keyboard focus has actually left this control.
        if (!IsKeyboardFocusWithin)
        {
            CommitCurrentValueToIntValue();
        }
    }

    protected override void UpdateDataValidation(AvaloniaProperty property, BindingValueType state, Exception? error)
    {
        // Suppress transient parsing/null validation messages while editing (e.g. empty text or '-' prefix).
        if (property == ValueProperty)
        {
            base.UpdateDataValidation(property, BindingValueType.Value, null);
            return;
        }

        base.UpdateDataValidation(property, state, error);
    }

    private void CommitCurrentValueToIntValue()
    {
        if (_isUpdatingValueFromInt)
        {
            return;
        }

        if (Value is { } currentValue)
        {
            decimal clampedDecimal = Math.Clamp(currentValue, Minimum, Maximum);
            int clampedInt = (int)Math.Round(clampedDecimal, MidpointRounding.AwayFromZero);

            if (IntValue != clampedInt)
            {
                SetCurrentValue(IntValueProperty, clampedInt);
            }

            // Always sync Value and Text from the final IntValue (which may have been coerced by the ViewModel).
            SyncValueFromIntValue();
            string expectedText = IntValue.ToString(CultureInfo.CurrentCulture);
            if (Text != expectedText)
            {
                SetCurrentValue(TextProperty, expectedText);
            }
        }
        else
        {
            // Restore the last valid integer when the user leaves the box empty.
            SyncValueFromIntValue();
        }
    }

    private void SyncValueFromIntValue()
    {
        decimal target = Math.Clamp(IntValue, Minimum, Maximum);
        SyncValueFromDecimal(target);
    }

    private void SyncValueFromDecimal(decimal target)
    {
        _isUpdatingValueFromInt = true;
        try
        {
            SetCurrentValue(ValueProperty, target);
        }
        finally
        {
            _isUpdatingValueFromInt = false;
        }
    }
}
