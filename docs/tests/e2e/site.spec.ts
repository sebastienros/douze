import { expect, test, type Page } from "@playwright/test";
import { readFile } from "node:fs/promises";
import path from "node:path";

// Every url of the sidebar (docs/_data/sidebar.yml), read without a YAML parser: they are the lines that hold a
// /douze/ link.
async function sidebarUrls(): Promise<string[]> {
  const yaml = await readFile(path.join(import.meta.dirname, "../../_data/sidebar.yml"), "utf8");
  return [...new Set([...yaml.matchAll(/(\/douze\/[a-z0-9/-]*\/)/g)].map((m) => m[1]))];
}

const isMobile = (page: Page) => (page.viewportSize()?.width ?? 1000) < 960;

// Records what the page copies, without the browser's clipboard permissions.
async function stubClipboard(page: Page) {
  await page.addInitScript(() => {
    (window as any).__copied = [];
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText: async (text: string) => { (window as any).__copied.push(text); } }
    });
  });
}

const copied = (page: Page) => page.evaluate(() => (window as any).__copied as string[]);

test.describe("home", () => {
  test("renders the hero, the twelve features and links into the docs", async ({ page }) => {
    await page.goto("/douze/");
    await expect(page).toHaveTitle(/Douze/);
    await expect(page.locator(".hero__title")).toContainText("Markdown in");
    await expect(page.locator(".twelve__grid > li")).toHaveCount(12);

    await page.getByRole("link", { name: "Build your first site" }).click();
    await expect(page).toHaveURL(/\/douze\/tutorial\/$/);
    await expect(page.locator("h1")).toBeVisible();
  });

  test("the install command can be copied", async ({ page }) => {
    await stubClipboard(page);
    await page.goto("/douze/");
    await page.locator("[data-copy]").click();
    await expect(page.locator("[data-copy]")).toHaveText("Copied");
    expect((await copied(page))[0]).toContain("install.sh");
  });

  test("section titles are centered, without heading anchors", async ({ page }) => {
    await page.goto("/douze/");
    await expect(page.locator(".home .heading-anchor")).toHaveCount(0);
    for (const id of ["twelve-title", "numbers-title"]) {
      const offset = await page.evaluate((id) => {
        const h = document.getElementById(id)!;
        const box = h.getBoundingClientRect();
        const range = document.createRange();
        range.selectNodeContents(h);
        // Every line of the title is centered in the heading's box.
        return Math.max(...[...range.getClientRects()].map((r) => Math.abs((r.left + r.right) / 2 - (box.left + box.right) / 2)));
      }, id);
      expect(offset, id).toBeLessThan(4);
    }
  });

  test("the root redirects to /douze/", async ({ page }) => {
    await page.goto("/");
    await expect(page).toHaveURL(/\/douze\/$/);
  });
});

test.describe("every page of the sidebar", () => {
  test("loads with a title, its sidebar entry active, and no template leftovers", async ({ page }) => {
    test.slow();
    for (const url of await sidebarUrls()) {
      const response = await page.goto(url);
      expect(response?.status(), url).toBe(200);
      await expect(page.locator("h1.doc__title"), url).not.toBeEmpty();
      // The text of the page outside code: Liquid or a container marker there was not rendered.
      const prose = await page.evaluate(() => {
        const clone = document.querySelector(".doc__article")!.cloneNode(true) as HTMLElement;
        clone.querySelectorAll("pre, code, figure.code").forEach((e) => e.remove());
        return clone.textContent ?? "";
      });
      expect(prose, `${url} shows Liquid`).not.toMatch(/\{%-?\s|\{\{-?\s/);
      expect(prose, `${url} shows an unconverted container`).not.toMatch(/^\s*:::/m);
      await expect(page.locator(`.sidebar a[href="${url}"]`).first(), url).toHaveAttribute("aria-current", "page");
    }
  });
});

test.describe("documentation layout", () => {
  test("has previous and next links inside a group", async ({ page }) => {
    await page.goto("/douze/tutorial/2-pages/");
    await expect(page.locator(".pager__link--prev")).toHaveAttribute("href", "/douze/tutorial/1-setup/");
    await expect(page.locator(".pager__link--next")).toHaveAttribute("href", "/douze/tutorial/3-includes-and-shortcodes/");
  });

  test("highlights code and copies it", async ({ page }) => {
    await stubClipboard(page);
    await page.goto("/douze/guide/configuration/");
    const block = page.locator("figure.code").first();
    await expect(block).toBeVisible();
    await expect(block.locator("span[style*='--0']").first()).toBeAttached();
    await block.hover();
    await block.locator(".code-copy").click();
    await expect(block.locator(".code-copy")).toHaveText("Copied");
    expect((await copied(page))[0].length).toBeGreaterThan(0);
  });

  test("headings have anchors and the outline links to them", async ({ page }) => {
    test.skip(isMobile(page), "the outline is hidden on small screens");
    await page.goto("/douze/guide/collections/");
    const first = page.locator(".outline a").first();
    await expect(first).toBeVisible();
    const hash = await first.getAttribute("href");
    await first.click();
    await expect(page).toHaveURL(new RegExp(`${hash}$`));
    await expect(page.locator(`[id="${hash!.slice(1)}"] .heading-anchor`)).toBeAttached();
    // The anchor's label is for screen readers: it takes no room in the heading.
    const label = await page.locator(".heading-anchor .sr-only").first().boundingBox();
    expect(label!.width).toBeLessThanOrEqual(1);
  });

  test("the theme toggle switches and remembers the theme", async ({ page }) => {
    await page.goto("/douze/guide/why-douze/");
    const before = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    await page.locator("[data-theme-toggle]").click();
    const theme = await page.evaluate(() => document.documentElement.dataset.theme);
    expect(["light", "dark"]).toContain(theme);
    const after = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    expect(after).not.toBe(before);
    await page.reload();
    expect(await page.evaluate(() => document.documentElement.dataset.theme)).toBe(theme);
  });

  test("tabs switch panels and sync by label", async ({ page }) => {
    await page.goto("/douze/guide/install/");
    const tabs = page.locator("[data-tabs].is-ready").first();
    await expect(tabs).toBeVisible();
    const buttons = tabs.getByRole("tab");
    expect(await buttons.count()).toBeGreaterThan(1);
    await buttons.nth(1).click();
    await expect(buttons.nth(1)).toHaveAttribute("aria-selected", "true");
    const panels = tabs.locator(":scope > .tab");
    await expect(panels.nth(1)).toBeVisible();
    await expect(panels.nth(0)).toBeHidden();
  });

  test("the mobile menu opens the sidebar", async ({ page }) => {
    test.skip(!isMobile(page), "mobile only");
    await page.goto("/douze/reference/cli/");
    await expect(page.locator(".sidebar")).not.toBeInViewport();
    await page.locator("[data-menu]").click();
    await expect(page.locator(".sidebar")).toBeInViewport();
    await page.locator(".sidebar a", { hasText: "douze.yml" }).first().click();
    await expect(page).toHaveURL(/\/douze\/reference\/configuration\/$/);
  });

  test("pages do not scroll sideways on a phone", async ({ page }) => {
    test.skip(!isMobile(page), "mobile only");
    for (const url of ["/douze/", "/douze/guide/benchmarks/", "/douze/reference/steps/highlight/", "/douze/tutorial/1-setup/"]) {
      await page.goto(url);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, url).toBeLessThanOrEqual(1);
    }
  });
});

test.describe("search", () => {
  test("finds pages with Pagefind", async ({ page }) => {
    await page.goto("/douze/guide/why-douze/");
    await page.keyboard.press("/");
    const input = page.locator(".search-dialog input");
    await expect(input).toBeVisible();
    await input.fill("pagination");
    const result = page.locator(".pagefind-ui__result-link").first();
    await expect(result).toBeVisible();
    await result.click();
    await expect(page).toHaveURL(/\/douze\//);
    await expect(page.locator("h1")).toBeVisible();
  });
});

test("unknown pages show the 404 page", async ({ page }) => {
  const response = await page.goto("/douze/does-not-exist/");
  expect(response?.status()).toBe(404);
  await expect(page.locator("h1")).toHaveText("Page not found");
});

test("no console errors on the main pages", async ({ page }) => {
  const errors: string[] = [];
  page.on("console", (m) => { if (m.type() === "error") errors.push(m.text()); });
  page.on("pageerror", (e) => errors.push(e.message));
  for (const url of ["/douze/", "/douze/tutorial/", "/douze/guide/why-douze/", "/douze/reference/steps/"]) {
    await page.goto(url);
  }
  expect(errors).toEqual([]);
});
