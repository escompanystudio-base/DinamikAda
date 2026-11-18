using DinamikAda.Platform.Win32.Windowing;
using Xunit;

namespace DinamikAda.Tests;

public class DockReorderMathTests
{
    private static readonly double[] Centers = { 20, 60, 100, 140 };

    [Theory]
    [InlineData(1, 10, 0)]    // drag second icon to far left
    [InlineData(1, 75, 1)]    // small wiggle stays in place
    [InlineData(1, 125, 2)]   // past the third icon
    [InlineData(1, 500, 3)]   // far right clamps to last index
    [InlineData(3, 0, 0)]
    public void Drag_target_index_counts_resting_centers_left_of_cursor(int dragged, double x, int expected)
    {
        Assert.Equal(expected, DockLayoutCalculator.ComputeReorderTargetIndex(Centers, x, dragged));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(45, 1)]
    [InlineData(500, 4)]      // external drop may append after the last pin
    public void External_drop_index_allows_insert_after_last(double x, int expected)
    {
        Assert.Equal(expected, DockLayoutCalculator.ComputeReorderTargetIndex(Centers, x, -1));
    }

    [Fact]
    public void Moving_right_shifts_skipped_icons_left_by_one_pitch()
    {
        // dragging index 0 to index 2: icons 1 and 2 slide left, 3 stays.
        Assert.Equal(0.0, DockLayoutCalculator.ComputeReorderShift(0, 0, 2, 40));
        Assert.Equal(-40.0, DockLayoutCalculator.ComputeReorderShift(1, 0, 2, 40));
        Assert.Equal(-40.0, DockLayoutCalculator.ComputeReorderShift(2, 0, 2, 40));
        Assert.Equal(0.0, DockLayoutCalculator.ComputeReorderShift(3, 0, 2, 40));
    }

    [Fact]
    public void Moving_left_shifts_skipped_icons_right_by_one_pitch()
    {
        // dragging index 3 to index 1: icons 1 and 2 slide right, 0 stays.
        Assert.Equal(0.0, DockLayoutCalculator.ComputeReorderShift(0, 3, 1, 40));
        Assert.Equal(40.0, DockLayoutCalculator.ComputeReorderShift(1, 3, 1, 40));
        Assert.Equal(40.0, DockLayoutCalculator.ComputeReorderShift(2, 3, 1, 40));
    }

    [Fact]
    public void External_drop_opens_a_gap_at_the_insertion_point()
    {
        Assert.Equal(-20.0, DockLayoutCalculator.ComputeReorderShift(0, -1, 2, 40));
        Assert.Equal(-20.0, DockLayoutCalculator.ComputeReorderShift(1, -1, 2, 40));
        Assert.Equal(20.0, DockLayoutCalculator.ComputeReorderShift(2, -1, 2, 40));
        Assert.Equal(20.0, DockLayoutCalculator.ComputeReorderShift(3, -1, 2, 40));
    }
}
