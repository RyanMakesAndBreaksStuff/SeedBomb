using System.Windows;
using DataGen.Core.Rules;

namespace Seedbomb.Views.Behaviors;

/// <summary>
/// Maps editor controls to <see cref="RuleInputTarget"/> and focuses the first visible,
/// enabled match. Validation stays in the ViewModel.
/// </summary>
public static class ValidationFocusBehavior
{
    /// <summary>Declares which <see cref="RuleInputTarget"/> a control represents.</summary>
    public static readonly DependencyProperty InputTargetProperty =
        DependencyProperty.RegisterAttached(
            "InputTarget",
            typeof(RuleInputTarget),
            typeof(ValidationFocusBehavior),
            new PropertyMetadata(default(RuleInputTarget)));

    /// <summary>When set on a root, focuses the first visible control for that target.</summary>
    public static readonly DependencyProperty RequestProperty =
        DependencyProperty.RegisterAttached(
            "Request",
            typeof(RuleInputTarget?),
            typeof(ValidationFocusBehavior),
            new PropertyMetadata(null, OnRequestChanged));

    /// <summary>Gets the input target declared on <paramref name="element"/>.</summary>
    public static RuleInputTarget GetInputTarget(DependencyObject element) =>
        (RuleInputTarget)element.GetValue(InputTargetProperty);

    /// <summary>Sets the input target declared on <paramref name="element"/>.</summary>
    public static void SetInputTarget(DependencyObject element, RuleInputTarget value) =>
        element.SetValue(InputTargetProperty, value);

    /// <summary>Gets the pending focus request on <paramref name="element"/>.</summary>
    public static RuleInputTarget? GetRequest(DependencyObject element) =>
        (RuleInputTarget?)element.GetValue(RequestProperty);

    /// <summary>Requests focus for <paramref name="value"/> under <paramref name="element"/>.</summary>
    public static void SetRequest(DependencyObject element, RuleInputTarget? value) =>
        element.SetValue(RequestProperty, value);

    /// <summary>
    /// Focuses the first visible, enabled control under <paramref name="root"/> whose
    /// <see cref="InputTargetProperty"/> matches <paramref name="target"/>.
    /// </summary>
    public static bool FocusFirst(DependencyObject root, RuleInputTarget target)
    {
        ArgumentNullException.ThrowIfNull(root);

        UIElement? match = null;
        Walk(root, child =>
        {
            if (child.ReadLocalValue(InputTargetProperty) == DependencyProperty.UnsetValue)
                return true;
            if (GetInputTarget(child) != target)
                return true;
            if (child is not UIElement element || !element.IsVisible || !element.IsEnabled)
                return true;
            match = element;
            return false;
        });

        return match?.Focus() == true;
    }

    /// <summary>
    /// Focuses the first visible, enabled control that matches the first error in
    /// <paramref name="messages"/>. Collapsed and disabled controls are skipped.
    /// </summary>
    public static bool FocusFirstError(DependencyObject root, IReadOnlyList<RuleMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(messages);

        foreach (var message in messages)
        {
            if (message.Severity != RuleMessageSeverity.Error)
                continue;
            if (FocusFirst(root, message.Target))
                return true;
        }

        return false;
    }

    private static void OnRequestChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is RuleInputTarget target)
            FocusFirst(d, target);
    }

    private static void Walk(DependencyObject root, Func<DependencyObject, bool> visit)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visit(current))
                return;

            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                queue.Enqueue(child);
        }
    }
}
