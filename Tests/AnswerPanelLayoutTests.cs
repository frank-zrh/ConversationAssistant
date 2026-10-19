using ConversationAssistant_App.UI;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class AnswerPanelLayoutTests
{
    [TestMethod]
    public void SingleArrowCyclesThroughNormalExpandedAndCollapsed()
    {
        var layout = new AnswerPanelLayout();
        foreach (var expected in new[] { AnswerPanelMode.Normal, AnswerPanelMode.Expanded, AnswerPanelMode.Collapsed,
            AnswerPanelMode.Normal, AnswerPanelMode.Expanded, AnswerPanelMode.Collapsed })
        {
            layout.Cycle();
            Assert.AreEqual(expected, layout.Mode);
        }
    }

    [TestMethod]
    public void StartsCollapsedWithAnalysisUsingAllRemainingHeight()
    {
        var layout = new AnswerPanelLayout();
        Assert.AreEqual(AnswerPanelMode.Collapsed, layout.Mode);
        Assert.IsFalse(layout.IsOpen);
        Assert.AreEqual(1d, layout.AnalysisWeight);
        Assert.AreEqual(0d, layout.AnswerWeight);
    }

    [TestMethod]
    public void HeaderOpensAtOneThirdAndCollapsesAgain()
    {
        var layout = new AnswerPanelLayout();
        layout.ToggleVisibility();
        Assert.AreEqual(AnswerPanelMode.Normal, layout.Mode);
        Assert.IsTrue(layout.IsOpen);
        Assert.AreEqual(1d / 3, layout.AnswerWeight / (layout.AnswerWeight + layout.AnalysisWeight));
        layout.ToggleVisibility();
        Assert.AreEqual(AnswerPanelMode.Collapsed, layout.Mode);
    }

    [TestMethod]
    public void ExpansionUsesThreeQuartersAndRestoresOneThird()
    {
        var layout = new AnswerPanelLayout();
        layout.Open();
        layout.ToggleExpansion();
        Assert.AreEqual(AnswerPanelMode.Expanded, layout.Mode);
        Assert.AreEqual(3d / 4, layout.AnswerWeight / (layout.AnswerWeight + layout.AnalysisWeight));
        layout.ToggleExpansion();
        Assert.AreEqual(AnswerPanelMode.Normal, layout.Mode);
        Assert.AreEqual(1d / 3, layout.AnswerWeight / (layout.AnswerWeight + layout.AnalysisWeight));
    }

    [TestMethod]
    public void CollapsingExpandedPanelReopensAtNormalSize()
    {
        var layout = new AnswerPanelLayout();
        layout.Open();
        layout.ToggleExpansion();
        layout.ToggleVisibility();
        layout.ToggleVisibility();
        Assert.AreEqual(AnswerPanelMode.Normal, layout.Mode);
        Assert.AreEqual(1d, layout.AnswerWeight);
        Assert.AreEqual(2d, layout.AnalysisWeight);
    }

    [TestMethod]
    public void ExplicitQuestionRevealDoesNotShrinkAnExpandedPanel()
    {
        var layout = new AnswerPanelLayout();
        layout.Open();
        layout.ToggleExpansion();
        layout.Open();
        Assert.AreEqual(AnswerPanelMode.Expanded, layout.Mode);
    }

    [TestMethod]
    public void RepeatedExplicitQuestionRevealDoesNotCollapseThePanel()
    {
        var layout = new AnswerPanelLayout();
        layout.Open();
        layout.Open();
        Assert.AreEqual(AnswerPanelMode.Normal, layout.Mode);
    }

    [TestMethod]
    public void NewWorkspaceResetReturnsToCollapsed()
    {
        var layout = new AnswerPanelLayout();
        layout.Open();
        layout.ToggleExpansion();
        layout.Reset();
        Assert.AreEqual(AnswerPanelMode.Collapsed, layout.Mode);
        Assert.IsFalse(layout.IsOpen);
    }
}
