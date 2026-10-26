namespace GameOptimizer.Tests;

using FluentAssertions;
using GameOptimizer.Core.GameMode;

/// <summary>
/// 恢复选择状态的纯逻辑：勾选、全选 / 取消全选、只把可恢复的勾选项交给队列。
///
/// 「全选」只覆盖可恢复项是本组用例的重点：把明确不可恢复的目标放进队列，
/// 等于让用户以为它会被恢复，实际什么也没发生。
/// </summary>
public class ProcessRestoreSelectionTests
{
    private static ProcessRestoreSelection Sample()
        => new([RestoreTestData.Eligible("chrome"), RestoreTestData.Skipped("ghost"), RestoreTestData.Eligible("kugou")]);

    [Fact]
    public void 单独勾选应与选择对象保持一致()
    {
        var selection = Sample();

        selection.SetSelected(0, true).Should().BeTrue();

        selection.IsSelected(0).Should().BeTrue();
        selection.Selected.Should().ContainSingle().Which.ProcessName.Should().Be("chrome");
        selection.SelectedCount.Should().Be(1);
    }

    [Fact]
    public void 取消勾选后不应再出现在恢复队列里()
    {
        var selection = Sample();
        selection.SetSelected(0, true);

        selection.SetSelected(0, false).Should().BeFalse();

        selection.IsSelected(0).Should().BeFalse();
        selection.Selected.Should().BeEmpty();
    }

    [Fact]
    public void 不可恢复项即使被勾选也不会生效()
    {
        var selection = Sample();

        // 界面本会把复选框禁用；这里验证 Core 自己也是硬拦一道
        selection.SetSelected(1, true).Should().BeFalse();

        selection.IsSelected(1).Should().BeFalse();
        selection.Selected.Should().BeEmpty();
    }

    [Fact]
    public void 全选与取消全选可以反复切换()
    {
        var selection = Sample();

        for (var round = 0; round < 3; round++)
        {
            selection.ToggleAll().Should().BeTrue();
            selection.SelectedCount.Should().Be(2, "只勾两个可恢复项");
            selection.ToggleLabel.Should().Be("取消全选");

            selection.ToggleAll().Should().BeFalse();
            selection.SelectedCount.Should().Be(0);
            selection.ToggleLabel.Should().Be("全选");
        }
    }

    [Fact]
    public void 全选不应把不可恢复项加入队列()
    {
        var selection = Sample();

        selection.ToggleAll();

        selection.Selected.Should().OnlyContain(c => c.Eligible);
        selection.Selected.Should().HaveCount(2);
        selection.IsSelected(1).Should().BeFalse();
    }

    [Fact]
    public void 空列表不应抛异常且没有可勾选项()
    {
        var selection = new ProcessRestoreSelection([]);

        selection.Count.Should().Be(0);
        selection.EligibleCount.Should().Be(0);
        selection.AnySelected.Should().BeFalse();

        var act = () => selection.ToggleAll();

        act.Should().NotThrow();
        selection.ToggleAll().Should().BeFalse();
        selection.Selected.Should().BeEmpty();
        selection.ToggleLabel.Should().Be("全选");
    }

    [Fact]
    public void 构造时默认不勾选由界面决定默认值()
    {
        // Core 不预勾：默认值是产品决策（弹窗默认全勾可恢复项），
        // 判定层只负责「勾了就是勾了」，不掺产品默认。
        var selection = Sample();

        selection.AnySelected.Should().BeFalse();
        selection.AllEligibleSelected.Should().BeFalse();
    }

    [Fact]
    public void 越界下标不应抛异常()
    {
        var selection = Sample();

        selection.IsSelected(-1).Should().BeFalse();
        selection.IsSelected(99).Should().BeFalse();
        selection.SetSelected(99, true).Should().BeFalse();
    }

    [Fact]
    public void 取消全部勾选应回到空队列()
    {
        var selection = Sample();
        selection.ToggleAll();

        selection.ClearSelection();

        selection.Selected.Should().BeEmpty();
        selection.AnySelected.Should().BeFalse();
    }
}
