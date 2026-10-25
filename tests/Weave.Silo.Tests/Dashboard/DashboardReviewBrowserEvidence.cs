using System.Text.Json;
using Microsoft.Playwright;

namespace Weave.Silo.Tests.Dashboard;

internal sealed class DashboardReviewBrowserEvidence(IPage page, string temporaryRoot, bool mobile)
{
    private readonly List<(string State, JsonElement Layout)> _layouts = [];
    private int _images;

    public async Task CaptureAsync(string state, bool reviewPage = true)
    {
        var layout = await page.EvaluateAsync<JsonElement>("""
            reviewPage => {
              const content = document.querySelector(reviewPage ? '.review-page' : 'main');
              const footer = document.querySelector('.footer');
              const box = element => { const r = element.getBoundingClientRect();
                return { left: r.left, right: r.right, top: r.top + scrollY, bottom: r.bottom + scrollY }; };
              const elements = [...content.querySelectorAll('input, button, label, pre, code, a, fluent-anchor, fluent-card')];
              return {
                reviewPage,
                viewportWidth: innerWidth, viewportHeight: innerHeight,
                documentWidth: document.documentElement.scrollWidth,
                documentHeight: document.documentElement.scrollHeight,
                content: box(content), footer: box(footer),
                formDisplay: reviewPage ? getComputedStyle(content.querySelector('.inputs')).display : null,
                maxWidth: reviewPage ? getComputedStyle(content).maxWidth : null,
                clipped: elements.filter(e => { const r = box(e); return r.left < -1 || r.right > innerWidth + 1; })
                  .map(e => e.id || e.tagName),
                capabilityLength: reviewPage ? document.querySelector('#review-capability').value.length : 0,
                tokenLength: reviewPage ? document.querySelector('#review-api-token').value.length : 0,
                touchPoints: navigator.maxTouchPoints
              };
            }
            """, reviewPage);
        _layouts.Add((state, layout));
        var directory = Path.Join(Environment.GetEnvironmentVariable("WEAVE_DASHBOARD_EVIDENCE")
            ?? Path.Join(temporaryRoot, "evidence"), mobile ? "mobile" : "desktop");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Join(directory, state + "-layout.json"), layout.GetRawText(),
            TestContext.Current.CancellationToken);

        var height = layout.GetProperty("documentHeight").GetInt32();
        var viewportHeight = layout.GetProperty("viewportHeight").GetInt32();
        height.ShouldBeInRange(1, 10_000);
        layout.GetProperty("documentWidth").GetInt32().ShouldBeInRange(1, 2000);
        var count = mobile ? (int)Math.Ceiling((double)height / viewportHeight) : 1;
        (_images + count).ShouldBeLessThanOrEqualTo(24, "Bound screenshot output per owned fixture.");
        var scroll = await page.EvaluateAsync<int[]>("() => [scrollX, scrollY]");
        var positions = new List<int[]>();
        try
        {
            for (var index = 0; index < count; index++)
            {
                if (mobile)
                    await page.EvaluateAsync("""
                        async y => {
                          window.scrollTo({left: 0, top: y, behavior: 'instant'});
                          // A focused input can finish a queued scroll on the next
                          // animation frame even after an instant scroll returns.
                          await new Promise(resolve => requestAnimationFrame(resolve));
                          if (window.scrollY !== y)
                            window.scrollTo({left: 0, top: y, behavior: 'instant'});
                        }
                        """, Math.Min(index * viewportHeight, height - viewportHeight));
                positions.Add(await page.EvaluateAsync<int[]>("() => [scrollX, scrollY]"));
                var bytes = await page.ScreenshotAsync(new()
                {
                    FullPage = !mobile,
                    Mask = [page.Locator("#review-capability"), page.Locator("#review-api-token")]
                });
                if (mobile)
                    (await page.EvaluateAsync<int>("window.scrollY")).ShouldBe(positions[^1][1],
                        "Masked screenshot capture must not shift away from the recorded tile.");
                bytes.Length.ShouldBeLessThanOrEqualTo(4 * 1024 * 1024);
                var name = mobile ? $"{state}-tile-{index + 1:D2}.png" : state + ".png";
                await File.WriteAllBytesAsync(Path.Join(directory, name), bytes, TestContext.Current.CancellationToken);
                _images++;
            }
            (await page.EvaluateAsync<int>("document.documentElement.scrollHeight")).ShouldBe(height,
                "The complete screenshot sequence must cover one stable document.");
            (await page.EvaluateAsync<int>("innerWidth")).ShouldBe(layout.GetProperty("viewportWidth").GetInt32());
            (await page.EvaluateAsync<int>("innerHeight")).ShouldBe(viewportHeight);
            (await page.EvaluateAsync<int>("navigator.maxTouchPoints")).ShouldBe(layout.GetProperty("touchPoints").GetInt32());
            if (mobile)
            {
                positions[0][1].ShouldBe(0);
                for (var index = 1; index < positions.Count; index++)
                    positions[index][1].ShouldBeLessThanOrEqualTo(positions[index - 1][1] + viewportHeight);
                (positions[^1][1] + viewportHeight).ShouldBeGreaterThanOrEqualTo(height);
            }
        }
        finally
        {
            await page.EvaluateAsync("position => window.scrollTo({left: position[0], top: position[1], behavior: 'instant'})", scroll);
        }
        await File.WriteAllTextAsync(Path.Join(directory, state + "-tiles.json"), JsonSerializer.Serialize(positions),
            TestContext.Current.CancellationToken);
    }

    public void AssertLayouts()
    {
        _layouts.ShouldNotBeEmpty();
        foreach (var (state, layout) in _layouts)
        {
            layout.GetProperty("viewportWidth").GetInt32().ShouldBe(mobile ? 390 : 1440, state);
            layout.GetProperty("viewportHeight").GetInt32().ShouldBe(mobile ? 844 : 1000, state);
            if (mobile)
                layout.GetProperty("touchPoints").GetInt32().ShouldBeGreaterThan(0, state);
            if (layout.GetProperty("reviewPage").GetBoolean())
            {
                layout.GetProperty("formDisplay").GetString().ShouldBe("grid", state + ": application styles must load.");
                layout.GetProperty("maxWidth").GetString().ShouldBe("980px", state);
            }
            layout.GetProperty("documentWidth").GetInt32().ShouldBeLessThanOrEqualTo(
                layout.GetProperty("viewportWidth").GetInt32() + 1, state + ": no horizontal document overflow.");
            layout.GetProperty("clipped").GetArrayLength().ShouldBe(0, state + ": controls and review text must fit the viewport.");
            layout.GetProperty("footer").GetProperty("top").GetDouble().ShouldBeGreaterThanOrEqualTo(
                layout.GetProperty("content").GetProperty("bottom").GetDouble() - 1, state + ": footer must follow the complete content.");
        }
    }
}
