using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class TeamsInterfaceTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static XDocument Load(string name) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, name));
    private static XElement Named(XDocument document, string name) => document.Descendants()
        .Single(element => (string?)element.Attribute(X + "Name") == name);

    [TestMethod]
    public void AdaptivePalettesExposeMatchingKeysAndSystemHighContrastColors()
    {
        var theme = Load("TeamsTheme.xaml");
        var dictionaries = theme.Descendants(Ui + "ResourceDictionary.ThemeDictionaries").Single().Elements().ToArray();
        CollectionAssert.AreEquivalent(new[] { "Default", "Dark", "HighContrast" },
            dictionaries.Select(element => (string)element.Attribute(X + "Key")!).ToArray());
        var keys = dictionaries[0].Elements().Select(element => (string)element.Attribute(X + "Key")!).ToArray();
        foreach (var dictionary in dictionaries)
            CollectionAssert.AreEquivalent(keys, dictionary.Elements().Select(element => (string)element.Attribute(X + "Key")!).ToArray());
        foreach (var brush in dictionaries.Single(element => (string?)element.Attribute(X + "Key") == "HighContrast").Elements())
            StringAssert.StartsWith((string)brush.Attribute("Color")!, "{ThemeResource SystemColor");
        var application = Load("AppResources.xaml");
        Assert.IsTrue(application.Descendants().Any(element =>
            (string?)element.Attribute("Source") == "ms-appx:///Themes/TeamsTheme.xaml"));
        Assert.IsFalse(application.Descendants().Any(element => element.Attribute("RequestedTheme") is not null));
        var project = Load("AppProject.xml");
        Assert.IsFalse(project.Descendants("Page").Any(element => (string?)element.Attribute("Remove") == @"Themes\TeamsTheme.xaml"));
        var themePage = project.Descendants("Page").Single(element => (string?)element.Attribute("Update") == @"Themes\TeamsTheme.xaml");
        Assert.AreEqual("MSBuild:Compile", themePage.Element("Generator")?.Value);
    }

    [TestMethod]
    [DataRow("Default")]
    [DataRow("Dark")]
    public void TextAndPrimaryButtonsMeetNormalTextContrast(string name)
    {
        var palette = Load("TeamsTheme.xaml").Descendants(Ui + "ResourceDictionary")
            .Single(element => (string?)element.Attribute(X + "Key") == name)
            .Elements().ToDictionary(element => (string)element.Attribute(X + "Key")!, element => (string)element.Attribute("Color")!);
        foreach (var background in new[] { "TeamsCanvasBrush", "TeamsSurfaceBrush", "TeamsSubtleBrush",
            "TeamsPendingBrush", "TeamsCompletedBrush", "TeamsFailedBrush" })
            foreach (var foreground in new[] { "TeamsTextBrush", "TeamsSecondaryTextBrush" })
                Assert.IsTrue(Contrast(palette[foreground], palette[background]) >= 4.5, $"{name}: {foreground} on {background}");
        Assert.IsTrue(Contrast(palette["TeamsOnAccentBrush"], palette["TeamsAccentBrush"]) >= 4.5);
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
            Assert.IsTrue(Contrast(palette["AccentButtonForeground" + state], palette["AccentButtonBackground" + state]) >= 4.5);
        Assert.IsTrue(Contrast(palette["TeamsDangerBrush"], palette["TeamsSurfaceBrush"]) >= 4.5);
        Assert.IsTrue(Contrast(palette["TeamsSuccessBrush"], palette["TeamsSurfaceBrush"]) >= 4.5);
    }

    [TestMethod]
    public void CompactControlsRetainNativeTemplatesAndKeyboardFocus()
    {
        var theme = Load("TeamsTheme.xaml");
        var style = theme.Descendants(Ui + "Style").Single(element => (string?)element.Attribute(X + "Key") == "TeamsButtonStyle");
        Assert.AreEqual("32", (string?)style.Elements().Single(element => (string?)element.Attribute("Property") == "MinHeight").Attribute("Value"));
        Assert.AreEqual("True", (string?)style.Elements().Single(element => (string?)element.Attribute("Property") == "UseSystemFocusVisuals").Attribute("Value"));
        Assert.IsFalse(theme.Descendants(Ui + "Setter").Any(element => (string?)element.Attribute("Property") == "Template"));
        var page = Load("MainPage.xaml");
        Assert.IsFalse(page.Descendants(Ui + "ControlTemplate").Any());
        var transcript = page.Descendants(Ui + "DataTemplate").Single(element => (string?)element.Attribute(X + "Key") == "TranscriptTemplate");
        foreach (var text in transcript.Descendants(Ui + "TextBlock"))
            StringAssert.StartsWith((string)text.Attribute("Foreground")!, "{ThemeResource Teams");
        Assert.AreEqual("{StaticResource TeamsListItemStyle}", (string?)Named(page, "ConversationHistoryList").Attribute("ItemContainerStyle"));
    }

    [TestMethod]
    public void TranscriptUtilitiesUseOneLocalizedOverflowWithExistingGuards()
    {
        var page = Load("MainPage.xaml");
        var button = Named(page, "TranscriptActionsButton");
        Assert.AreEqual("DropDownButton", button.Name.LocalName);
        Assert.AreEqual("{Binding Texts[ConversationActions]}", (string?)button.Attribute("AutomationProperties.Name"));
        var menu = button.Descendants(Ui + "MenuFlyout").Single();
        Assert.AreEqual("ActionMenu_Opening", (string?)menu.Attribute("Opening"));
        CollectionAssert.AreEquivalent(new[] { "NewChat_Click", "CopyTranscript_Click", "ClearTranscript_Click", "CreateGroup_Click",
            "SaveRecord_Click", "ViewSession_Click", "OpenRecordsFolder_Click" },
            menu.Elements(Ui + "MenuFlyoutItem").Select(element => (string)element.Attribute("Click")!).ToArray());
        Assert.AreEqual("{Binding CanCreateGroup}", (string?)Named(page, "CreateGroupMenuItem").Attribute("IsEnabled"));
        Assert.AreEqual("{Binding CanNewChat}", (string?)Named(page, "NewChatMenuItem").Attribute("IsEnabled"));
        Assert.AreEqual("{Binding CanSaveConversation}", (string?)Named(page, "SaveRecordMenuItem").Attribute("IsEnabled"));
        Assert.AreEqual("{Binding CanViewSession}", (string?)Named(page, "ViewSessionMenuItem").Attribute("IsEnabled"));
        Assert.HasCount(1, Named(page, "TranscriptToolbar").Descendants(Ui + "Button"));
    }

    [TestMethod]
    public void AnswerUtilitiesUseOverflowAndPrimaryActionsStayVisible()
    {
        var page = Load("MainPage.xaml");
        var button = Named(page, "AnswerActionsButton");
        Assert.AreEqual("{Binding Texts[AnswerActions]}", (string?)button.Attribute("AutomationProperties.Name"));
        CollectionAssert.AreEquivalent(new[] { "CopyAnswer_Click", "Retry_Click", "Dismiss_Click", "AnswerView_Click" },
            button.Descendants(Ui + "MenuFlyoutItem").Select(element => (string)element.Attribute("Click")!).ToArray());
        foreach (var name in new[] { "StartConversationButton", "EndConversationButton", "PauseListeningButton", "ResumeListeningButton", "SendQuestionButton" })
        {
            var action = Named(page, name);
            Assert.AreEqual("Button", action.Name.LocalName);
            Assert.IsFalse(action.Ancestors().Any(element => element.Name.LocalName == "MenuFlyout"));
            if (action.Element(Ui + "FontIcon") is not null)
            {
                Assert.IsNotNull(action.Attribute("AutomationProperties.Name"));
                Assert.IsNotNull(action.Attribute("ToolTipService.ToolTip"));
            }
        }
        Assert.AreEqual("AskBox_KeyDown", (string?)Named(page, "AskBox").Attribute("KeyDown"));
    }

    [TestMethod]
    public void AllPageTextResourceBindingsResolveInBothLanguages()
    {
        var keys = Regex.Matches(Load("MainPage.xaml").ToString(), @"Texts\[(?<key>[^\]]+)\]")
            .Select(match => match.Groups["key"].Value).Distinct();
        foreach (var key in keys)
            foreach (var language in new[] { ConversationLanguage.English, ConversationLanguage.Chinese })
                Assert.IsFalse(string.IsNullOrWhiteSpace(UiText.For(language)[key]), key);
    }

    [TestMethod]
    public void RichAnswersShareAdaptiveColorsAndRetainTheNoScriptPolicy()
    {
        var html = AnswerHtmlPage.Build(new RenderedAnswer("<p>Readable answer</p>", new Dictionary<string, string>()));
        foreach (var value in new[] { "--surface:#FFFFFF", "--text:#242424", "--accent:#5B5FC7", "--surface:#292929",
            "--text:#F5F5F5", "--accent:#A6A7DC", "prefers-color-scheme:dark", "forced-colors:active", "Segoe UI Variable",
            "script-src 'none'", "connect-src 'none'", "<p>Readable answer</p>" })
            StringAssert.Contains(html, value);
    }

    private static double Contrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            var channels = Enumerable.Range(0, 3).Select(index =>
            {
                var channel = int.Parse(hex.AsSpan(1 + index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
                return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }).ToArray();
            return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
        }
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
