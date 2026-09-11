using System.Windows;
using System.Windows.Controls;
using Seedbomb.ViewModels;

namespace Seedbomb.Views.Controls;

/// <summary>
/// Picks the operation editor template from <see cref="RuleEditorViewModel.SelectedOp"/>.
/// A ContentControl only re-runs a selector when Content changes, so
/// <see cref="RefreshOnProperty"/> reapplies the template when the operation changes.
/// </summary>
public sealed class RuleOperationTemplateSelector : DataTemplateSelector
{
    /// <summary>Editor for a constant value.</summary>
    public DataTemplate? ConstantTemplate { get; set; }

    /// <summary>Editor for a numeric or date range.</summary>
    public DataTemplate? RangeTemplate { get; set; }

    /// <summary>Editor for a sequence start/step.</summary>
    public DataTemplate? SequenceTemplate { get; set; }

    /// <summary>Editor for a pattern template string.</summary>
    public DataTemplate? PatternTemplate { get; set; }

    /// <summary>Editor for a one-of value list.</summary>
    public DataTemplate? OneOfTemplate { get; set; }

    /// <summary>Editor for a Bogus catalog endpoint.</summary>
    public DataTemplate? BogusTemplate { get; set; }

    /// <summary>Explanation for lookup-random.</summary>
    public DataTemplate? LookupRandomTemplate { get; set; }

    /// <summary>Empty editor used when no operation-specific UI should render.</summary>
    public DataTemplate? EmptyTemplate { get; set; }

    /// <summary>Identifies the refresh trigger attached to a ContentControl.</summary>
    public static readonly DependencyProperty RefreshOnProperty =
        DependencyProperty.RegisterAttached(
            "RefreshOn",
            typeof(object),
            typeof(RuleOperationTemplateSelector),
            new PropertyMetadata(null, OnRefreshOnChanged));

    /// <summary>Gets the refresh trigger.</summary>
    public static object? GetRefreshOn(DependencyObject obj) => obj.GetValue(RefreshOnProperty);

    /// <summary>Sets the refresh trigger. Changing it reapplies the selector.</summary>
    public static void SetRefreshOn(DependencyObject obj, object? value) => obj.SetValue(RefreshOnProperty, value);

    /// <inheritdoc />
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        var op = item as string ?? (item as RuleEditorViewModel)?.SelectedOp;
        return op switch
        {
            "constant" => ConstantTemplate,
            "range" => RangeTemplate,
            "sequence" => SequenceTemplate,
            "pattern" => PatternTemplate,
            "oneOf" => OneOfTemplate,
            "bogus" => BogusTemplate,
            "lookupRandom" => LookupRandomTemplate,
            _ => EmptyTemplate,
        };
    }

    private static void OnRefreshOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl control)
            return;

        var selector = control.ContentTemplateSelector;
        if (selector is null)
            return;

        // Content is the ViewModel and does not change with SelectedOp; set ContentTemplate
        // so only the active editor is instantiated.
        control.ContentTemplate = selector.SelectTemplate(control.Content, control);
    }
}
