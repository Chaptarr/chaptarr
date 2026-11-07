using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Localization
{
    [TestFixture]
    [Category("LocalizationBrowser")]
    public sealed class LocalizationBrowserFixture
    {
        private Uri _baseUri;

        [SetUp]
        public void RequireLocalBrowserTarget()
        {
            var configuredUrl = Environment.GetEnvironmentVariable("CHAPTARR_LOCALIZATION_BROWSER_URL");

            if (string.IsNullOrWhiteSpace(configuredUrl))
            {
                Assert.Ignore("Set CHAPTARR_LOCALIZATION_BROWSER_URL to run the opt-in localization browser flow.");
            }

            _baseUri = new Uri(configuredUrl);
            if (_baseUri.Host != "localhost" && _baseUri.Host != "127.0.0.1" && _baseUri.Host != "::1")
            {
                Assert.Fail("Localization browser tests only run against a loopback app instance.");
            }
        }

        [Test]
        public async Task should_cover_rtl_navigation_forms_tables_dialogs_and_imports()
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            var page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
            });
            var settingsUrl = new Uri(_baseUri, "settings/ui").ToString();

            await page.GotoAsync(settingsUrl);
            var languageControl = page.Locator("#uiLanguage");
            await languageControl.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            var originalLanguage = int.Parse(await languageControl.GetAttributeAsync("data-value") ?? "1");
            Assert.That(await languageControl.GetAttributeAsync("aria-labelledby"), Is.EqualTo("uiLanguage-label"));
            Assert.That(await page.Locator("#uiLanguage-label").CountAsync(), Is.EqualTo(1));
            StringAssert.Contains("uiLanguage-help", await languageControl.GetAttributeAsync("aria-describedby"));
            Assert.That(await page.Locator("#uiLanguage-help").CountAsync(), Is.EqualTo(1));

            try
            {
                var locales = new[]
                {
                    (Name: "العربية — Arabic", Tag: "ar", Id: 31, Direction: "ltr"),
                    (Name: "فارسی — Persian", Tag: "fa", Id: 1003, Direction: "rtl"),
                    (Name: "עברית — Hebrew", Tag: "he", Id: 23, Direction: "rtl")
                };

                await languageControl.FocusAsync();
                await page.Keyboard.PressAsync("ArrowDown");
                Assert.That(await languageControl.GetAttributeAsync("aria-expanded"), Is.EqualTo("true"));
                await page.Keyboard.PressAsync("Escape");
                Assert.That(await languageControl.GetAttributeAsync("aria-expanded"), Is.EqualTo("false"));

                foreach (var locale in locales)
                {
                    await languageControl.ClickAsync();
                    await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = locale.Name }).ClickAsync();
                    await page.WaitForFunctionAsync("direction => document.documentElement.dir === direction", locale.Direction);
                    await page.Keyboard.PressAsync("Control+s");
                    await page.WaitForFunctionAsync("tag => document.documentElement.lang === tag", locale.Tag);
                    await page.WaitForFunctionAsync(
                        "id => document.querySelector('#uiLanguage')?.getAttribute('data-value') === String(id)",
                        locale.Id);
                }

                Assert.That(await page.GetByRole(AriaRole.Combobox).CountAsync(), Is.GreaterThan(0));
                Assert.That(await page.GetByRole(AriaRole.Navigation).CountAsync(), Is.GreaterThan(0));

                await page.GotoAsync(new Uri(_baseUri, "books").ToString());
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                Assert.That(await page.GetByRole(AriaRole.Table).CountAsync(), Is.GreaterThan(0), "Seed the test library with at least one book.");
                Assert.That(await page.Locator("table bdi").CountAsync(), Is.GreaterThan(0), "The seeded book title should be bidi-isolated.");

                var mixedDirectionInput = page.Locator("input[dir='auto']").First;
                await mixedDirectionInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
                const string mixedTitle = "العربية / The Hobbit";
                await mixedDirectionInput.FillAsync(mixedTitle);
                Assert.That(await mixedDirectionInput.InputValueAsync(), Is.EqualTo(mixedTitle));
                await mixedDirectionInput.BlurAsync();

                var longMixedTitle = string.Concat(Enumerable.Repeat("עברית / The Hobbit — ", 24));
                await mixedDirectionInput.FillAsync(longMixedTitle);
                Assert.That(await mixedDirectionInput.InputValueAsync(), Is.EqualTo(longMixedTitle));
                await mixedDirectionInput.BlurAsync();

                await page.Keyboard.PressAsync("?");
                var dialog = page.GetByRole(AriaRole.Dialog);
                await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
                Assert.That(await dialog.GetAttributeAsync("aria-labelledby"), Is.Not.Null);
                Assert.That(await dialog.Locator("button[aria-label]").CountAsync(), Is.GreaterThan(0));
                await page.Keyboard.PressAsync("Escape");

                await page.GotoAsync(settingsUrl);
                await languageControl.ClickAsync();
                await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "العربية — Arabic" }).ClickAsync();
                await page.Keyboard.PressAsync("Control+s");
                await page.WaitForFunctionAsync("tag => document.documentElement.lang === tag", "ar");
                await page.WaitForFunctionAsync(
                    "id => document.querySelector('#uiLanguage')?.getAttribute('data-value') === String(id)",
                    31);

                await page.GotoAsync(new Uri(_baseUri, "unmapped").ToString());
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                await page.WaitForFunctionAsync("() => document.documentElement.dir === 'ltr'");
                Assert.That(await page.GetByRole(AriaRole.Navigation).CountAsync(), Is.GreaterThan(0));

                var importButtons = page.GetByRole(
                    AriaRole.Button,
                    new PageGetByRoleOptions { Name = "الواردات التفاعلية" });
                Assert.That(await importButtons.CountAsync(), Is.GreaterThan(0), "Seed the test library with at least one unmapped file.");
                await importButtons.First.ClickAsync();
                var importDialog = page.GetByRole(AriaRole.Dialog);
                await importDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
                Assert.That(await importDialog.GetAttributeAsync("aria-labelledby"), Is.Not.Null);
                await page.Keyboard.PressAsync("Escape");

                await page.GotoAsync(settingsUrl);
                await page.SetViewportSizeAsync(640, 900);
                await page.EvaluateAsync("() => { document.documentElement.style.fontSize = '200%'; }");
                var overflows = await page.EvaluateAsync<bool>(
                    "() => { const form = document.querySelector('#uiSettings'); return !!form && form.scrollWidth > form.clientWidth + 2; }");
                Assert.That(overflows, Is.False, "The UI settings form should reflow at narrow width and 200% text size.");
            }
            finally
            {
                await page.GotoAsync(settingsUrl);
                var language = page.Locator("#uiLanguage");
                await language.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
                var current = await language.GetAttributeAsync("data-value");
                if (current != originalLanguage.ToString())
                {
                    await language.ClickAsync();
                    await page.Locator("[role='option'][id='" + originalLanguage + "']").ClickAsync();
                    await page.Keyboard.PressAsync("Control+s");
                    await page.WaitForFunctionAsync(
                        "id => document.querySelector('#uiLanguage')?.getAttribute('data-value') === String(id)",
                        originalLanguage);
                }
            }
        }
    }
}
