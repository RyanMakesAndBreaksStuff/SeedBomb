using SeedBomb.Converters;
using SeedBomb.Wpf.Tests.Views;
using System.Windows.Data;
using Wpf.Ui.Controls;
using Xunit;

namespace SeedBomb.Wpf.Tests;

[Collection("StaUi")]
public sealed class ParallelismBoxBindingStaTests
{
    private sealed class Holder { public int Parallelism { get; set; } }

    [StaFact]
    public void ClearingTheBox_ResetsTheSourceToZero()
    {
        var holder = new Holder();
        var box = new NumberBox { Minimum = 0, Maximum = 16, PlaceholderText = "auto" };
        BindingOperations.SetBinding(box, NumberBox.ValueProperty, new Binding(nameof(Holder.Parallelism))
        {
            Source = holder,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            Converter = new NullToZeroConverter(),
        });

        box.Value = 8;
        Assert.Equal(8, holder.Parallelism);

        box.Value = null;
        Assert.Equal(0, holder.Parallelism);
    }
}
