using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class EmbeddedFontResourceTests
{
    private const string UiFamilySource =
        "/Midora.Desktop.Presentation;Component/Assets/Fonts/Sora/#Sora";
    private const string MonospaceFamilySource =
        "/Midora.Desktop.Presentation;Component/Assets/Fonts/JetBrainsMono/#JetBrains Mono";

    [Fact]
    public void SharedThemeResolvesEveryEmbeddedFontWeightWithoutInstalledFonts()
    {
        RunOnSta(() =>
        {
            ResourceDictionary palette = (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri(
                    "/Midora.Desktop.Presentation;component/Themes/Palette.xaml",
                    UriKind.Relative));
            FontFamily ui = Assert.IsType<FontFamily>(palette["Font.UI"]);
            FontFamily monospace = Assert.IsType<FontFamily>(palette["Font.Mono"]);

            Assert.Equal(UiFamilySource, ui.Source);
            Assert.Equal(MonospaceFamilySource, monospace.Source);
            AssertTypeface(ui, FontWeights.Light, "Sora");
            AssertTypeface(ui, FontWeights.Normal, "Sora");
            AssertTypeface(ui, FontWeights.SemiBold, "Sora");
            AssertTypeface(ui, FontWeights.Bold, "Sora");
            AssertTypeface(monospace, FontWeights.Normal, "JetBrains Mono");
            AssertTypeface(monospace, FontWeights.SemiBold, "JetBrains Mono");
            AssertTypeface(monospace, FontWeights.Bold, "JetBrains Mono");
        });
    }

    [Fact]
    public void FontAssetsLicensesAndPresentationResourceLinksStayComplete()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] relativeFontPaths =
        [
            "assets/fonts/Sora/Sora-Light.ttf",
            "assets/fonts/Sora/Sora-Regular.ttf",
            "assets/fonts/Sora/Sora-SemiBold.ttf",
            "assets/fonts/Sora/Sora-Bold.ttf",
            "assets/fonts/JetBrainsMono/JetBrainsMono-Regular.ttf",
            "assets/fonts/JetBrainsMono/JetBrainsMono-SemiBold.ttf",
            "assets/fonts/JetBrainsMono/JetBrainsMono-Bold.ttf"
        ];
        foreach (string relativePath in relativeFontPaths)
        {
            FileInfo font = new(Path.Combine(
                repositoryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(font.Exists, $"Missing embedded font asset: {relativePath}");
            Assert.True(font.Length > 10_000, $"Embedded font asset is unexpectedly small: {relativePath}");
        }

        foreach (string relativeLicensePath in new[]
        {
            "assets/fonts/Sora/OFL.txt",
            "assets/fonts/JetBrainsMono/OFL.txt"
        })
        {
            string license = File.ReadAllText(Path.Combine(
                repositoryRoot,
                relativeLicensePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", license, StringComparison.Ordinal);
        }

        XDocument project = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "midora-desktop",
            "Midora.Desktop.Presentation",
            "Midora.Desktop.Presentation.csproj"));
        string[] linkedResources = project
            .Descendants("Resource")
            .Select(resource => (string?)resource.Attribute("Link"))
            .Where(link => link is not null)
            .Cast<string>()
            .Select(link => link.Replace('\\', '/'))
            .ToArray();
        foreach (string relativePath in relativeFontPaths)
        {
            string expectedLink = "Assets/Fonts/" + relativePath["assets/fonts/".Length..];
            Assert.Contains(expectedLink, linkedResources, StringComparer.OrdinalIgnoreCase);
        }

        XDocument desktopProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "midora-desktop",
            "Midora.Desktop",
            "Midora.Desktop.csproj"));
        string[] distributedNotices = desktopProject
            .Descendants("Content")
            .Where(content => string.Equals(
                (string?)content.Attribute("CopyToPublishDirectory"),
                "Always",
                StringComparison.Ordinal))
            .Select(content => (string?)content.Attribute("Link"))
            .Where(link => link is not null)
            .Cast<string>()
            .Select(link => link.Replace('\\', '/'))
            .ToArray();
        Assert.Contains("THIRD-PARTY-NOTICES.md", distributedNotices, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("licenses/Sora-OFL-1.1.txt", distributedNotices, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("licenses/JetBrainsMono-OFL-1.1.txt", distributedNotices, StringComparer.OrdinalIgnoreCase);

        string thirdPartyNotices = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "THIRD-PARTY-NOTICES.md"));
        Assert.Contains("Fluent System Icons", thirdPartyNotices, StringComparison.Ordinal);
        Assert.Contains("Microsoft.CodeAnalysis (Roslyn)", thirdPartyNotices, StringComparison.Ordinal);
        Assert.Contains("Google.Protobuf", thirdPartyNotices, StringComparison.Ordinal);
        Assert.Contains(".NET 10 self-contained runtime", thirdPartyNotices, StringComparison.Ordinal);

        string[] forbiddenInstalledFontNames = ["Segoe UI", "Cascadia Mono", "Consolas"];
        string[] productionDirectories =
        [
            "src/midora-desktop/Midora.Desktop",
            "src/midora-desktop/Midora.Desktop.Presentation",
            "src/midora-desktop/Midora.Desktop.StyleGallery"
        ];
        foreach (string directory in productionDirectories)
        {
            string absoluteDirectory = Path.Combine(
                repositoryRoot,
                directory.Replace('/', Path.DirectorySeparatorChar));
            foreach (string sourceFile in Directory.EnumerateFiles(
                absoluteDirectory,
                "*.*",
                SearchOption.AllDirectories).Where(path =>
                    (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                     || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            {
                string source = File.ReadAllText(sourceFile);
                foreach (string forbiddenName in forbiddenInstalledFontNames)
                {
                    Assert.DoesNotContain(forbiddenName, source, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void EveryApplicationWindowExplicitlyUsesTheEmbeddedUiFont()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] windowDirectories =
        [
            "src/midora-desktop/Midora.Desktop",
            "src/midora-desktop/Midora.Desktop.StyleGallery"
        ];
        int windowCount = 0;

        foreach (string directory in windowDirectories)
        {
            string absoluteDirectory = Path.Combine(
                repositoryRoot,
                directory.Replace('/', Path.DirectorySeparatorChar));
            foreach (string xamlPath in Directory.EnumerateFiles(
                         absoluteDirectory,
                         "*.xaml",
                         SearchOption.AllDirectories).Where(path =>
                         !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                         && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            {
                XDocument document = XDocument.Load(xamlPath);
                XElement? root = document.Root;
                if (root?.Name.LocalName != "Window") continue;

                windowCount++;
                Assert.Contains((string?)root.Attribute("FontFamily"),
                    new[] { "{StaticResource Font.UI}", "{DynamicResource Font.UI}" });
            }
        }

        Assert.True(windowCount >= 20, $"Expected to audit every application Window, found only {windowCount}.");
    }

    [Fact]
    public void SharedTextBearingStylesExplicitlyUseTheEmbeddedUiFont()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument controls = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "midora-desktop",
            "Midora.Desktop.StyleGallery",
            "Themes",
            "Controls.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        string[] requiredTargetTypes =
        [
            "Window",
            "TextBlock",
            "Button",
            "ToggleButton",
            "TextBox",
            "PasswordBox",
            "ComboBox",
            "ComboBoxItem",
            "CheckBox",
            "RadioButton",
            "ListBox",
            "ListBoxItem",
            "ItemsControl",
            "TreeView",
            "TreeViewItem",
            "GroupBox",
            "TabControl",
            "TabItem",
            "Expander",
            "ToolTip",
            "ContextMenu",
            "Menu",
            "MenuItem"
        ];

        foreach (string targetType in requiredTargetTypes)
        {
            XElement[] candidates = controls
                .Descendants(presentation + "Style")
                .Where(style => NormalizeTargetType((string?)style.Attribute("TargetType")) == targetType)
                .ToArray();
            Assert.NotEmpty(candidates);
            Assert.Contains(candidates, StyleUsesUiFontDirectlyOrThroughBase);
        }
    }

    private static void AssertTypeface(FontFamily family, FontWeight weight, string expectedFamilyName)
    {
        Typeface typeface = new(family, FontStyles.Normal, weight, FontStretches.Normal);
        Assert.True(typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphTypeface));
        Assert.NotNull(glyphTypeface);
        Assert.Contains(
            glyphTypeface.FamilyNames.Values,
            name => string.Equals(name, expectedFamilyName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(weight, glyphTypeface.Weight);
        Assert.True(glyphTypeface.CharacterToGlyphMap.ContainsKey('A'));
    }

    private static string NormalizeTargetType(string? targetType) =>
        targetType?.Replace("{x:Type ", string.Empty, StringComparison.Ordinal)
            .TrimEnd('}') ?? string.Empty;

    private static bool StyleUsesUiFontDirectlyOrThroughBase(XElement style)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        if (style.Elements(presentation + "Setter").Any(setter =>
                string.Equals((string?)setter.Attribute("Property"), "FontFamily", StringComparison.Ordinal)
                && string.Equals((string?)setter.Attribute("Value"), "{StaticResource Font.UI}", StringComparison.Ordinal)))
        {
            return true;
        }

        string? basedOn = (string?)style.Attribute("BasedOn");
        return basedOn is not null
               && (basedOn.Contains("Button.Base", StringComparison.Ordinal)
                   || basedOn.Contains("Toggle.Segment", StringComparison.Ordinal)
                   || basedOn.Contains("{x:Type TextBlock}", StringComparison.Ordinal));
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The embedded font resource test did not complete.");
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "LICENSE"))
                && Directory.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "midora-desktop",
                    "Midora.Desktop")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Midora repository root.");
    }
}
